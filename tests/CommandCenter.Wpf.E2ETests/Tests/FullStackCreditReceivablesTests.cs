using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Npgsql;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

/// <summary>
/// WU4 (8.189): vertical de Crédito/CxC contra el stack real. Emite dos obligaciones en USD con
/// cupo autorizado, cambia la tasa BCV, registra un abono FIFO de $20 y verifica en PostgreSQL:
/// imputación 13/7, saldo remanente 6, diferencial cambiario y el ingreso a caja a la tasa del día.
/// El grid de CxC no es automatizable por UIA (PerformantDataGrid con peer nulo), por lo que la
/// acción de cobro se ejerce contra el MISMO endpoint del botón y la UI solo se usa para verificar
/// los totales reactivos del panel.
/// </summary>
public class FullStackCreditReceivablesTests : IClassFixture<FullStackFixture>
{
    private const decimal ProductCostUsd = 10.00m;
    private const decimal RetailMarginPercent = 30.00m;
    private const decimal PaymentUsd = 20.00m;

    private readonly FullStackFixture _fixture;

    public FullStackCreditReceivablesTests(FullStackFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreditSales_AfterRateChange_AndFifoPayment_PersistBalancesAndCash()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var timeout = UiaRetry.FindTimeout;
        await _fixture.InitializeAsync();

        try
        {
            var bootstrap = _fixture.Bootstrap
                ?? throw new InvalidOperationException("El bootstrap del harness no produjo estado inicial.");
            var adminToken = _fixture.AdminToken
                ?? throw new InvalidOperationException("El harness no expuso el token del admin.");

            using var api = new E2eApiClient(_fixture.BackendBaseAddress);
            int productId = await api.GetProductIdBySkuAsync(adminToken, bootstrap.ProductSku);

            string suffix = Random.Shared.Next(10_000_000, 99_999_999).ToString(CultureInfo.InvariantCulture);
            int customerId = await api.CreateCustomerAsync(
                adminToken,
                $"V-{suffix}",
                $"Cliente Crédito {suffix}",
                $"0414{suffix[..7]}");
            await api.UpdateCreditProfileAsync(adminToken, customerId, creditLimitUsd: 100m);

            decimal initialRate = bootstrap.ExchangeRate;
            var saleIds = new List<int>();
            for (int i = 0; i < 2; i++)
            {
                int saleId = await api.StartSaleAsync(adminToken);
                await api.SetSaleCustomerAsync(adminToken, saleId, customerId);
                await api.AddItemAsync(adminToken, saleId, productId, quantity: 1m, exchangeRate: initialRate);
                await api.CompleteCreditSaleAsync(adminToken, saleId, initialRate);
                saleIds.Add(saleId);
            }

            decimal newRate = await api.SetExchangeRateAsync(adminToken, initialRate + 10m);
            await api.RegisterCreditPaymentAsync(adminToken, customerId, PaymentUsd);

            decimal unitPriceUsd = Math.Ceiling(ProductCostUsd * (1m + (RetailMarginPercent / 100m)) * 100m) / 100m;

            await AssertCreditPersisted(customerId, saleIds, unitPriceUsd, newRate);

            // La UI real refleja el saldo remanente ($26 - $20 = $6) y una sola obligación abierta.
            var window = _fixture.ClientWindow
                ?? throw new InvalidOperationException("El cliente WPF no se lanzó contra el harness full-stack.");
            TestHelper.EnsureLoggedInWithCredentials(window, _fixture.AdminUsername, _fixture.AdminPassword);
            NavigateToView(window, "Nav_BtnCreditReceivables", timeout);

            var totalOpen = WaitForElement(window, "CreditReceivables_TotalOpen", timeout)
                ?? throw new InvalidOperationException("El panel CxC no expuso el total abierto.");
            Assert.Equal(6m, ParseUiNumber(totalOpen.Name));

            var openCount = WaitForElement(window, "CreditReceivables_OpenCount", timeout)
                ?? throw new InvalidOperationException("El panel CxC no expuso el conteo de obligaciones.");
            Assert.Equal(1m, ParseUiNumber(openCount.Name));

            AssertNoErrorDialogs();
        }
        finally
        {
            await _fixture.TeardownAsync();
        }
    }

    private async Task AssertCreditPersisted(
        int customerId,
        IReadOnlyList<int> saleIds,
        decimal unitPriceUsd,
        decimal paymentRate)
    {
        await using var connection = new NpgsqlConnection(_fixture.DatabaseConnectionString);
        await connection.OpenAsync();

        await using (var salesCommand = new NpgsqlCommand(
            "SELECT \"TotalUSD\", \"TotalBsS\", \"AppliedRate\" FROM \"Sales\" WHERE \"Id\" = ANY(@ids) ORDER BY \"Id\"",
            connection))
        {
            salesCommand.Parameters.AddWithValue("ids", saleIds.ToArray());
            await using var reader = await salesCommand.ExecuteReaderAsync();
            for (int i = 0; i < saleIds.Count; i++)
            {
                Assert.True(await reader.ReadAsync(), $"La venta a crédito #{saleIds[i]} no quedó persistida.");
                Assert.Equal(unitPriceUsd, reader.GetDecimal(0));

                // El servicio ancla la tasa oficial (SEC-08): la paridad se valida contra la tasa
                // realmente persistida, no contra la tasa de bootstrap.
                decimal appliedRate = reader.GetDecimal(2);
                decimal expectedBsS = Math.Ceiling(unitPriceUsd * appliedRate * 100m) / 100m;
                Assert.Equal(expectedBsS, reader.GetDecimal(1));
            }
        }

        await using (var receivablesCommand = new NpgsqlCommand(
            "SELECT \"BalanceUSD\" FROM \"Receivables\" WHERE \"CustomerId\" = @customer ORDER BY \"Id\"",
            connection))
        {
            receivablesCommand.Parameters.AddWithValue("customer", customerId);
            await using var reader = await receivablesCommand.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0m, reader.GetDecimal(0)); // primera obligación liquidada
            Assert.True(await reader.ReadAsync());
            Assert.InRange(reader.GetDecimal(0), 5.99m, 6.01m); // 26 - 20 (4dp)
            Assert.False(await reader.ReadAsync());
        }

        await using (var allocationsCommand = new NpgsqlCommand(
            "SELECT a.\"AmountUSD\" FROM \"CreditPaymentAllocations\" a " +
            "JOIN \"Receivables\" r ON r.\"Id\" = a.\"ReceivableId\" " +
            "WHERE r.\"CustomerId\" = @customer ORDER BY a.\"Id\"",
            connection))
        {
            allocationsCommand.Parameters.AddWithValue("customer", customerId);
            await using var reader = await allocationsCommand.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            decimal firstAllocation = reader.GetDecimal(0);
            Assert.True(await reader.ReadAsync());
            decimal secondAllocation = reader.GetDecimal(0);
            Assert.False(await reader.ReadAsync());

            // FIFO: el primer documento se liquida (≈13 USD) y el resto va al segundo.
            Assert.InRange(firstAllocation, 12.99m, 13.01m);
            Assert.InRange(firstAllocation + secondAllocation, 19.99m, 20.01m);
        }

        await using (var cashCommand = new NpgsqlCommand(
            "SELECT \"AmountLocal\" FROM \"CashTransactions\" WHERE \"Source\" = 7 ORDER BY \"Id\" DESC LIMIT 1",
            connection))
        {
            var amountLocal = (decimal)(await cashCommand.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("El abono CxC no generó ingreso de caja."));
            Assert.Equal(Math.Round(PaymentUsd * paymentRate, 2, MidpointRounding.AwayFromZero), amountLocal);
        }
    }

    private static AutomationElement? WaitForElement(Window window, string automationId, TimeSpan timeout)
    {
        var result = Retry.WhileNull(
            () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))),
            timeout);
        return result.Result;
    }

    private static void NavigateToView(Window window, string navAutomationId, TimeSpan timeout)
    {
        if (TestHelper.NavigateTo(window, navAutomationId))
        {
            return;
        }

        var openMenuButton = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_OpenMenuButton"))?.AsButton();
        if (openMenuButton is { IsEnabled: true })
        {
            openMenuButton.Invoke();
        }

        if (TestHelper.NavigateTo(window, navAutomationId))
        {
            return;
        }

        var button = WaitForElement(window, navAutomationId, timeout)?.AsButton()
            ?? throw new InvalidOperationException($"No se encontró el botón de navegación '{navAutomationId}'.");
        button.Invoke();
    }

    private void AssertNoErrorDialogs()
    {
        var dialogs = _fixture.FindErrorDialogs();
        Assert.True(dialogs.Length == 0, $"Diálogos de error inesperados en CxC: {string.Join(" | ", dialogs)}");
    }

    private static decimal ParseUiNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0m;
        }

        var match = Regex.Match(text, @"\d[\d.,]*");
        if (!match.Success)
        {
            return 0m;
        }

        var digits = match.Value;
        int lastComma = digits.LastIndexOf(',');
        int lastDot = digits.LastIndexOf('.');

        if (lastComma >= 0 && lastDot >= 0)
        {
            digits = lastComma > lastDot
                ? digits.Replace(".", string.Empty).Replace(',', '.')
                : digits.Replace(",", string.Empty);
        }
        else if (lastDot >= 0 && digits.Length - lastDot - 1 == 3)
        {
            digits = digits.Replace(".", string.Empty);
        }
        else if (lastDot < 0)
        {
            digits = digits.Replace(',', '.');
        }

        return decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;
    }
}
