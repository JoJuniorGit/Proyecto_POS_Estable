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
    private const int MaxRetries = 3;

    public ResilienceHandler(IHealthPollingService healthPollingService)
    {
        _healthPollingService = healthPollingService;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestUri = request.RequestUri?.ToString() ?? string.Empty;
        var method = request.Method.Method;

        // Exempt /health endpoint from retry policy
        if (requestUri.EndsWith("/health", StringComparison.OrdinalIgnoreCase))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        HttpResponseMessage? response = null;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                // Re-clone request content for retries if needed
                response = await base.SendAsync(request, cancellationToken);

                if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                {
                    // Success or non-503 status code: return directly
                    return response;
                }

                // Check for Fast-Fail DB Auth Error
                var contentString = await response.Content.ReadAsStringAsync(cancellationToken);
                if (IsFatalDbAuthError(contentString))
                {
                    ClientStateLogger.LogFatalDbAuth();
                    throw new FatalDbAuthenticationException("Fallo crítico de credenciales en PostgreSQL. Se aborta la auto-recuperación.");
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

        // Retries Exhausted: Transition to HealthPollingService exclusively
        ClientStateLogger.LogRetriesExhausted(requestUri, method);
        _healthPollingService.StartPolling();

        if (response != null)
        {
            return response;
        }

        throw new ResilienceRetriesExhaustedException(requestUri, method, $"Reintentos agotados para la petición {method} {requestUri}. Transicionando a sondeo de salud.");
    }

    private static bool IsFatalDbAuthError(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        return content.Contains("DB_AUTH_FAILED", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("28P01", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("Fallo de autenticación en PostgreSQL", StringComparison.OrdinalIgnoreCase);
    }
}
