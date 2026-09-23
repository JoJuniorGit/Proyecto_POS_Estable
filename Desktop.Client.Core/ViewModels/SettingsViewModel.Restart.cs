using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class SettingsViewModel
{
    [ObservableProperty]
    private bool _isRestarting;

    [RelayCommand]
    private async Task RestartSystemAsync()
    {
        if (UserSession == null || !UserSession.CanMutateSettings) return;

        bool confirmed = _dialogService != null
            ? _dialogService.ShowConfirm(
                "Reiniciar Sistema",
                "¿Desea reiniciar el servicio del sistema POS?\n\nLas operaciones en curso se interrumpirán durante unos segundos. Ninguna venta ni configuración se perderá.")
            : false;

        if (!confirmed) return;

        IsRestarting = true;
        try
        {
            await _settingsService.RestartSystemAsync();
            _dialogService?.ShowInfo(
                "Reinicio en curso",
                "El servicio se está reiniciando. Espere unos segundos; la conexión volverá automáticamente.");
        }
        catch (Exception ex)
        {
            _dialogService?.ShowError("Reinicio de Sistema", $"No se pudo iniciar el reinicio: {ex.Message}");
        }
        finally
        {
            IsRestarting = false;
        }
    }
}