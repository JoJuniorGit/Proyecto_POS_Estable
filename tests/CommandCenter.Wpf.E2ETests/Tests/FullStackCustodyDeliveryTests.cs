using System;
using System.Linq;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

/// <summary>
/// WU2 (8.189): vertical único de custodia/retiros. Siembra un apartado pagado real por API, lo
/// entrega parcialmente por la UI real (modal) y verifica en el servidor que las unidades
/// entregadas quedaron persistidas (TotalUnits=2, DeliveredUnits=1), sin pruebas estáticas ni
/// pasos aislados del mismo flujo.
/// </summary>
public class FullStackCustodyDeliveryTests : IClassFixture<FullStackFixture>
{
    private readonly FullStackFixture _fixture;

    public FullStackCustodyDeliveryTests(FullStackFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PartialPickup_ConfirmedInUi_PersistsDeliveredUnitsOnServer()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var findTimeout = UiaRetry.FindTimeout;
        await _fixture.InitializeAsync();

        try
        {
            var bootstrap = _fixture.Bootstrap
                ?? throw new InvalidOperationException("El bootstrap del harness no produjo estado inicial.");
            var adminToken = _fixture.AdminToken
                ?? throw new InvalidOperationException("El harness no expuso el token del admin.");

            using var api = new E2eApiClient(_fixture.BackendBaseAddress);

            // 1) Siembra real: cliente con cédula/teléfono válidos + apartado pagado al 100%.
            int productId = await api.GetProductIdBySkuAsync(adminToken, bootstrap.ProductSku);
            string suffix = Random.Shared.Next(10_000_000, 99_999_999).ToString();
            int customerId = await api.CreateCustomerAsync(
                adminToken,
                $"V-{suffix}",
                $"Cliente Custodia {suffix}",
                $"0412{suffix[..7]}");

            int saleId = await api.StartSaleAsync(adminToken);
            await api.SetSaleCustomerAsync(adminToken, saleId, customerId);
            await api.AddItemAsync(adminToken, saleId, productId, quantity: 2m, bootstrap.ExchangeRate);
            var preview = await api.PreviewCheckoutAsync(adminToken, saleId, bootstrap.ExchangeRate);
            await api.CompletePendingPickupAsync(adminToken, saleId, bootstrap.ExchangeRate, preview);

            var seeded = await api.GetPendingPickupsAsync(adminToken);
            var seededRow = Assert.Single(seeded, row => row.SaleId == saleId);
            Assert.Equal(2m, seededRow.TotalUnits);
            Assert.Equal(0m, seededRow.DeliveredUnits);
            Assert.Equal("PendingPickup", seededRow.DeliveryStatus);

            // 2) UI real: login admin, navegación a custodia y entrega parcial de 1 unidad.
            var window = _fixture.ClientWindow
                ?? throw new InvalidOperationException("El cliente WPF no se lanzó contra el harness full-stack.");

            TestHelper.EnsureLoggedInWithCredentials(window, _fixture.AdminUsername, _fixture.AdminPassword);
            NavigateToView(window, "Nav_BtnPendingPickups", findTimeout);

            var confirmButton = WaitForEnabledButton(window, "PendingPickups_ConfirmButton", findTimeout)
                ?? throw new InvalidOperationException(
                    $"La fila del apartado no expuso el botón de retiro. {DescribeUi(window)}");
            confirmButton.Invoke();

            var quantityInput = WaitForElement(window, "PartialDelivery_QuantityInput", findTimeout)?.AsTextBox()
                ?? throw new InvalidOperationException(
                    $"El modal de retiro no expuso el input de cantidad. {DescribeUi(window)}");

            // El modal precarga la cantidad pendiente acotada; se entrega 1 de las 2 unidades.
            var quantitySettled = Retry.WhileNull(
                () => UiaRetry.RetryUia(() =>
                {
                    quantityInput.Text = "1";
                    return string.Equals(quantityInput.Text?.Trim(), "1", StringComparison.Ordinal) ? "1" : null;
                }),
                findTimeout);
            Assert.True(
                quantitySettled.Result != null,
                $"No se pudo fijar la cantidad a entregar en 1. {DescribeUi(window)}");

            var dialogConfirm = WaitForEnabledButton(window, "PartialDelivery_ConfirmButton", findTimeout)
                ?? throw new InvalidOperationException(
                    $"El botón de confirmación del retiro no se habilitó. {DescribeUi(window)}");
            dialogConfirm.Invoke();

            AssertPartialDeliveryDialogClosed(window, findTimeout);
            DismissPrintOfferIfPresent(window);
            Assert.False(_fixture.App?.HasExited ?? true, "La aplicación se cerró durante el retiro.");

            // 3) Persistencia real: el apartado sigue en custodia con 1 de 2 unidades entregadas.
            var pending = await api.GetPendingPickupsAsync(adminToken);
            var row = Assert.Single(pending, candidate => candidate.SaleId == saleId);
            Assert.Equal(2m, row.TotalUnits);
            Assert.Equal(1m, row.DeliveredUnits);
            Assert.Equal("PartiallyDelivered", row.DeliveryStatus);

            AssertNoErrorDialogs();
        }
        finally
        {
            await _fixture.TeardownAsync();
        }
    }

    private static void AssertPartialDeliveryDialogClosed(Window window, TimeSpan timeout)
    {
        Retry.WhileNotNull(
            () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId("PartialDelivery_ConfirmButton"))),
            timeout);
        Assert.Null(window.FindFirstDescendant(cf => cf.ByAutomationId("PartialDelivery_ConfirmButton")));
    }

    private static void DismissPrintOfferIfPresent(Window window)
    {
        using var automation = new UIA3Automation();
        int processId = window.Properties.ProcessId.ValueOrDefault;

        var printOffer = Retry.WhileNull(
            () => UiaRetry.RetryUia(() => automation.GetDesktop()
                .FindAllChildren(cf => cf.ByProcessId(processId))
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Properties.AutomationId.ValueOrDefault,
                    "CustomDialogWindow_Confirm",
                    StringComparison.Ordinal))),
            TimeSpan.FromSeconds(5));

        if (printOffer.Result is null)
        {
            return;
        }

        printOffer.Result.Focus();
        Keyboard.Press(VirtualKeyShort.ESCAPE);
    }

    private static AutomationElement? WaitForElement(Window window, string automationId, TimeSpan timeout)
    {
        var result = Retry.WhileNull(
            () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))),
            timeout);
        return result.Result;
    }

    private static Button? WaitForEnabledButton(Window window, string automationId, TimeSpan timeout)
    {
        var result = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var button = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))?.AsButton();
                return button is { IsEnabled: true } ? button : null;
            }),
            timeout);
        return result.Result;
    }

    private static void NavigateToView(Window window, string navAutomationId, TimeSpan findTimeout)
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

        var button = WaitForElement(window, navAutomationId, findTimeout)?.AsButton()
            ?? throw new InvalidOperationException(
                $"No se encontró el botón de navegación '{navAutomationId}'. {DescribeUi(window)}");
        button.Invoke();
    }

    private void AssertNoErrorDialogs()
    {
        var dialogs = _fixture.FindErrorDialogs();
        Assert.True(
            dialogs.Length == 0,
            $"Diálogos de error inesperados durante el retiro: {string.Join(" | ", dialogs)}");
    }

    private static string DescribeUi(Window window)
    {
        try
        {
            var ids = string.Join(", ", window.FindAllDescendants()
                .Select(element => element.Properties.AutomationId.ValueOrDefault)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .Take(40));
            return $"Ids visibles: {ids}";
        }
        catch (Exception ex)
        {
            return $"La enumeración UIA falló: {ex.Message}";
        }
    }
}
