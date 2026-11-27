using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Npgsql;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

/// <summary>
/// WU5 (8.189): vertical de Catálogo/Precios. Crea un producto real, aplica una edición masiva de
/// margen retail, cambia la tasa BCV por la UI, y verifica que el POS busque el SKU y calcule el
/// precio efectivo Bs.S con la política ToBsSCeiling, además de la persistencia del margen/precio.
/// </summary>
[Collection(FullStackCollection.Name)]
public class FullStackCatalogPricingTests : IClassFixture<FullStackFixture>
{
    private const decimal CostUsd = 10.00m;
    private const decimal InitialMargin = 30.00m;
    private const decimal UpdatedMargin = 40.00m;
    private const decimal NewRate = 777.77m;

    private readonly FullStackFixture _fixture;

    public FullStackCatalogPricingTests(FullStackFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task BulkMargin_AndRateChange_ProjectEffectivePriceInPos()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var timeout = UiaRetry.FindTimeout;
        await _fixture.InitializeAsync();

        try
        {
            var adminToken = _fixture.AdminToken
                ?? throw new InvalidOperationException("El harness no expuso el token del admin.");

            using var api = new E2eApiClient(_fixture.BackendBaseAddress);
            var product = await api.CreateTestProductAsync(adminToken);
            int productId = await api.GetProductIdBySkuAsync(adminToken, product.Sku);

            var initialPrice = Math.Ceiling(CostUsd * (1m + (InitialMargin / 100m)) * 100m) / 100m;
            var updatedPrice = Math.Ceiling(CostUsd * (1m + (UpdatedMargin / 100m)) * 100m) / 100m;

            await AssertProductPriceAsync(productId, initialPrice, InitialMargin);

            await api.ApplyBulkRetailMarginAsync(adminToken, productId, UpdatedMargin);
            await AssertProductPriceAsync(productId, updatedPrice, UpdatedMargin);

            var window = _fixture.ClientWindow
                ?? throw new InvalidOperationException("El cliente WPF no se lanzó contra el harness full-stack.");
            TestHelper.EnsureLoggedInWithCredentials(window, _fixture.AdminUsername, _fixture.AdminPassword);

            SetExchangeRateThroughUi(window, NewRate, timeout);
            NavigateToView(window, "Nav_BtnPos", timeout);

            var rateElement = WaitForElement(window, "Pos_ExchangeRateText", timeout)
                ?? throw new InvalidOperationException("El POS no expuso la tasa vigente.");
            decimal clientRate = ParseUiNumber(rateElement.Name);
            Assert.Equal(NewRate, clientRate);

            AddProductThroughSearch(window, product.Sku, product.Name, timeout);

            var totalElement = WaitForElement(window, "Pos_TotalAmountText", timeout)
                ?? throw new InvalidOperationException("El POS no expuso el total del carrito.");
            decimal expectedBsS = Math.Ceiling(updatedPrice * clientRate * 100m) / 100m;
            Assert.Equal(expectedBsS, ParseUiNumber(totalElement.Name));

            Assert.Empty(_fixture.FindErrorDialogs());
        }
        finally
        {
            await _fixture.TeardownAsync();
        }
    }

    private async Task AssertProductPriceAsync(int productId, decimal expectedPriceUsd, decimal expectedMargin)
    {
        await using var connection = new NpgsqlConnection(_fixture.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"PriceRetailUSD\", \"ProfitMarginRetail\", \"IsActive\", \"IsDeleted\" FROM \"Products\" WHERE \"Id\" = @id",
            connection);
        command.Parameters.AddWithValue("id", productId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "El producto sembrado no existe en la base aislada.");
        Assert.Equal(expectedPriceUsd, reader.GetDecimal(0));
        Assert.Equal(expectedMargin, reader.GetDecimal(1));
        Assert.True(reader.GetBoolean(2), "El producto quedó inactivo tras la edición masiva de margen.");
        Assert.False(reader.GetBoolean(3), "El producto quedó marcado como eliminado tras la edición masiva de margen.");
    }

    private static void SetExchangeRateThroughUi(Window window, decimal rate, TimeSpan timeout)
    {
        NavigateToView(window, "Nav_BtnExchangeRate", timeout);

        var input = WaitForElement(window, "ExchangeRate_RateInput", timeout)?.AsTextBox()
            ?? throw new InvalidOperationException("La vista de tasa no expuso su input.");

        string desired = rate.ToString("0.00", CultureInfo.InvariantCulture);
        var settled = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                input.Text = desired;
                return string.Equals(input.Text?.Trim(), desired, StringComparison.Ordinal) ? desired : null;
            }),
            timeout);
        Assert.True(settled.Result != null, "No se pudo fijar la tasa en la vista de cambio.");

        var save = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var button = window.FindFirstDescendant(cf => cf.ByAutomationId("ExchangeRate_SaveButton"))?.AsButton();
                return button is { IsEnabled: true } ? button : null;
            }),
            timeout).Result ?? throw new InvalidOperationException("El botón Guardar de la tasa no se habilitó.");
        save.Invoke();

        var confirmed = Retry.WhileNull(
            () => UiaRetry.RetryUia(() => window
                .FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
                .Select(text => text.Name)
                .FirstOrDefault(name => name?.Contains("actualizada correctamente", StringComparison.OrdinalIgnoreCase) == true)),
            timeout);
        Assert.True(confirmed.Result != null, "La UI no confirmó el guardado de la tasa.");
    }

    private static void AddProductThroughSearch(Window window, string sku, string productName, TimeSpan timeout)
    {
        var search = WaitForElement(window, "Pos_SearchInput", timeout)?.AsTextBox()
            ?? throw new InvalidOperationException("El buscador del POS no apareció.");
        search.Text = sku;

        bool dispatched = false;
        // Wait for the suggestions popup: Enter is only pressed once the list is open and the
        // first item is not actionable, so the single commit is never consumed too early.
        using var automation = new UIA3Automation();
        var row = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var grid = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_CartGrid"));
                var existing = grid?.FindFirstDescendant(cf => cf.ByName(productName));
                if (existing != null)
                {
                    return existing;
                }

                if (dispatched)
                {
                    return null;
                }

                // La vista puede reiniciar el buscador al terminar de inicializar la venta;
                // se re-escribe el SKU para relanzar la búsqueda sin despachar un segundo commit.
                if (!string.Equals(search.Text, sku, StringComparison.Ordinal))
                {
                    search.Text = sku;
                }

                var list = FindSuggestionsList(window, automation);
                if (list == null)
                {
                    return null;
                }

                var suggestion = list.FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem))
                    .FirstOrDefault();
                if (suggestion == null)
                {
                    // El popup puede abrirse antes de que lleguen los resultados: no consumir el
                    // único commit; se espera a que el primer ítem esté materializado.
                    return null;
                }

                try
                {
                    window.SetForeground();
                }
                catch (Exception)
                {
                }

                try
                {
                    suggestion.AsListBoxItem().Select();
                    suggestion.Click();
                    dispatched = true;
                    return null;
                }
                catch (FlaUI.Core.Exceptions.ElementNotAvailableException)
                {
                }
                catch (FlaUI.Core.Exceptions.NoClickablePointException)
                {
                }

                try
                {
                    window.SetForeground();
                }
                catch (Exception)
                {
                }

                search.Focus();
                Keyboard.Press(VirtualKeyShort.RETURN);
                dispatched = true;
                return null;
            }),
            timeout);

        if (row.Result == null)
        {
            Console.WriteLine(
                $"[CATALOG-DBG] search='{search.Text}' {DescribeSuggestions(window, automation)} {DescribeCart(window)}");
        }

        Assert.True(row.Result != null, $"El producto '{productName}' no se agregó al carrito desde el POS.");
    }

    private static void NavigateToView(Window window, string navAutomationId, TimeSpan timeout)
    {
        if (TestHelper.NavigateTo(window, navAutomationId))
        {
            return;
        }

        var menu = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_OpenMenuButton"))?.AsButton();
        if (menu is { IsEnabled: true })
        {
            menu.Invoke();
        }

        if (TestHelper.NavigateTo(window, navAutomationId))
        {
            return;
        }

        (WaitForElement(window, navAutomationId, timeout)?.AsButton()
            ?? throw new InvalidOperationException($"No se encontró la navegación '{navAutomationId}'.")).Invoke();
    }

    private static AutomationElement? FindSuggestionsList(Window window, UIA3Automation automation)
    {
        var list = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_SuggestionsList"));
        if (list != null && !IsOffscreenSafe(list))
        {
            return list;
        }

        int processId = window.Properties.ProcessId.ValueOrDefault;
        foreach (var topLevel in automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(processId)))
        {
            list = topLevel.FindFirstDescendant(cf => cf.ByAutomationId("Pos_SuggestionsList"));
            if (list != null && !IsOffscreenSafe(list))
            {
                return list;
            }
        }

        return null;
    }

    private static bool IsOffscreenSafe(AutomationElement element)
    {
        try
        {
            return element.IsOffscreen;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static AutomationElement? WaitForElement(Window window, string automationId, TimeSpan timeout)
    {
        var result = Retry.WhileNull(
            () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))),
            timeout);
        return result.Result;
    }

    private static string DescribeSuggestions(Window window, UIA3Automation automation)
    {
        try
        {
            var list = FindSuggestionsList(window, automation);
            if (list == null)
            {
                return "Sugerencias: (sin popup).";
            }

            var items = list.FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem))
                .Select(item => item.Name)
                .ToArray();
            return $"Sugerencias: [{string.Join(" | ", items)}].";
        }
        catch (Exception ex)
        {
            return $"Sugerencias: error {ex.GetType().Name}.";
        }
    }

    private static string DescribeCart(Window window)
    {
        try
        {
            var grid = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_CartGrid"));
            if (grid == null)
            {
                return "Carrito: (sin grilla).";
            }

            var rows = grid.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem))
                .Select(row => row.Name)
                .ToArray();
            return $"Carrito: [{string.Join(" | ", rows)}].";
        }
        catch (Exception ex)
        {
            return $"Carrito: error {ex.GetType().Name}.";
        }
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
