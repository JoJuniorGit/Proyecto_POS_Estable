using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;
using System.Windows;

namespace Desktop.Client.ViewModels;

public partial class VersionLockoutViewModel : ObservableObject
{
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

    public VersionLockoutViewModel(string currentVersion, string minimumClientVersion, string updateServerUrl)
    {
        CurrentVersion = currentVersion;
        MinimumClientVersion = minimumClientVersion;
        UpdateServerUrl = updateServerUrl;
        StatusMessage = $"Su versión instalada ({currentVersion}) es inferior a la requerida ({minimumClientVersion}). Por favor actualice el sistema.";
    }

    [RelayCommand]
    private void StartUpdate()
    {
        IsDownloading = true;
        StatusMessage = "Iniciando proceso de actualización...";

        if (!string.IsNullOrWhiteSpace(UpdateServerUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = UpdateServerUrl,
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
        Application.Current.Shutdown();
    }
}
