using System.Threading.Tasks;
using Backend.API.Startup;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Middleware;

/// <summary>
/// Mientras la inicialización diferida de base de datos está en curso, solo health y
/// version-check pasan. El resto recibe 503 con Retry-After para que el cliente reintente.
/// </summary>
public sealed class StartupReadinessMiddleware
{
    private readonly RequestDelegate _next;

    public StartupReadinessMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!StartupReadiness.IsReady && !IsAllowedWhileStarting(context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "2";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Service Unavailable",
                Detail = "La base de datos se está inicializando. Reintente en unos segundos."
            });
            return;
        }

        await _next(context);
    }

    private static bool IsAllowedWhileStarting(PathString path)
        => path.Equals("/health", System.StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/health", System.StringComparison.OrdinalIgnoreCase)
           || path.StartsWithSegments("/api/version-check");
}
