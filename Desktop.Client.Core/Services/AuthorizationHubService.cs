using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Core.Common;
using Core.Helpers;
using Core.Logging;
using Microsoft.AspNetCore.SignalR.Client;

// 8.157 (SEC-07): exposicion minima de los seams internos de ForceDisconnect al ensamblado de
// pruebas (mismo patron del ANEXO 8.75 en SecurityStampValidator, Backend.API).
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("CommandCenter.Tests")]

namespace Desktop.Client.Services;

/// <summary>
/// 8.150 (T9, design D7): cliente WPF del hub /hubs/authorization con access_token (patron
/// ExchangeRateService), auto-reconnect y par REST del controlador para cuando el socket esta
/// caido. Los eventos AuthorizationRequested/Resolved/Expired replican al notificador del backend.
/// </summary>
public class AuthorizationHubService : IAuthorizationHubService, IAsyncDisposable
{
    public const string HubPath = "hubs/authorization";
    public const string RequestAuthorizationMethod = "RequestAuthorization";
    public const string ResolveAuthorizationMethod = "ResolveAuthorization";
    public const string RequestedEvent = "AuthorizationRequested";
    public const string ResolvedEvent = "AuthorizationResolved";
    public const string ExpiredEvent = "AuthorizationExpired";

    /// <summary>8.157 (SEC-07): push de revocacion de sesion; el evento llega sin payload (T1).</summary>
    public const string ForceDisconnectEvent = "ForceDisconnect";

    private const int SignalRMaxAttempts = 5;
    private const int SignalRRetryDelayMs = 5000;

    private readonly HttpClient _httpClient;
    private readonly UserSession? _userSession;
    private readonly HubConnection? _hubConnection;
    private int _isDisposed;
    private int _signalRStartInProgress;
    private int _hasConnectedBefore;
    private int _forceDisconnectRaised;

    public AuthorizationHubService(HttpClient httpClient, UserSession? userSession = null, bool enableRealtime = true)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
        _userSession = userSession;

        if (enableRealtime)
        {
            var baseAddress = httpClient.BaseAddress ?? new Uri("http://localhost:5000/");
            var hubUri = new Uri(baseAddress, HubPath);

            _hubConnection = new HubConnectionBuilder()
                .WithUrl(hubUri, options =>
                {
                    options.AccessTokenProvider = CreateAccessTokenProvider(hubUri, userSession);
                    options.HttpMessageHandlerFactory = handler =>
                    {
                        if (handler is HttpClientHandler clientHandler)
                        {
                            // 8.16-R18: pinning TOFU compartido con el resto de hubs/API del cliente.
                            clientHandler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                                CertificatePinning.IsTrusted(message.RequestUri?.Host ?? baseAddress.Host, cert, errors);
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
                    ClientStateLogger.LogWarning($"[SIGNALR] Hub de autorizaciones cerrado: {error?.Message ?? "sin detalle"}", nameof(AuthorizationHubService));
                }
                return Task.CompletedTask;
            };

            _hubConnection.Reconnected += _ =>
            {
                ClientStateLogger.LogInfo("[SIGNALR] Hub de autorizaciones restablecido.", nameof(AuthorizationHubService));
                RaiseReconnected();
                return Task.CompletedTask;
            };

            _hubConnection.On<AuthorizationRequestedPayload>(RequestedEvent, payload => RaiseEvent(AuthorizationRequested, payload));
            _hubConnection.On<AuthorizationResolvedPayload>(ResolvedEvent, payload => RaiseEvent(AuthorizationResolved, payload));
            _hubConnection.On<AuthorizationExpiredPayload>(ExpiredEvent, payload => RaiseEvent(AuthorizationExpired, payload));

            // 8.157 (SEC-07): push de revocacion; el stop explicito corta el auto-reconnect y
            // ForceDisconnected notifica una unica vez a la UI.
            _hubConnection.On(ForceDisconnectEvent, () =>
            {
                ClientStateLogger.LogWarning("[SIGNALR] Sesión revocada por el servidor; cerrando hub de autorizaciones.", nameof(AuthorizationHubService));
                StopAfterForceDisconnectAsync().SafeFireAndForget("AuthorizationHubService.ForceDisconnect");
            });
        }

        if (_userSession != null)
        {
            _userSession.SessionChanged += OnSessionChanged;
        }

        StartSignalRAsync().SafeFireAndForget("AuthorizationHubService.StartSignalR");
    }

    public event EventHandler<AuthorizationRequestedPayload>? AuthorizationRequested;
    public event EventHandler<AuthorizationResolvedPayload>? AuthorizationResolved;
    public event EventHandler<AuthorizationExpiredPayload>? AuthorizationExpired;
    public event EventHandler? Reconnected;

    /// <summary>8.157 (SEC-07): el servidor revoco la sesion; el socket quedo detenido y la UI debe cerrar sesion.</summary>
    public event EventHandler? ForceDisconnected;

    /// <summary>8.150 (T9): misma politica TOFU que ExchangeRateService para el token del hub.</summary>
    public static Func<Task<string?>> CreateAccessTokenProvider(Uri hubUri, UserSession? userSession)
    {
        ArgumentNullException.ThrowIfNull(hubUri);

        return () =>
        {
            if (hubUri.Scheme == "http" && !hubUri.IsLoopback)
            {
                ClientStateLogger.LogWarning("[SECURITY] Intentando enviar token SignalR sobre HTTP no loopback. Bloqueado.", nameof(AuthorizationHubService));
                return Task.FromResult<string?>(null);
            }

            return Task.FromResult(userSession?.Token);
        };
    }

    /// <summary>8.150 (T9): claves exactas de RequestAuthorizationContract (hub y REST comparten el payload).</summary>
    public static Dictionary<string, object?> BuildRequestPayload(AuthorizationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new Dictionary<string, object?>
        {
            ["saleId"] = context.SaleId,
            ["productId"] = context.ProductId,
            ["productName"] = context.ProductName,
            ["quantity"] = context.Quantity,
            ["customUnitPriceUsd"] = context.CustomUnitPriceUsd,
            ["customUnitPriceLocal"] = context.CustomUnitPriceLocal,
            ["terminal"] = context.Terminal
        };
    }

    public async Task<AuthorizationRequestInfo> RequestAuthorizationAsync(AuthorizationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        EnsureNotDisposed();

        var payload = BuildRequestPayload(context);
        var hubResult = await TryInvokeHubAsync<CreateAuthorizationResponse>(RequestAuthorizationMethod, payload);
        if (hubResult is not null)
        {
            if (!hubResult.Success || hubResult.RequestId is null)
            {
                throw new AuthorizationHubException(hubResult.Message ?? "No se pudo crear la solicitud de autorización.", 400);
            }

            return ToRequestInfo(hubResult);
        }

        using var response = await _httpClient.PostAsJsonAsync("api/authorizations", payload).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateRestExceptionAsync(response).ConfigureAwait(false);
        }

        var restResult = await response.Content.ReadFromJsonAsync<CreateAuthorizationResponse>().ConfigureAwait(false)
            ?? throw new AuthorizationHubException("No se pudo crear la solicitud de autorización.", 400);

        if (restResult.RequestId is null)
        {
            throw new AuthorizationHubException(restResult.Message ?? "No se pudo crear la solicitud de autorización.", 400);
        }

        return ToRequestInfo(restResult);
    }

    public async Task<AuthorizationResolveInfo> ResolveAuthorizationAsync(int requestId, bool approved, string? reason = null)
    {
        EnsureNotDisposed();

        var hubResult = await TryInvokeHubAsync<ResolveAuthorizationResponse>(ResolveAuthorizationMethod, requestId, approved, reason);
        if (hubResult is not null)
        {
            if (!hubResult.Success)
            {
                throw new AuthorizationHubException(hubResult.Message ?? "No se pudo resolver la solicitud de autorización.", 409);
            }

            return new AuthorizationResolveInfo(hubResult.Status, hubResult.ResolvedByName, hubResult.Reason);
        }

        var payload = new Dictionary<string, object?>
        {
            ["approved"] = approved,
            ["reason"] = reason
        };

        using var response = await _httpClient.PostAsJsonAsync($"api/authorizations/{requestId}/resolve", payload).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateRestExceptionAsync(response).ConfigureAwait(false);
        }

        var restResult = await response.Content.ReadFromJsonAsync<ResolveAuthorizationResponse>().ConfigureAwait(false)
            ?? new ResolveAuthorizationResponse();

        return new AuthorizationResolveInfo(restResult.Status, restResult.ResolvedByName, restResult.Reason);
    }

    public async Task CancelRequestAsync(int requestId)
    {
        EnsureNotDisposed();

        // 8.151 (W3, R4-client): el hub no expone cancelacion; el retiro va por el REST del W1
        // (200/409/404/403) y los llamadores best-effort ignoran el rechazo.
        using var response = await _httpClient.PostAsync($"api/authorizations/{requestId}/cancel", content: null).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateRestExceptionAsync(response).ConfigureAwait(false);
        }
    }

    public async Task<AuthorizationStatusInfo?> GetStatusAsync(int requestId)
    {
        EnsureNotDisposed();

        using var response = await _httpClient.GetAsync($"api/authorizations/{requestId}").ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateRestExceptionAsync(response).ConfigureAwait(false);
        }

        var status = await response.Content.ReadFromJsonAsync<AuthorizationStatusInfo>().ConfigureAwait(false);
        if (status is null)
        {
            return null;
        }

        return status;
    }

    public async Task<LocalResolveInfo> LocalResolveAsync(int requestId, string username, string password, string? reason = null)
    {
        EnsureNotDisposed();

        var payload = new Dictionary<string, object?>
        {
            ["username"] = username,
            ["password"] = password,
            ["reason"] = reason
        };

        using var response = await _httpClient.PostAsJsonAsync($"api/authorizations/{requestId}/local-resolve", payload).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateRestExceptionAsync(response).ConfigureAwait(false);
        }

        var result = await response.Content.ReadFromJsonAsync<LocalResolveResponse>().ConfigureAwait(false)
            ?? new LocalResolveResponse();

        return new LocalResolveInfo(result.Token, result.SupervisorUserId, result.SupervisorName, result.Status);
    }

    private void OnSessionChanged()
    {
        if (Volatile.Read(ref _isDisposed) != 0 || string.IsNullOrWhiteSpace(_userSession?.Token))
        {
            return;
        }

        StartSignalRAsync().SafeFireAndForget("AuthorizationHubService.SessionChangedReconnect");
    }

    private async Task StartSignalRAsync()
    {
        if (_hubConnection is null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _signalRStartInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            for (var attempt = 1; attempt <= SignalRMaxAttempts; attempt++)
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

                    await _hubConnection.StartAsync().ConfigureAwait(false);
                    ClientStateLogger.LogInfo("[SIGNALR] Hub de autorizaciones conectado.", nameof(AuthorizationHubService));

                    // 8.151 (W3, R6/design D4a): un arranque manual exitoso tras una conexion previa
                    // (p. ej. SessionChanged) tambien es una reconexion y debe reconciliar la cola.
                    if (Interlocked.Exchange(ref _hasConnectedBefore, 1) == 1)
                    {
                        RaiseReconnected();
                    }

                    return;
                }
                catch (Exception ex)
                {
                    ClientStateLogger.LogWarning($"[SIGNALR] Fallo al conectar el hub de autorizaciones (intento {attempt}/{SignalRMaxAttempts}): {ex.Message}", nameof(AuthorizationHubService));

                    if (attempt == SignalRMaxAttempts)
                    {
                        ClientStateLogger.LogWarning($"[SIGNALR] Hub de autorizaciones no disponible tras {SignalRMaxAttempts} intentos; el flujo usa el par REST.", nameof(AuthorizationHubService));
                        return;
                    }

                    await Task.Delay(SignalRRetryDelayMs).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _signalRStartInProgress, 0);
        }
    }

    private async Task<T?> TryInvokeHubAsync<T>(string methodName, params object?[] args) where T : class
    {
        if (_hubConnection is not { State: HubConnectionState.Connected })
        {
            return null;
        }

        try
        {
            return await _hubConnection.InvokeCoreAsync<T>(methodName, args).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ClientStateLogger.LogWarning($"[SIGNALR] {methodName} fallo por socket; se usa el par REST: {ex.Message}", nameof(AuthorizationHubService));
            return null;
        }
    }

    private void RaiseEvent<T>(EventHandler<T>? handler, T payload)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        try
        {
            handler?.Invoke(this, payload);
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogWarning($"[SIGNALR] Un suscriptor de {typeof(T).Name} fallo: {ex.Message}", nameof(AuthorizationHubService));
        }
    }

    private void RaiseReconnected()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        try
        {
            Reconnected?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogWarning($"[SIGNALR] Un suscriptor de Reconnected fallo: {ex.Message}", nameof(AuthorizationHubService));
        }
    }

    /// <summary>
    /// 8.157 (SEC-07): reaccion al push de revocacion. Detiene el socket si sigue vivo (StopAsync
    /// corta el auto-reconnect: el hub no se reabre solo) y eleva ForceDisconnected una vez.
    /// Expuesto internal para pruebas (InternalsVisibleTo).
    /// </summary>
    internal async Task StopAfterForceDisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_hubConnection is { } connection)
        {
            var stopError = await TryStopHubForRevocationAsync(connection.State, () => connection.StopAsync(cancellationToken)).ConfigureAwait(false);
            if (stopError is not null)
            {
                ClientStateLogger.LogWarning($"[SIGNALR] Fallo al detener el hub de autorizaciones tras la revocación: {stopError.Message}", nameof(AuthorizationHubService));
            }
        }

        RaiseForceDisconnected();
    }

    /// <summary>
    /// 8.157 (SEC-07): detiene el hub si no esta ya desconectado; devuelve la excepcion de stop o
    /// null (fail-soft: un stop fallido no impide la notificacion). Expuesto internal para pruebas.
    /// </summary>
    internal static async Task<Exception?> TryStopHubForRevocationAsync(HubConnectionState state, Func<Task> stopAsync)
    {
        ArgumentNullException.ThrowIfNull(stopAsync);

        if (state == HubConnectionState.Disconnected)
        {
            return null;
        }

        try
        {
            await stopAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private void RaiseForceDisconnected()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _forceDisconnectRaised, 1, 0) != 0)
        {
            return;
        }

        try
        {
            ForceDisconnected?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogWarning($"[SIGNALR] Un suscriptor de ForceDisconnected fallo: {ex.Message}", nameof(AuthorizationHubService));
        }
    }

    private static AuthorizationRequestInfo ToRequestInfo(CreateAuthorizationResponse result)
    {
        var expiresAt = ToUtcOffset(result.ExpiresAt)
            ?? DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, result.RemainingLifetimeSeconds));
        return new AuthorizationRequestInfo(result.RequestId!.Value, expiresAt, result.Deduplicated, result.RemainingLifetimeSeconds);
    }

    private static DateTimeOffset? ToUtcOffset(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value.Value),
            DateTimeKind.Local => new DateTimeOffset(value.Value.ToUniversalTime()),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc))
        };
    }

    private static async Task<AuthorizationHubException> CreateRestExceptionAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var message = ApiErrorParser.FromBody(body, $"Error del servidor ({(int)response.StatusCode})");
        return new AuthorizationHubException(message, (int)response.StatusCode);
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        if (_userSession != null)
        {
            _userSession.SessionChanged -= OnSessionChanged;
        }

        if (_hubConnection != null)
        {
            try
            {
                await _hubConnection.StopAsync().ConfigureAwait(false);
            }
            catch (Exception) { }

            try
            {
                await _hubConnection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception) { }
        }

        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        // 8.6-B5: sin sync-over-async; el apagado asincrono real corre en el pool igual que
        // ExchangeRateService, porque Dispose puede venir del hilo de UI o del contenedor DI.
        try
        {
            _ = Task.Run(async () => await DisposeAsync().ConfigureAwait(false));
        }
        catch (Exception) { }
    }

    private sealed class CreateAuthorizationResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("requestId")]
        public int? RequestId { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("expiresAt")]
        public DateTime? ExpiresAt { get; set; }

        [JsonPropertyName("remainingLifetimeSeconds")]
        public int RemainingLifetimeSeconds { get; set; }

        [JsonPropertyName("deduplicated")]
        public bool Deduplicated { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private sealed class ResolveAuthorizationResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("resolvedByName")]
        public string? ResolvedByName { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private sealed class LocalResolveResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("supervisorUserId")]
        public int? SupervisorUserId { get; set; }

        [JsonPropertyName("supervisorName")]
        public string? SupervisorName { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }
}
