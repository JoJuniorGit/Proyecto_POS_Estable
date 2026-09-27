using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;
using System.Diagnostics;

namespace Desktop.Client.ViewModels;

public partial class VersionLockoutViewModel : ObservableObject
{
    private readonly IAppShutdown _lifetime;
    [ObservableProperty]
    private string _currentVersion = "1.0.0";

    [ObservableProperty]
    private string _minimumClientVersion = "1.0.0";

    [ObservableProperty]
    private string _updateServerUrl = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Su versión de cliente es obsoleta. Debe actualizar para continuar.";

    [ObservableProperty]
    private bool _isDownloading;

    public VersionLockoutViewModel(string currentVersion, string minimumClientVersion, string updateServerUrl, IAppShutdown? lifetime = null)
    {
        CurrentVersion = currentVersion;
        MinimumClientVersion = minimumClientVersion;
        UpdateServerUrl = updateServerUrl;
        _lifetime = lifetime ?? new NoopAppShutdown();
        StatusMessage = $"Su versión instalada ({currentVersion}) es inferior a la requerida ({minimumClientVersion}). Por favor actualice el sistema.";
    }

    [RelayCommand]
    private void StartUpdate()
    {
        IsDownloading = true;
        StatusMessage = "Iniciando proceso de actualización...";

        if (!string.IsNullOrWhiteSpace(UpdateServerUrl))
        {
            // 8.5-M6 + 8.16: solo http/https y nunca HTTP plano hacia hosts remotos; loopback exceptuado.
            var url = UpdateServerUrl.Trim();
            if (!UpdateUrlPolicy.IsAllowed(url))
            {
                Core.Logging.ClientStateLogger.LogWarning($"[SECURITY] URL de actualización rechazada por no cumplir la política HTTPS: {url}", nameof(VersionLockoutViewModel));
                StatusMessage = "La dirección de actualización no es segura (se requiere HTTPS). Contacte al administrador.";
                IsDownloading = false;
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Core.Logging.ClientStateLogger.LogWarning($"[UPDATE] No se pudo abrir el enlace de actualización: {ex.Message}", nameof(VersionLockoutViewModel));
                StatusMessage = "No se pudo abrir el enlace de actualización automáticamente.";
            }
        }
    }

    [RelayCommand]
    private void ExitApp()
    {
        _lifetime.Shutdown();
    }

    private sealed class NoopAppShutdown : IAppShutdown
    {
        public void Shutdown() { }
    }
}
