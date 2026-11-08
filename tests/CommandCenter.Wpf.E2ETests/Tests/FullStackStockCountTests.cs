using System;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

/// <summary>
/// Smoke E2E for the WPF physical stock count: an Admin creates a session, opens it and scans the
/// bootstrap product. The assertion lives on the blind item grid, so a broken client/API contract
/// or a missing navigation entry fails here.
/// </summary>
public sealed class FullStackStockCountTests : IClassFixture<FullStackFixture>
{
    private readonly FullStackFixture _fixture;

    public FullStackStockCountTests(FullStackFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateSession_ScanProduct_ShowsBlindItemInWpf()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        await _fixture.InitializeAsync();

        try
        {
            var bootstrap = _fixture.Bootstrap
                ?? throw new InvalidOperationException("El bootstrap del harness no produjo estado inicial.");
            var window = _fixture.ClientWindow
                ?? throw new InvalidOperationException("El cliente WPF no se lanzó contra el harness full-stack.");
            var timeout = UiaRetry.FindTimeout;

            TestHelper.EnsureLoggedInWithCredentials(window, _fixture.AdminUsername, _fixture.AdminPassword);
            NavigateToView(window, "Nav_BtnStockCount", timeout);

            var create = WaitForEnabledButton(window, "StockCount_CreateButton", timeout)
                ?? throw new InvalidOperationException(
                    $"La vista de conteo no expuso el botón Nueva sesión. {DescribeUi(window)}");
            create.Invoke();

            var nameInput = WaitForElement(window, "StockCount_CreateName", timeout)?.AsTextBox()
                ?? throw new InvalidOperationException(
                    $"El panel de creación no expuso el nombre. {DescribeUi(window)}");
            nameInput.Text = $"Conteo E2E {Guid.NewGuid():N}"[..24];

            var submit = WaitForEnabledButton(window, "StockCount_CreateSubmit", timeout)
                ?? throw new InvalidOperationException(
                    $"El botón Crear sesión no se habilitó. {DescribeUi(window)}");
            submit.Invoke();

            var open = WaitForEnabledButton(window, "StockCount_SessionOpenButton", timeout)
                ?? throw new InvalidOperationException(
                    $"La sesión creada no apareció en la grilla. {DescribeUi(window)}");
            open.Invoke();

            var scanInput = WaitForElement(window, "StockCount_ScanInput", timeout)?.AsTextBox()
                ?? throw new InvalidOperationException(
                    $"La sesión abierta no expuso el escáner. {DescribeUi(window)}");

            // Admin auto-loads the review after opening the session; its grid proves the
            // theoretical/cost contract deserializes on the WPF client.
            _ = WaitForElement(window, "StockCount_ReviewGrid", timeout)
                ?? throw new InvalidOperationException(
                    $"El panel de descuadre no apareció para el Admin. {DescribeUi(window)}");

            scanInput.Text = bootstrap.ProductSku;

            var scanButton = WaitForEnabledButton(window, "StockCount_ScanButton", timeout)
                ?? throw new InvalidOperationException(
                    $"El botón Contar no se habilitó. {DescribeUi(window)}");
            scanButton.Invoke();

            var grid = WaitForElement(window, "StockCount_ItemsGrid", timeout)
                ?? throw new InvalidOperationException(
                    $"La grilla de ítems no apareció. {DescribeUi(window)}");
            var item = Retry.WhileNull(
                () => UiaRetry.RetryUia(() => grid.FindFirstDescendant(cf => cf.ByName(bootstrap.ProductName))),
                timeout).Result;
            Assert.True(
                item is not null,
                $"El producto '{bootstrap.ProductName}' no apareció en la grilla ciega. {DescribeUi(window)}");

            Assert.Empty(_fixture.FindErrorDialogs());
        }
        finally
        {
            await _fixture.TeardownAsync();
        }
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
            ?? throw new InvalidOperationException($"No se encontró la navegación '{navAutomationId}'. {DescribeUi(window)}");
        button.Invoke();
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
