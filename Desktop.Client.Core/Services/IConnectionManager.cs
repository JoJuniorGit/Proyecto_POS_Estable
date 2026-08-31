using System;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public enum ConnectionStatus
{
    Connected,
    Connecting,
    Disconnected,
    Scanning
}

public class ConnectionStatusEventArgs : EventArgs
{
    public ConnectionStatus Status { get; set; }
    public string ServerAddress { get; set; } = string.Empty;
    public string? MachineName { get; set; }
    public string? ErrorMessage { get; set; }
}

public interface IConnectionManager
{
    ConnectionStatus Status { get; }
    string CurrentServerAddress { get; }
    string? CurrentMachineName { get; }
    event EventHandler<ConnectionStatusEventArgs>? ConnectionStatusChanged;

    Task<bool> InitializeAsync();
    Task<bool> TestAndSetServerAddressAsync(string newAddress, bool savePermanent = true);
    Task<bool> AutoRecoverAsync(CancellationToken ct = default);
    void NotifyConnectionFailed(string? error = null);
    void NotifyConnectionRestored();
}

public class ConnectionManager : IConnectionManager, IDisposable
{
    private readonly IClientSettingsStore _settingsStore;
    private readonly ISubnetScannerService _scannerService;
    private readonly object _lock = new object();
    private Timer? _heartbeatTimer;
    private int _isProbing;
    private bool _disposed;

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Connecting;
    public string CurrentServerAddress { get; private set; } = "http://localhost:5000/";
    public string? CurrentMachineName { get; private set; }

    public event EventHandler<ConnectionStatusEventArgs>? ConnectionStatusChanged;

    public ConnectionManager(IClientSettingsStore settingsStore, ISubnetScannerService scannerService)
    {
        _settingsStore = settingsStore;
        _scannerService = scannerService;

        var settings = _settingsStore.LoadSettings();
        CurrentServerAddress = settings.ServerBaseAddress;
        CurrentMachineName = settings.LastKnownServerMachineName;

        // Iniciar inmediatamente sondeo en segundo plano y temporizador periódico cada 4 segundos
        StartHeartbeat();
        _ = InitializeAsync();
    }

    private void StartHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = new Timer(OnHeartbeatTick, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
    }

    private async void OnHeartbeatTick(object? state)
    {
        if (_disposed || Status == ConnectionStatus.Scanning) return;
        if (Interlocked.CompareExchange(ref _isProbing, 1, 0) != 0) return;

        try
        {
            var probe = await _scannerService.ProbeSingleHostAsync(CurrentServerAddress, 5000, 800);
            if (probe != null && probe.IsHealthy)
            {
                if (Status != ConnectionStatus.Connected || CurrentMachineName != probe.MachineName)
                {
                    lock (_lock)
                    {
                        Status = ConnectionStatus.Connected;
                        CurrentMachineName = probe.MachineName;
                    }
                    RaiseStatusChanged();
                }
            }
            else
            {
                if (Status == ConnectionStatus.Connected)
                {
                    lock (_lock)
                    {
                        Status = ConnectionStatus.Disconnected;
                    }
                    RaiseStatusChanged("Conexión perdida con el servidor.");
                }
                else if (Status == ConnectionStatus.Connecting)
                {
                    lock (_lock)
                    {
                        Status = ConnectionStatus.Disconnected;
                    }
                    RaiseStatusChanged("No se pudo conectar con el servidor.");
                }
            }
        }
        catch
        {
            // Ignorar excepciones transitorias en el temporizador
        }
        finally
        {
            Interlocked.Exchange(ref _isProbing, 0);
        }
    }

    public async Task<bool> InitializeAsync()
    {
        var settings = _settingsStore.LoadSettings();
        CurrentServerAddress = settings.ServerBaseAddress;
        CurrentMachineName = settings.LastKnownServerMachineName;

        // Probar si el servidor actual responde
        var probe = await _scannerService.ProbeSingleHostAsync(CurrentServerAddress, 5000, 800);
        if (probe != null && probe.IsHealthy)
        {
            lock (_lock)
            {
                Status = ConnectionStatus.Connected;
                CurrentMachineName = probe.MachineName;
            }
            RaiseStatusChanged();
            return true;
        }

        // Si falló y auto-discover está habilitado, intentar auto-recuperar
        if (settings.AutoDiscoverOnFailure)
        {
            return await AutoRecoverAsync();
        }

        lock (_lock)
        {
            Status = ConnectionStatus.Disconnected;
        }
        RaiseStatusChanged("No se pudo conectar con el servidor configurado.");
        return false;
    }

    public async Task<bool> TestAndSetServerAddressAsync(string newAddress, bool savePermanent = true)
    {
        if (string.IsNullOrWhiteSpace(newAddress)) return false;

        var clean = newAddress.Trim();
        if (!clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
            !clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            clean = $"http://{clean}";
        }

        if (!clean.EndsWith("/")) clean += "/";

        Status = ConnectionStatus.Connecting;
        RaiseStatusChanged();

        var probe = await _scannerService.ProbeSingleHostAsync(clean, 5000, 1000);
        if (probe != null)
        {
            lock (_lock)
            {
                CurrentServerAddress = probe.BaseUrl;
                CurrentMachineName = probe.MachineName;
                Status = ConnectionStatus.Connected;

                if (savePermanent)
                {
                    _settingsStore.UpdateServerAddress(probe.BaseUrl, probe.MachineName);
                }
            }

            RaiseStatusChanged();
            return true;
        }

        Status = ConnectionStatus.Disconnected;
        RaiseStatusChanged("El servidor no respondió a la prueba de conexión.");
        return false;
    }

    public async Task<bool> AutoRecoverAsync(CancellationToken ct = default)
    {
        Status = ConnectionStatus.Scanning;
        RaiseStatusChanged("Buscando servidor POS en la red local...");

        var settings = _settingsStore.LoadSettings();
        var discovered = await _scannerService.QuickDiscoverAsync(settings.LastKnownServerIp, ct);

        if (discovered != null)
        {
            lock (_lock)
            {
                CurrentServerAddress = discovered.BaseUrl;
                CurrentMachineName = discovered.MachineName;
                Status = ConnectionStatus.Connected;
                _settingsStore.UpdateServerAddress(discovered.BaseUrl, discovered.MachineName);
            }

            RaiseStatusChanged();
            return true;
        }

        Status = ConnectionStatus.Disconnected;
        RaiseStatusChanged("No se encontró ningún servidor POS en la red local.");
        return false;
    }

    public void NotifyConnectionFailed(string? error = null)
    {
        if (Status != ConnectionStatus.Disconnected)
        {
            Status = ConnectionStatus.Disconnected;
            RaiseStatusChanged(error ?? "Se perdió la conexión con el servidor.");
        }
    }

    public void NotifyConnectionRestored()
    {
        if (Status != ConnectionStatus.Connected)
        {
            Status = ConnectionStatus.Connected;
            RaiseStatusChanged();
        }
    }

    private void RaiseStatusChanged(string? error = null)
    {
        ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs
        {
            Status = Status,
            ServerAddress = CurrentServerAddress,
            MachineName = CurrentMachineName,
            ErrorMessage = error
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }
}
