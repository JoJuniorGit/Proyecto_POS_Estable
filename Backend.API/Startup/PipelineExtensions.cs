using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Backend.API.Hubs;

namespace Backend.API.Startup;

/// <summary>
/// Configura el pipeline de middleware HTTP del backend. Se extrajo del monolito Program.cs
/// (hallazgo B11) para mantener el punto de entrada legible y separar la configuración de
/// servicios de la composición del pipeline de peticiones.
/// </summary>
public static class PipelineExtensions
{
    public static WebApplication ConfigureBackendPipeline(this WebApplication app, System.Security.Cryptography.X509Certificates.X509Certificate2? httpsCert, int httpsPort)
    {
        // 8.7-L2: validación del encabezado Host contra la whitelist calculada (AllowedHosts).
        // Debe ejecutarse antes del manejo de encabezados reenviados para no confiar en Host externos.
        app.UseHostFiltering();

        // Normalizar encabezados reenviados (X-Forwarded-For / X-Forwarded-Proto) al inicio del pipeline
        app.UseForwardedHeaders();

        // Global Unhandled Exception & DB Resilience Middleware
        app.UseMiddleware<Backend.API.Middleware.GlobalExceptionHandlerMiddleware>();

        // Security Headers (M-07)
        app.UseMiddleware<Backend.API.Middleware.SecurityHeadersMiddleware>();

        app.UseMiddleware<Backend.API.Middleware.RequestMetricsMiddleware>();

        // 8.7-B10: Con certificado presente, en Producción se fuerza HTTPS para el cliente Web:
        //  - HSTS dirige al browser a HTTPS desde la primera respuesta segura.
        //  - Redirección 307 HTTP→HTTPS para request con X-Client-Platform: Web (preserva el body
        //    del login). El escritorio (desktop) sigue sobre HTTP en LAN por compatibilidad, pero la
        //    cookie pos_jwt es Secure siempre (solo viaja por HTTPS).
        if (httpsCert != null && !app.Environment.IsDevelopment())
        {
            app.UseHsts();
            app.Use(async (context, next) =>
            {
                var platform = context.Request.Headers["X-Client-Platform"].ToString();
                if (!context.Request.IsHttps
                    && string.Equals(platform, "Web", StringComparison.OrdinalIgnoreCase))
                {
                    var host = context.Request.Host.Host;
                    var target = $"https://{host}:{httpsPort}{context.Request.Path}{context.Request.QueryString}";
                    context.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status307TemporaryRedirect;
                    context.Response.Headers.Location = target;
                    return;
                }
                await next();
            });
        }

        // HTTP Response Compression (Brotli/Gzip)
        app.UseResponseCompression();

        // Serve static files for integrated React Frontend build (wwwroot)
        app.UseDefaultFiles();
        app.UseStaticFiles();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint("/openapi/v1.json", "Administrador API v1");
            });
        }

        app.UseCors();
        app.UseRateLimiter();

        // Version Compatibility Handshake Middleware
        app.UseMiddleware<Backend.API.Middleware.VersionCheckMiddleware>();

        // Security Audit Middleware for 401 & 403 events
        app.UseMiddleware<Backend.API.Middleware.SecurityAuditMiddleware>();

        app.UseAuthentication();
        app.UseMiddleware<Backend.API.Middleware.SecurityStampValidationMiddleware>();
        app.UseAuthorization();
        app.UseMiddleware<Backend.API.Middleware.MustChangePasswordMiddleware>();

        app.MapControllers().RequireRateLimiting("GeneralApiRateLimit");
        app.MapHub<ExchangeRateHub>("/hubs/exchange-rate").RequireAuthorization();

        // Fallback SPA routing with strict API 404 segregation (H-API-16)
        app.MapFallback(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
            {
                context.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status404NotFound;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Status = Microsoft.AspNetCore.Http.StatusCodes.Status404NotFound,
                    Title = "Not Found",
                    Detail = $"Ruta de API no encontrada: {context.Request.Path}"
                });
                return;
            }

            var webRoot = app.Environment.WebRootPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot");
            var indexPath = System.IO.Path.Combine(webRoot, "index.html");
            if (System.IO.File.Exists(indexPath))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(indexPath);
            }
            else
            {
                context.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status404NotFound;
            }
        });

        return app;
    }
}
