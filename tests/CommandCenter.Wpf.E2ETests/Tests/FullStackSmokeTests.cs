using System;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Wpf.E2ETests.Fixtures;
using FlaUI.Core.Tools;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

/// <summary>
/// Smoke del harness full-stack (S1): boot real (backend + PostgreSQL aislada + bootstrap por API),
/// login real por UI con la clave rotada, POS listo y teardown limpio. Gateado por
/// <c>E2E_POSTGRES_CONNECTION</c>: local sin variable retorna en silencio; en CI falla cerrado.
/// </summary>
public class FullStackSmokeTests : IClassFixture<FullStackFixture>
{
    private readonly FullStackFixture _fixture;

    public FullStackSmokeTests(FullStackFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task HarnessBoots_BootstrapsAndLogsInRealClient_ThenTearsDownCleanly()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        await _fixture.InitializeAsync();

        try
        {
            var window = _fixture.ClientWindow
                ?? throw new InvalidOperationException("El cliente WPF no se lanzó contra el harness full-stack.");
            Assert.NotNull(_fixture.Bootstrap);

            // Login real: la UI usa la clave rotada por el bootstrap (el admin semilla nace forzado a cambiarla).
            TestHelper.EnsureLoggedInWithCredentials(window, _fixture.AdminUsername, _fixture.AdminPassword);

            // POS listo: el buscador de productos es la marca estable de la vista principal.
            var searchInput = Retry.WhileNull(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("Pos_SearchInput")),
                TimeSpan.FromSeconds(15));
            Assert.NotNull(searchInput.Result);

            // Mismo criterio que E2EAppHealthTests: vigila diálogos de error durante la carga inicial.
            string[] errorDialogs = Array.Empty<string>();
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                errorDialogs = _fixture.FindErrorDialogs();
                if (errorDialogs.Length > 0)
                {
                    break;
                }
                Thread.Sleep(250);
            }

            Assert.True(
                errorDialogs.Length == 0,
                $"Diálogos de error inesperados en el harness full-stack: {string.Join(" | ", errorDialogs)}");
        }
        finally
        {
            await _fixture.TeardownAsync();
        }

        Assert.False(
            await _fixture.DatabaseExistsAsync(),
            $"La base aislada '{_fixture.DatabaseName}' no fue eliminada por el teardown.");
        Assert.True(
            _fixture.ClientSettingsRestored,
            $"El settings del cliente no quedó restaurado en '{_fixture.ClientSettingsPath}'.");
        Assert.Empty(_fixture.TeardownErrors);
    }
}
