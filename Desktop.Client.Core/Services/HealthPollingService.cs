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

    public HealthPollingService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public void StartPolling()
    {
        lock (_lock)
        {
            if (_isPollingActive) return;
            _isPollingActive = true;
            _cts = new CancellationTokenSource();
        }

        var token = _cts.Token;
        Task.Run(() => PollLoopAsync(token), token);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        ClientStateLogger.LogInfo("Health polling loop started in background (polling /health every 3s).");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var response = await _httpClient.GetAsync("health", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    ClientStateLogger.LogHealthRecovery();
                    StopPolling();
                    OnHealthRecovered?.Invoke(this, EventArgs.Empty);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Continued outage: log debug or silently wait
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
            _isPollingActive = false;
        }
    }

    public void StopPolling()
    {
        lock (_lock)
        {
            if (!_isPollingActive && _cts == null) return;
            _isPollingActive = false;
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
            }
            catch { }
            _cts = null;
        }
    }

    public void Dispose()
    {
        StopPolling();
    }
}
