using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Core.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Backend.API.Middleware;

public class VersionCheckMiddleware
{
    private readonly RequestDelegate _next;

    public VersionCheckMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IOptionsMonitor<SystemSettingsOptions> optionsMonitor)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Exempt non-API requests, Swagger, OpenAPI, SignalR hubs, version-check, health, and static files
        if (context.Request.Method == "OPTIONS" ||
            !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/system/version-check", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var settings = optionsMonitor.CurrentValue;
        var clientVersionHeader = context.Request.Headers["X-Client-Version"].ToString();
        if (string.IsNullOrWhiteSpace(clientVersionHeader))
        {
            clientVersionHeader = settings.MinimumClientVersion;
        }

        bool isVersionOk = false;
        if (!string.IsNullOrWhiteSpace(clientVersionHeader) &&
            Version.TryParse(clientVersionHeader, out var clientVer) &&
            Version.TryParse(settings.MinimumClientVersion, out var minVer))
        {
            if (clientVer >= minVer)
            {
                isVersionOk = true;
            }
        }

        if (!isVersionOk)
        {
            context.Response.StatusCode = (int)HttpStatusCode.UpgradeRequired; // HTTP 426
            context.Response.ContentType = "application/json";

            var responsePayload = new
            {
                error = "ClientVersionObsolete",
                message = $"Su versión de cliente ({clientVersionHeader}) es obsoleta. Se requiere la versión {settings.MinimumClientVersion} o superior.",
                minimumClientVersion = settings.MinimumClientVersion,
                clientVersionReceived = string.IsNullOrWhiteSpace(clientVersionHeader) ? "0.0.0" : clientVersionHeader,
                updateServerUrl = settings.UpdateServerUrl
            };

            await context.Response.WriteAsync(JsonSerializer.Serialize(responsePayload));
            return;
        }

        await _next(context);
    }
}
