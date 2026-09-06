using System;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Backend.API.Attributes;
using Backend.API.Services;
using Core.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Backend.API.Middleware;

/// <summary>
/// Validates user security stamps for all authenticated requests.
/// Employs a 10-second micro-cache sliding window for standard operations,
/// and forces immediate database verification for endpoints decorated with [RequireSecurityStampValidation].
/// </summary>
public class SecurityStampValidationMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityStampValidationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ISecurityStampValidator validator)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() != null)
        {
            await _next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/api/auth/logout", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (context.User?.Identity?.IsAuthenticated == true)
        {
            var idClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                       ?? context.User.FindFirst("sub")?.Value;

            if (int.TryParse(idClaim, out int userId))
            {
                var stampClaim = context.User.FindFirst("security_stamp")?.Value;

                if (string.IsNullOrWhiteSpace(stampClaim))
                {
                    AppLogger.LogWarn($"[AUTH] Request rejected: missing security_stamp claim for UserId={userId} on {context.Request.Path}");
                    await WriteUnauthorizedResponse(context, "El token no posee un sello de seguridad válido. Por favor inicie sesión nuevamente.");
                    return;
                }

                bool forceImmediate = endpoint?.Metadata.GetMetadata<RequireSecurityStampValidationAttribute>() != null;

                bool isValid = await validator.ValidateStampAsync(userId, stampClaim, forceImmediate);
                if (!isValid)
                {
                    AppLogger.LogWarn($"[AUTH] Token revoked or stamp invalid for UserId={userId} on {context.Request.Path} (forceImmediate={forceImmediate})");
                    await WriteUnauthorizedResponse(context, "La sesión ha sido revocada o las credenciales del usuario cambiaron. Inicie sesión nuevamente.");
                    return;
                }
            }
        }

        await _next(context);
    }

    private static async Task WriteUnauthorizedResponse(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";

        var problem = new
        {
            type = "https://tools.ietf.org/html/rfc7235#section-3.1",
            title = "Unauthorized",
            status = StatusCodes.Status401Unauthorized,
            detail = message
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(problem));
    }
}
