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
            "Nav_BtnSupplierInvoices",
            "Nav_BtnPos"
        };

        var failures = new List<string>();
        foreach (var navId in views)
        {
            if (!TestHelper.NavigateTo(window, navId))
            {
                failures.Add($"{navId}: no se pudo invocar la navegación (botón ausente o deshabilitado)");
                break;
            }

            // Deja completar la carga asíncrona de la vista (los mocks responden al instante,
            // pero la UI procesa el dispatcher en el hilo de WPF).
            System.Threading.Thread.Sleep(1500);

            // 8.144: un crash de la app desmontaba el test en silencio (las navegaciones
            // siguientes fallaban sin diálogo de error). Si el proceso murió, fallar aquí.
            if (_fixture.App == null || _fixture.App.HasExited)
            {
                failures.Add($"{navId}: la aplicación se cerró durante la navegación (crash)");
                break;
            }

            // 8.144: la vista de facturas debe renderizar su título; sin esta marca un fallo de
            // plantilla la dejaba invisible mientras el test quedaba en verde.
            if (navId == "Nav_BtnSupplierInvoices")
            {
                var title = Retry.WhileNull(
                    () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId("SupplierInvoice_Title"))),
                    TimeSpan.FromSeconds(5));
                if (title.Result == null)
                {
                    failures.Add($"{navId}: la vista no renderizó (SupplierInvoice_Title ausente)");
                    break;
                }
            }

            var dialogs = FindDialogs("CustomDialogWindow_Error");
            if (dialogs.Length > 0)
            {
                failures.Add($"{navId}: {string.Join(", ", dialogs)}");
                break;
            }
        }

        Assert.True(failures.Count == 0, $"Fallos tras navegar las vistas: {string.Join(" | ", failures)}");
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
