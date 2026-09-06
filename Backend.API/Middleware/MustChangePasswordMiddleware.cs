using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Backend.API.Middleware;

/// <summary>
/// Gating global de seguridad: impide la ejecución de endpoints protegidos de negocio
/// si el usuario autenticado tiene pendiente el cambio de su contraseña temporal (MustChangePassword == true).
/// Solo permite invocar /api/auth/change-password y /api/auth/logout.
/// </summary>
public class MustChangePasswordMiddleware
{
    private readonly RequestDelegate _next;

    public MustChangePasswordMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() != null)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path;
        if (path.StartsWithSegments("/api/auth/change-password", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/api/auth/logout", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (context.User?.Identity?.IsAuthenticated == true)
        {
            var mustChangeClaim = context.User.FindFirst("must_change_password")?.Value;
            if (string.Equals(mustChangeClaim, "true", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                var payload = new
                {
                    requiresPasswordChange = true,
                    message = "Debe cambiar su contraseña antes de continuar realizando operaciones en el sistema."
                };
                await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
                return;
            }
        }

        await _next(context);
    }
}
