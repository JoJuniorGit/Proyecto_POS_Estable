using System;
using System.Collections.Generic;
using System.Linq;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

/// <summary>
/// Regresión del modo --e2e: la app debe arrancar contra el mock HTTP sin diálogos de error y
/// con la tasa vigente cargada. Antes, los shapes desalineados del mock (start sale, payment
/// methods, exchange-rate, products, history) producían "Error de Venta" / "Error de Conexión"
/// y "Tasa: 0,00" al iniciar sesión y al navegar las vistas.
/// </summary>
public class E2EAppHealthTests : IClassFixture<WpfAppFixture>
{
    private readonly WpfAppFixture _fixture;

    public E2EAppHealthTests(WpfAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void PosView_AfterLogin_LoadsMockBackendWithoutErrorDialogs()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        TestHelper.EnsureLoggedIn(window);

        // El badge de tasa del header muestra "Tasa: 50,00 Bs/$" (o "50.00" según cultura).
        var rateText = Retry.WhileNull(
            () => window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
                .FirstOrDefault(e => (e.Name ?? string.Empty).StartsWith("Tasa", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10));
        Assert.NotNull(rateText.Result);
        Assert.Contains("50", rateText.Result!.Name);

        // La inicialización asíncrona del POS (tasa, métodos de pago, venta) tarda unos segundos;
        // se monitorea hasta 8s para detectar cualquier diálogo de error que aparezca.
        string[] errorDialogs = Array.Empty<string>();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            errorDialogs = FindDialogs("CustomDialogWindow_Error");
            if (errorDialogs.Length > 0)
            {
                break;
            }
            System.Threading.Thread.Sleep(250);
        }

        Assert.True(errorDialogs.Length == 0, $"Diálogos de error inesperados en modo E2E: {string.Join(" | ", errorDialogs)}");
    }

    [Fact]
    public void MainViews_WhenNavigated_DoNotOpenErrorDialogs()
    {
        var window = _fixture.Launch();
        Assert.NotNull(window);

        TestHelper.EnsureLoggedIn(window);

        string[] views =
        {
            "Nav_BtnInventory",
            "Nav_BtnSalesHistory",
            "Nav_BtnPendingOrders",
            "Nav_BtnPendingPickups",
            "Nav_BtnCashDrawer",
            "Nav_BtnDailyClosure",
            "Nav_BtnExchangeRate",
            "Nav_BtnSettings",
            "Nav_BtnUsersManagement",
            "Nav_BtnImportProducts",
            "Nav_BtnPos"
        };

        var failures = new List<string>();
        foreach (var navId in views)
        {
            TestHelper.NavigateTo(window, navId);
            // Deja completar la carga asíncrona de la vista (los mocks responden al instante,
            // pero la UI procesa el dispatcher en el hilo de WPF).
            System.Threading.Thread.Sleep(1500);

            var dialogs = FindDialogs("CustomDialogWindow_Error");
            if (dialogs.Length > 0)
            {
                failures.Add($"{navId}: {string.Join(", ", dialogs)}");
                break;
            }
        }

        Assert.True(failures.Count == 0, $"Diálogos de error tras navegar las vistas: {string.Join(" | ", failures)}");
    }

    private string[] FindDialogs(string automationIdPrefix)
    {
        if (_fixture.App == null)
        {
            return Array.Empty<string>();
        }

        return _fixture.App.GetAllTopLevelWindows(_fixture.Automation)
            .Where(w => (w.Properties.AutomationId.ValueOrDefault ?? string.Empty).StartsWith(automationIdPrefix, StringComparison.Ordinal))
            .Select(w => w.Name)
            .ToArray();
    }
}
