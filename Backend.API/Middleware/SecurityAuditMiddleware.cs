using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Core.Logging;
using Microsoft.AspNetCore.Http;

namespace Backend.API.Middleware;

public class SecurityAuditMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityAuditMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        int statusCode = context.Response.StatusCode;
        if (statusCode == StatusCodes.Status401Unauthorized || statusCode == StatusCodes.Status403Forbidden)
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var method = context.Request.Method;
            // Strictly use Path.Value without QueryString to prevent any accidental leakage of sensitive query parameters
            var path = context.Request.Path.Value ?? "/";
            
            var user = context.User.Identity?.IsAuthenticated == true
                ? (context.User.Identity.Name ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "AuthenticatedUser")
                : "Anonymous";

            var role = context.User.FindFirst(ClaimTypes.Role)?.Value ?? "None";

            var reason = statusCode == StatusCodes.Status401Unauthorized
                ? "Token ausente, inválido o expirado."
                : "Permisos insuficientes para el rol solicitado.";

            var logEntry = $"IP={ip} | METHOD={method} | PATH={path} | USER={user} | ROLE={role} | STATUS={statusCode} | REASON={reason}";
            AppLogger.LogSecurityAudit(logEntry);
        }
    }
}
