using System.Diagnostics;
using System.Threading.Tasks;
using Backend.API.Metrics;
using Microsoft.AspNetCore.Http;

namespace Backend.API.Middleware;

public class RequestMetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RequestMetricsRegistry _registry;

    public RequestMetricsMiddleware(RequestDelegate next, RequestMetricsRegistry registry)
    {
        _next = next;
        _registry = registry;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();
            var method = context.Request.Method ?? "UNKNOWN";
            var path = context.Request.Path.Value ?? "/";
            _registry.Record(method, path, stopwatch.Elapsed.TotalMilliseconds, context.Response.StatusCode);
        }
    }
}