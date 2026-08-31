using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

public partial class ServerConnectionViewModel : ObservableObject
{
    private readonly IConnectionManager _connectionManager;
    private readonly ISubnetScannerService _scannerService;
    private CancellationTokenSource? _scanCts;

    [ObservableProperty]
    private bool _isLocalServerMode;

    [ObservableProperty]
    private bool _isRemoteServerMode = true;

    [ObservableProperty]
    private string _serverAddress = string.Empty;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private int _scanProgress;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private bool? _testSuccess;

    [ObservableProperty]
    private ObservableCollection<DiscoveredServer> _discoveredServers = new();

    [ObservableProperty]
    private DiscoveredServer? _selectedServer;

    public event Action<bool>? RequestClose;

    public ServerConnectionViewModel(IConnectionManager connectionManager, ISubnetScannerService scannerService)
    {
        _connectionManager = connectionManager;
        _scannerService = scannerService;

        var current = _connectionManager.CurrentServerAddress;
        ServerAddress = current;
        IsLocalServerMode = current.Contains("localhost") || current.Contains("127.0.0.1");
        IsRemoteServerMode = !IsLocalServerMode;
    }

    partial void OnIsLocalServerModeChanged(bool value)
    {
        if (value)
        {
            ServerAddress = "http://localhost:5000/";
            IsRemoteServerMode = false;
        }
    }

    partial void OnIsRemoteServerModeChanged(bool value)
    {
        if (value)
        {
            IsLocalServerMode = false;
        }
    }

    partial void OnSelectedServerChanged(DiscoveredServer? value)
    {
        if (value != null)
        {
            ServerAddress = value.BaseUrl;
            IsRemoteServerMode = true;
            IsLocalServerMode = false;
        }
    }

    [RelayCommand]
    private async Task ScanNetworkAsync()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();

        IsScanning = true;
        ScanProgress = 0;
        StatusMessage = "Escaneando la subred local en busca de servidores POS...";
        ErrorMessage = string.Empty;
        DiscoveredServers.Clear();

        var progress = new Progress<int>(pct => ScanProgress = pct);

        try
        {
            var servers = await _scannerService.ScanSubnetAsync(progress, force: true, _scanCts.Token);
            foreach (var s in servers)
            {
                DiscoveredServers.Add(s);
            }

            if (servers.Any())
            {
                StatusMessage = $"Se encontraron {servers.Count} servidor(es) POS en la red.";
                SelectedServer = servers.First();
            }
            else
            {
                StatusMessage = "No se detectaron servidores POS activos en la subred local.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Búsqueda cancelada.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error al escanear: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerAddress))
        {
            ErrorMessage = "Ingrese una dirección de servidor.";
            return;
        }

        IsTesting = true;
        TestSuccess = null;
        ErrorMessage = string.Empty;
        StatusMessage = "Probando conexión con el servidor...";

        try
        {
            var probe = await _scannerService.ProbeSingleHostAsync(ServerAddress, 5000, 1500);
            if (probe != null && probe.IsHealthy)
            {
                TestSuccess = true;
                StatusMessage = $"Conexión exitosa con {probe.MachineName} ({probe.IpAddress}) en {probe.ResponseTimeMs} ms.";
            }
            else
            {
                TestSuccess = false;
                ErrorMessage = "El servidor no respondió. Verifique la IP y que el servicio POS esté en ejecución.";
            }
        }
        catch (Exception ex)
        {
            TestSuccess = false;
            ErrorMessage = $"Error de conexión: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task SaveAndConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerAddress))
        {
            ErrorMessage = "Ingrese o seleccione un servidor.";
            return;
        }

        IsTesting = true;
        ErrorMessage = string.Empty;

        try
        {
            var success = await _connectionManager.TestAndSetServerAddressAsync(ServerAddress, savePermanent: true);
            if (success)
            {
                RequestClose?.Invoke(true);
            }
            else
            {
                ErrorMessage = "No se pudo establecer la conexión con la dirección especificada.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }
}
