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
            // 8.5-M6: Validación estricta antes de abrir la URL del servidor. Solo se permite
            // http/https; cualquier otra entrada (script, comando, protocolo custom) se rechaza
            // para evitar OS command injection vía UseShellExecute si el servidor se ve comprometido.
            var url = UpdateServerUrl.Trim();
            bool isAllowed =
                Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

            if (!isAllowed)
            {
                StatusMessage = "La dirección de actualización no es una URL válida (http/https). Contacte al administrador.";
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
            catch
            {
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
