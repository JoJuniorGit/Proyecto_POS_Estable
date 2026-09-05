using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Backend.API.Middleware;

public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment? _env;

    public SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment? env = null)
    {
        _next = next;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        if (!headers.ContainsKey("X-Content-Type-Options"))
        {
            headers["X-Content-Type-Options"] = "nosniff";
        }

        if (!headers.ContainsKey("X-Frame-Options"))
        {
            headers["X-Frame-Options"] = "DENY";
        }

        if (!headers.ContainsKey("X-XSS-Protection"))
        {
            headers["X-XSS-Protection"] = "1; mode=block";
        }

        if (!headers.ContainsKey("Referrer-Policy"))
        {
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        }

        if (!headers.ContainsKey("Content-Security-Policy"))
        {
            bool isDev = _env?.IsDevelopment() ?? false;
            string connectSrc = isDev ? "connect-src 'self' http://localhost:* ws: wss:;" : "connect-src 'self' ws: wss:;";
            string scriptSrc = isDev ? "script-src 'self' 'unsafe-inline';" : "script-src 'self';";

            headers["Content-Security-Policy"] = $"default-src 'self'; {scriptSrc} style-src 'self' 'unsafe-inline'; img-src 'self' data:; {connectSrc}";
        }

        await _next(context);
    }
}

