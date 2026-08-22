using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Logging;

namespace Desktop.Client.Services;

public class FatalDbAuthenticationException : Exception
{
    public FatalDbAuthenticationException(string message) : base(message) { }
}

public class ResilienceRetriesExhaustedException : Exception
{
    public string RequestUri { get; }
    public string HttpMethod { get; }

    public ResilienceRetriesExhaustedException(string requestUri, string httpMethod, string message)
        : base(message)
    {
        RequestUri = requestUri;
        HttpMethod = httpMethod;
    }
}

public class ResilienceHandler : DelegatingHandler
{
    private readonly IHealthPollingService _healthPollingService;
    private readonly IClientStateService? _clientStateService;
    private const int MaxRetries = 3;

    public ResilienceHandler(IHealthPollingService healthPollingService, IClientStateService? clientStateService = null)
    {
        _healthPollingService = healthPollingService;
        _clientStateService = clientStateService;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestUri = request.RequestUri?.ToString() ?? string.Empty;
        var method = request.Method.Method;

        bool isExemptEndpoint = requestUri.EndsWith("/health", StringComparison.OrdinalIgnoreCase) ||
                                requestUri.Contains("/api/auth/", StringComparison.OrdinalIgnoreCase) ||
                                requestUri.Contains("/api/version/", StringComparison.OrdinalIgnoreCase) ||
                                requestUri.Contains("/api/exchange-rate/today", StringComparison.OrdinalIgnoreCase);

        // Exempt health, auth, and version check endpoints from retry policy and circuit breaker fail-fast
        if (isExemptEndpoint)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // Circuit Breaker Fail-Fast: if the system is in active fatal error mode, short-circuit immediately
        if (_clientStateService?.IsFatalErrorActive == true)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                ReasonPhrase = "Circuit breaker active – backend unavailable",
                RequestMessage = request,
                Content = new StringContent("{\"message\":\"El servidor backend no está disponible temporalmente (cortacircuitos activo).\"}", System.Text.Encoding.UTF8, "application/json")
            };
        }

        HttpResponseMessage? response = null;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                response = await base.SendAsync(request, cancellationToken);

                // Only 5xx errors (infrastructure/server failures) trigger retry policy.
                // 2xx, 3xx, and 4xx (business/validation errors) return immediately without triggering the circuit breaker.
                if ((int)response.StatusCode < 500)
                {
                    return response;
                }

                // Buffer stream so reading does not exhaust the body for caller or subsequent inspections
                if (response.Content != null)
                {
                    await response.Content.LoadIntoBufferAsync();
                    var contentString = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (IsFatalDbAuthError(contentString))
                    {
                        ClientStateLogger.LogFatalDbAuth();
                        _clientStateService?.TryActivateFatalError();
                        throw new FatalDbAuthenticationException("Fallo crítico de credenciales en PostgreSQL. Se aborta la auto-recuperación.");
                    }
                }

                // Log transient retry
                ClientStateLogger.LogRetry(attempt, MaxRetries, requestUri, method);

                if (attempt < MaxRetries)
                {
                    int delayMs = (int)Math.Pow(2, attempt) * 1000; // 2s, 4s, 8s
                    await Task.Delay(delayMs, cancellationToken);
                }
            }
            catch (FatalDbAuthenticationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                ClientStateLogger.LogRetry(attempt, MaxRetries, requestUri, method);

                if (attempt < MaxRetries)
                {
                    int delayMs = (int)Math.Pow(2, attempt) * 1000;
                    await Task.Delay(delayMs, cancellationToken);
                }
            }
        }

        // Retries Exhausted: Activate fatal error state and transition to HealthPollingService exclusively
        ClientStateLogger.LogRetriesExhausted(requestUri, method);
        _clientStateService?.TryActivateFatalError();
        _healthPollingService.StartPolling();

        if (response != null)
        {
            return response;
        }

        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            ReasonPhrase = "Reintentos agotados – backend no disponible",
            RequestMessage = request,
            Content = new StringContent("{\"message\":\"Reintentos agotados. El servidor backend no responde.\"}", System.Text.Encoding.UTF8, "application/json")
        };
    }

    private static bool IsFatalDbAuthError(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        return content.Contains("DB_AUTH_FAILED", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("28P01", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("Fallo de autenticación en PostgreSQL", StringComparison.OrdinalIgnoreCase);
    }
}
