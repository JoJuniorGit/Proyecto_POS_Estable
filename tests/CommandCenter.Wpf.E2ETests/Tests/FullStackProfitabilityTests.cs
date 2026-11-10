using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

/// <summary>
/// F5-WU2 (Paso 14): journey visual del dashboard de rentabilidad contra el stack real.
/// Login Admin, navegación por <c>Nav_BtnProfitability</c>, render de KPIs/cobertura/gráfico y
/// toggle Devengado/Percibido/Ambos, con captura en <c>artifacts/profitability-wpf.png</c>.
/// Gateado por <c>E2E_POSTGRES_CONNECTION</c>: sin variable retorna en silencio como el resto
/// del harness.
/// </summary>
public class FullStackProfitabilityTests : IClassFixture<FullStackFixture>
{
    private readonly FullStackFixture _fixture;

    public FullStackProfitabilityTests(FullStackFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ProfitabilityDashboard_RendersKpisCoverageAndToggle_AndCapturesScreenshot()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var findTimeout = UiaRetry.FindTimeout;

        await _fixture.InitializeAsync();

        try
        {
            var window = _fixture.ClientWindow
                ?? throw new InvalidOperationException("El cliente WPF no se lanzó contra el harness full-stack.");

            EnsureLoggedInWithBoundedRetry(window, findTimeout);
            AssertNoErrorDialogs();

            NavigateToView(window, "Nav_BtnProfitability", findTimeout);

            // La cobertura y el gráfico solo se hacen visibles cuando el summary real llegó
            // (HasData), por lo que su aparición certifica el render con datos del servidor.
            var kpiAccrued = WaitForElement(window, "Profitability_KpiAccrued", findTimeout)
                ?? throw new InvalidOperationException($"La KPI devengada no apareció. {DescribeUi(window)}");
            var kpiRealized = WaitForElement(window, "Profitability_KpiRealized", findTimeout)
                ?? throw new InvalidOperationException($"La KPI percibida no apareció. {DescribeUi(window)}");
            var coverage = WaitForElement(window, "Profitability_Coverage", findTimeout)
                ?? throw new InvalidOperationException($"El banner de cobertura no apareció. {DescribeUi(window)}");
            var seriesChart = WaitForElement(window, "Profitability_SeriesChart", findTimeout)
                ?? throw new InvalidOperationException($"El gráfico de series no apareció. {DescribeUi(window)}");

            Assert.False(string.IsNullOrWhiteSpace(kpiAccrued.Name), "La KPI devengada está vacía.");
            Assert.False(string.IsNullOrWhiteSpace(kpiRealized.Name), "La KPI percibida está vacía.");
            Assert.Contains("Cobertura de datos", coverage.Name, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Serie temporal", seriesChart.Name, StringComparison.OrdinalIgnoreCase);

            var texts = ReadAllTexts(window);
            Assert.Contains(texts, text => text.Contains("Rentabilidad", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(texts, text => text.Contains("UTILIDAD DEVENGADA", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.Contains("UTILIDAD PERCIBIDA EN CAJA", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.Contains("Costo:", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(texts, text => text.Contains("Comisiones:", StringComparison.OrdinalIgnoreCase));

            // La exportación CSV comparte el guard CanExport del summary cargado.
            var exportCsv = WaitForEnabledButton(window, "Profitability_ExportCsv", findTimeout)
                ?? throw new InvalidOperationException($"El botón CSV no se habilitó con datos. {DescribeUi(window)}");
            Assert.True(exportCsv.IsEnabled, "El botón CSV debería estar habilitado.");

            // Toggle real: Percibido y Ambos. La vista no expone un estado UIA "selected", así que
            // la aserción automatizada confirma que la acción se invoca y el dashboard sigue vivo;
            // la captura es la evidencia visual del modo activo.
            var realizedToggle = WaitForEnabledButton(window, "Profitability_ToggleRealized", findTimeout)
                ?? throw new InvalidOperationException($"El toggle Percibido no apareció. {DescribeUi(window)}");
            realizedToggle.Invoke();
            Assert.True(
                WaitForElement(window, "Profitability_Coverage", findTimeout) != null,
                "El dashboard debe seguir visible tras cambiar a Percibido.");
            AssertNoErrorDialogs();

            var bothToggle = WaitForEnabledButton(window, "Profitability_ToggleBoth", findTimeout)
                ?? throw new InvalidOperationException($"El toggle Ambos no apareció. {DescribeUi(window)}");
            bothToggle.Invoke();
            Assert.True(
                WaitForElement(window, "Profitability_SeriesChart", findTimeout) != null,
                "El gráfico debe seguir visible tras volver a Ambos.");
            AssertNoErrorDialogs();

            var screenshotPath = Path.Combine(FindRepositoryRoot(), "artifacts", "profitability-wpf.png");
            Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
            try
            {
                window.SetForeground();
            }
            catch (Exception)
            {
                // La captura por elemento no exige foco; se conserva el intento best-effort.
            }

            Thread.Sleep(400);
            window.CaptureToFile(screenshotPath);

            var screenshot = new FileInfo(screenshotPath);
            Assert.True(
                screenshot.Exists && screenshot.Length > 0,
                $"La captura WPF no se generó en '{screenshotPath}'.");
            Console.WriteLine($"[F5] Captura WPF de rentabilidad: {screenshotPath} ({screenshot.Length} bytes)");
        }
        finally
        {
            await _fixture.TeardownAsync();
        }
    }

    /// <summary>
    /// Login real con reintentos acotados (mismo patrón que FullStackCashClosureTests): una
    /// pérdida transitoria de foco o un hipo de UIA deja la vista a medio exponer aunque las
    /// credenciales del harness sean correctas. Cada reintento re-escribe ambos campos; un fallo
    /// real de credenciales vuelve a fallar en el último intento.
    /// </summary>
    private void EnsureLoggedInWithBoundedRetry(Window window, TimeSpan findTimeout)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                WaitForStableLoginView(window);
                TestHelper.EnsureLoggedInWithCredentials(window, _fixture.AdminUsername, _fixture.AdminPassword);
                return;
            }
            catch (InvalidOperationException) when (attempt < maxAttempts && IsLoginViewVisible(window, findTimeout))
            {
                try
                {
                    window.SetForeground();
                }
                catch (Exception)
                {
                    // Activación best-effort.
                }

                Thread.Sleep(500);
            }
        }
    }

    /// <summary>
    /// Exige presencia continua del botón de envío habilitado durante ~1.5 s para no competir
    /// con el churn de reconstrucción de la vista de login durante el arranque real.
    /// </summary>
    private static void WaitForStableLoginView(Window window)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        DateTime? stableSince = null;
        while (DateTime.UtcNow < deadline)
        {
            bool submitReady = UiaRetry.RetryUia(() =>
                window.FindFirstDescendant(cf => cf.ByAutomationId("Login_SubmitButton"))?.AsButton())
                is { IsEnabled: true };

            if (submitReady)
            {
                stableSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - stableSince.Value >= TimeSpan.FromSeconds(1.5))
                {
                    return;
                }
            }
            else
            {
                stableSince = null;
            }

            Thread.Sleep(200);
        }
    }

    private static bool IsLoginViewVisible(Window window, TimeSpan findTimeout)
    {
        var login = Retry.WhileNull(
            () => UiaRetry.RetryUia(() => window.FindFirstDescendant(cf => cf.ByAutomationId("Login_Username"))),
            TimeSpan.FromSeconds(Math.Min(findTimeout.TotalSeconds, 3)));
        return login.Result != null;
    }

    /// <summary>
    /// Navega por la barra lateral. En modo real el DrawerHost derecho arranca CERRADO y
    /// MaterialDesign deshabilita su contenido para UIA; si la navegación estándar falla se abre
    /// con el engranaje del POS y se reintenta dentro del presupuesto.
    /// </summary>
    private static void NavigateToView(Window window, string navAutomationId, TimeSpan findTimeout)
    {
        if (TestHelper.NavigateTo(window, navAutomationId))
        {
            return;
        }

        var navigated = Retry.WhileNull(
            () => UiaRetry.RetryUia(() =>
            {
                var openMenuButton = window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_OpenMenuButton"))?.AsButton();
                if (openMenuButton is { IsEnabled: true })
                {
                    try
                    {
                        openMenuButton.Invoke();
                    }
                    catch (FlaUI.Core.Exceptions.ElementNotEnabledException)
                    {
                        return null;
                    }
                }

                var button = window.FindFirstDescendant(cf => cf.ByAutomationId(navAutomationId))?.AsButton();
                if (button is not { IsEnabled: true })
                {
                    return null;
                }

                try
                {
                    button.Invoke();
                    return button;
                }
                catch (FlaUI.Core.Exceptions.ElementNotEnabledException)
                {
                    return null;
                }
            }),
            findTimeout);

        Assert.True(
            navigated.Result != null,
            $"No se pudo navegar a '{navAutomationId}' por la barra lateral. {DescribeUi(window)}");
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

    private static string[] ReadAllTexts(Window window)
        => UiaRetry.RetryUia(() => window
            .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Select(element => element.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray()) ?? Array.Empty<string>();

    private static string DescribeUi(Window window)
    {
        string ids;
        try
        {
            ids = string.Join(", ", window.FindAllDescendants()
                .Select(element => element.Properties.AutomationId.ValueOrDefault)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .Take(40));
        }
        catch (Exception ex)
        {
            ids = $"(la enumeración UIA falló: {ex.Message})";
        }

        return $"Ids visibles: {ids}";
    }

    private void AssertNoErrorDialogs()
    {
        var dialogs = _fixture.FindErrorDialogs();
        Assert.True(
            dialogs.Length == 0,
            $"Diálogos de error inesperados en el journey de rentabilidad: {string.Join(" | ", dialogs)}");
    }

    private static string FindRepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CommandCenter.slnx")))
        {
            root = root.Parent;
        }

        return root?.FullName ?? AppContext.BaseDirectory;
    }
}
