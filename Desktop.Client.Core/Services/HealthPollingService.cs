using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Core.Logging;

namespace Desktop.Client.Services;

public interface IHealthPollingService
{
    bool IsPollingActive { get; }
    event EventHandler? OnHealthRecovered;
    void StartPolling();
    void StopPolling();
}

public class HealthPollingService : IHealthPollingService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IClientStateService? _clientStateService;
    private readonly IConnectionManager? _connectionManager;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new object();
    private bool _isPollingActive;

    public bool IsPollingActive
    {
        get
        {
            lock (_lock) return _isPollingActive;
        }
        private set
        {
            lock (_lock) _isPollingActive = value;
        }
    }

    public event EventHandler? OnHealthRecovered;

    public HealthPollingService(HttpClient httpClient, IClientStateService? clientStateService = null, IConnectionManager? connectionManager = null)
    {
        _httpClient = httpClient;
        _clientStateService = clientStateService;
        _connectionManager = connectionManager;
    }

    public void StartPolling()
    {
        CancellationTokenSource localCts;
        CancellationToken token;
        lock (_lock)
        {
            if (_isPollingActive) return;
            _isPollingActive = true;
            _cts = new CancellationTokenSource();
            localCts = _cts;
            try
            {
                token = localCts.Token;
            }
            catch (ObjectDisposedException)
            {
                _isPollingActive = false;
                _cts = null;
                return;
            }
        }

        try
        {
            Task.Run(() => PollLoopAsync(localCts, token), token);
        }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }

    private async Task PollLoopAsync(CancellationTokenSource originCts, CancellationToken cancellationToken)
    {
        ClientStateLogger.LogInfo("Health polling loop started in background (polling /health every 3s).");
        int consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            lock (_lock)
            {
                if (_cts != originCts || !_isPollingActive) break;
            }

            try
            {
                var response = await _httpClient.GetAsync("health", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    ClientStateLogger.LogHealthRecovery();
                    _clientStateService?.ResetFatalError();
                    _connectionManager?.NotifyConnectionRestored();
                    consecutiveFailures = 0;
                    StopPolling();
                    OnHealthRecovered?.Invoke(this, EventArgs.Empty);
                    break;
                }
                else
                {
                    consecutiveFailures++;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                consecutiveFailures++;
            }

            if (consecutiveFailures == 3)
            {
                _connectionManager?.NotifyConnectionFailed("Sin respuesta del servidor tras múltiples intentos.");
                if (_connectionManager != null)
                {
                    _ = _connectionManager.AutoRecoverAsync(cancellationToken);
                }
            }

            try
            {
                await Task.Delay(3000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        lock (_lock)
        {
            if (_cts == originCts)
            {
                _isPollingActive = false;
            }
        }
    }

    public void StopPolling()
    {
        CancellationTokenSource? oldCts;
        lock (_lock)
        {
            if (!_isPollingActive && _cts == null) return;
            _isPollingActive = false;
            oldCts = _cts;
            _cts = null;
        }

        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        StopPolling();
    }
}
