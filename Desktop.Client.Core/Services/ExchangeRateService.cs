using System;
using System.Collections.Generic;
using Core.Common;
using Core.Helpers;
using Core.Logging;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Messages;
using Microsoft.AspNetCore.SignalR.Client;
using System.Linq;

namespace Desktop.Client.Services;

/// <summary>
/// Unified service for managing exchange rates. Handles API interaction, 
/// persistence, and real-time SignalR updates.
/// </summary>
public class ExchangeRateService : IExchangeRateService, IDisposable, IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IDispatcherInvoker _dispatcherInvoker;
    private readonly UserSession? _userSession;
    private readonly HubConnection _hubConnection;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private decimal _currentRate;
    private DateTime? _lastUpdated;
    private int _isDisposed;
    private int _signalRStartInProgress;

    private const int SignalRMaxAttempts = 5;
    private const int SignalRRetryDelayMs = 5000;

    public DateTime? LastUpdated => _lastUpdated;
    public bool IsRateOutdated => _lastUpdated == null || (DateTime.UtcNow - _lastUpdated.Value.ToUniversalTime()) > TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ExchangeRateService(HttpClient httpClient, IDispatcherInvoker? dispatcherInvoker = null, UserSession? userSession = null)
    {
        _httpClient = httpClient;
        _dispatcherInvoker = dispatcherInvoker ?? new InlineDispatcherInvoker();
        _userSession = userSession;

        var baseAddress = httpClient.BaseAddress ?? new Uri("http://localhost:5000/");
        var hubUri = new Uri(baseAddress, "hubs/exchange-rate");

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                options.AccessTokenProvider = CreateAccessTokenProvider(hubUri, userSession);
                options.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is HttpClientHandler clientHandler)
                    {
                        // 8.16-R18: pinning TOFU compartido. Acepta certificados válidos o, en hosts
                        // privados/locales, autofirmados bajo Trust-On-First-Use (primer fingerprint
                        // registrado; certificados distintos posteriores son rechazados). Ya no se acepta
                        // cualquier certificado inválido por el único hecho de ser loopback.
                        clientHandler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                        {
                            return CertificatePinning.IsTrusted(message.RequestUri?.Host ?? baseAddress.Host, cert, errors);
                        };
                    }
                    return handler;
                };
            })
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.Closed += error =>
        {
            if (Volatile.Read(ref _isDisposed) == 0)
            {
                ClientStateLogger.LogWarning($"[SIGNALR] Conexión en tiempo real cerrada: {error?.Message ?? "sin detalle"}", nameof(ExchangeRateService));
            }
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += _ =>
        {
            ClientStateLogger.LogInfo("[SIGNALR] Conexión en tiempo real restablecida.", nameof(ExchangeRateService));
            return Task.CompletedTask;
        };

        _hubConnection.On<decimal>("ReceiveRateUpdate", async (newRate) =>
        {
            await UpdateRateLocallyAsync(newRate);
        });

        _hubConnection.On("OnHoldSalesUpdated", () =>
        {
            _dispatcherInvoker.Invoke(() =>
            {
                WeakReferenceMessenger.Default.Send(new OnHoldSalesRefreshMessage());
            });
        });

        _hubConnection.On("OnPaymentMethodsUpdated", () =>
        {
            _dispatcherInvoker.Invoke(() =>
            {
                WeakReferenceMessenger.Default.Send(new Desktop.Client.ViewModels.PaymentMethodsChangedMessage());
            });
        });

        if (_userSession != null)
        {
            _userSession.SessionChanged += OnSessionChanged;
        }

        InitializeAsync().SafeFireAndForget("ExchangeRateService.Initialize");
    }

    public static Func<Task<string?>> CreateAccessTokenProvider(Uri hubUri, UserSession? userSession)
    {
        ArgumentNullException.ThrowIfNull(hubUri);

        return () =>
        {
            if (hubUri.Scheme == "http" && !hubUri.IsLoopback)
            {
                ClientStateLogger.LogWarning("[SECURITY] Intentando enviar token SignalR sobre HTTP no loopback. Bloqueado.", nameof(ExchangeRateService));
                return Task.FromResult<string?>(null);
            }

            return Task.FromResult(userSession?.Token);
        };
    }

    private void OnSessionChanged()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_userSession?.Token))
        {
            return;
        }

        StartSignalRAsync().SafeFireAndForget("ExchangeRateService.SessionChangedReconnect");
    }

    private async Task InitializeAsync()
    {
        // Initial sync from API — if this fails, the app starts with rate = 0
        // which is safe (PricingHelper returns 0 for rate <= 0)
        try
        {
            await GetCurrentRateAsync();
        }
        catch
        {
            // Rate stays at 0; user will see "0.00" and can set it manually
        }

        // Start SignalR in background — never blocks the UI
        StartSignalRAsync().SafeFireAndForget("ExchangeRateService.StartSignalR");
    }

    public decimal CurrentRate
    {
        get => _currentRate;
        set => SetCurrentRateSynchronously(value);
    }

    /// <summary>
    /// 8.9-L7: incorporación síncrona de tasa desde el setter. El estado queda consistente al
    /// retornar; no se delega a fire-and-forget (evita tareas huérfanas y estado reversionado
    /// ante fallos). Inseguro de marcar/deadlocarse porque los hilos de fondo ya no retienen el
    /// semáforo mientras esperan al dispatcher (ver BroadcastRateChange).
    /// </summary>
    public void SetCurrentRateSynchronously(decimal newRate)
    {
        _semaphore.Wait();
        try
        {
            newRate = PricingCalculator.RoundExchangeRateCeiling(newRate);
            if (_currentRate != newRate)
            {
                _currentRate = newRate;
                BroadcastRateChange(_currentRate);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task UpdateRateLocallyAsync(decimal newRate)
    {
        await _semaphore.WaitAsync();
        try
        {
            newRate = PricingCalculator.RoundExchangeRateCeiling(newRate);
            if (_currentRate != newRate)
            {
                _currentRate = newRate;
                BroadcastRateChange(_currentRate);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private void BroadcastRateChange(decimal newRate)
    {
        // 8.9-L7: InvokeAsync (no bloqueante): el hilo de fondo suelta el semáforo sin
        // esperar al dispatcher, por lo que un setter síncrono desde la UI (Wait) jamás
        // puede quedar atrapado esperando un dispatcher bloqueado por sí mismo.
        _dispatcherInvoker.InvokeAsync(() =>
        {
            WeakReferenceMessenger.Default.Send(new ExchangeRateChangedMessage(newRate));
        });
    }

    private async Task StartSignalRAsync()
    {
        if (Interlocked.CompareExchange(ref _signalRStartInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            for (int attempt = 1; attempt <= SignalRMaxAttempts; attempt++)
            {
                if (Volatile.Read(ref _isDisposed) != 0)
                {
                    return;
                }

                try
                {
                    if (_hubConnection.State != HubConnectionState.Disconnected)
                    {
                        return;
                    }

                    await _hubConnection.StartAsync();
                    ClientStateLogger.LogInfo("[SIGNALR] Conexión en tiempo real establecida.", nameof(ExchangeRateService));
                    return;
                }
                catch (Exception ex)
                {
                    ClientStateLogger.LogWarning($"[SIGNALR] Fallo al conectar en tiempo real (intento {attempt}/{SignalRMaxAttempts}): {ex.Message}", nameof(ExchangeRateService));

                    if (attempt == SignalRMaxAttempts)
                    {
                        ClientStateLogger.LogWarning($"[SIGNALR] Tiempo real no disponible tras {SignalRMaxAttempts} intentos. Degradado a actualización manual.", nameof(ExchangeRateService));
                        return;
                    }

                    await Task.Delay(SignalRRetryDelayMs);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _signalRStartInProgress, 0);
        }
    }

    public async Task<(decimal Rate, DateTime? LastUpdated)> GetCurrentRateAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("api/exchange-rate/today");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>(_jsonOptions);
            decimal rate = json?.Value ?? 0m;
            if (json?.UpdatedAt != null)
            {
                _lastUpdated = json.UpdatedAt;
            }
            if (rate > 0)
            {
                await UpdateRateLocallyAsync(rate);
            }
            return (rate, _lastUpdated);
        }
        catch
        {
            return (0m, null);
        }
    }

    public async Task SaveRateAsync(decimal rate)
    {
        var response = await _httpClient.PostAsJsonAsync("api/exchange-rate", new { Value = rate });
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>(_jsonOptions);
        decimal effectiveRate = json?.Value ?? rate;
        effectiveRate = PricingCalculator.RoundExchangeRateCeiling(effectiveRate);

        _lastUpdated = json?.UpdatedAt ?? DateTime.UtcNow;
        await UpdateRateLocallyAsync(effectiveRate);
    }

    public async Task<List<ExchangeRateHistoryDto>> GetHistoryAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("api/exchange-rate/history");
            response.EnsureSuccessStatusCode();

            var history = await response.Content.ReadFromJsonAsync<List<ExchangeRateHistoryDto>>(_jsonOptions);
            return history ?? new List<ExchangeRateHistoryDto>();
        }
        catch
        {
            return new List<ExchangeRateHistoryDto>();
        }
    }

    public async Task<(decimal Rate, DateTime? LastUpdated)> SyncBcvAsync()
    {
        var response = await _httpClient.PostAsync("api/exchange-rate/sync-bcv", null);
        if (!response.IsSuccessStatusCode)
        {
            string? errorMessage = null;
            try
            {
                var errorDoc = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(_jsonOptions);
                errorMessage = errorDoc?["message"]?.ToString();
            }
            catch { }
            throw new HttpRequestException(errorMessage ?? $"Error {(int)response.StatusCode}: {response.ReasonPhrase}");
        }

        var json = await response.Content.ReadFromJsonAsync<ExchangeRateResponse>(_jsonOptions);
        decimal rate = json?.Value ?? 0m;
        _lastUpdated = json?.UpdatedAt ?? DateTime.UtcNow;
        if (rate > 0)
        {
            await UpdateRateLocallyAsync(rate);
        }
        return (rate, _lastUpdated);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

        if (_userSession != null)
        {
            _userSession.SessionChanged -= OnSessionChanged;
        }

        if (_hubConnection != null)
        {
            try
            {
                await _hubConnection.StopAsync();
            }
            catch { }

            try
            {
                await _hubConnection.DisposeAsync();
            }
            catch { }
        }

        try
        {
            _semaphore.Dispose();
        }
        catch { }

        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _isDisposed) != 0) return;

        // 8.6-B5: NO bloquear el hilo caller (UI o contenedor DI) con Wait/GetResult (sync-over-async).
        // El apagado asíncrono real lo ejecuta App.StopServicesAsync vía DisposeAsync(); este camino
        // es solo best-effort defensivo que se despacha al pool de subprocesos sin esperarlo.
        try
        {
            _ = Task.Run(async () => await DisposeAsync().ConfigureAwait(false));
        }
        catch (Exception) { }
    }

    private class ExchangeRateResponse
    {
        public decimal Value { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
