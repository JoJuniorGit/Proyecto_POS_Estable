using System;
using System.IO;
using Core.Logging;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests;

public class WpfDialogServiceTests
{
    // Helper: creates WpfDialogService with a null ISalesService stub (safe for UI-suppressed tests)
    private static WpfDialogService CreateService(ClientStateService clientState)
        => new WpfDialogService(clientState, null!);

    [Fact]
    public void ShowConfirm_ReturnsFalse_WhenApplicationCurrentIsNull()
    {
        var clientState = new ClientStateService();
        var dialogService = CreateService(clientState);

        bool result = dialogService.ShowConfirm("Cierre de Caja", "¿Desea forzar el cierre de caja?");

        Assert.False(result);
    }

    [Fact]
    public void ShowConfirm_ReturnsFalse_WhenCircuitBreakerIsActive()
    {
        var clientState = new ClientStateService();
        clientState.TryActivateFatalError();
        Assert.True(clientState.IsFatalErrorActive);

        var dialogService = CreateService(clientState);

        bool result = dialogService.ShowConfirm("Eliminar Producto", "¿Desea eliminar este producto?");

        // Must return false strictly
        Assert.False(result);
    }

    [Fact]
    public void ShowNotifications_SuppressedWithoutException_WhenApplicationCurrentIsNull()
    {
        var clientState = new ClientStateService();
        var dialogService = CreateService(clientState);

        // Should execute cleanly without throwing NRE or WPF exceptions
        dialogService.ShowError("Error Grave", "Fallo de conexión");
        dialogService.ShowWarning("Advertencia", "Tasa de cambio no configurada");
        dialogService.ShowInfo("Información", "Operación completada");

        Assert.True(true);
    }

    [Fact]
    public void ShowNotifications_SuppressedWithoutException_WhenCircuitBreakerIsActive()
    {
        var clientState = new ClientStateService();
        clientState.TryActivateFatalError();
        var dialogService = CreateService(clientState);

        // Should execute cleanly without throwing NRE or WPF exceptions
        dialogService.ShowError("Error Fatal", "Servidor no responde");
        dialogService.ShowWarning("Advertencia", "Almacén offline");
        dialogService.ShowInfo("Información", "Modo degradado");

        Assert.True(true);
    }

    [Fact]
    public void WpfDialogService_WithActiveCircuitBreaker_SuppressesErrorNotifications()
    {
        var clientState = new ClientStateService();
        clientState.TryActivateFatalError();
        Assert.True(clientState.IsFatalErrorActive);

        var dialogService = CreateService(clientState);

        // Verify that executing ShowError, ShowWarning, and ShowInfo under fatal circuit breaker does not throw or crash
        var exception = Record.Exception(() =>
        {
            dialogService.ShowError("Error de Red", "Fallo de conexión en modo degradado");
            dialogService.ShowWarning("Advertencia", "Notificación suprimida");
            dialogService.ShowInfo("Info", "Operación no prestada");
        });

        Assert.Null(exception);
    }
}
