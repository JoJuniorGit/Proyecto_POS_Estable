using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.API.Services;

public class CacheMetricsLoggerService : BackgroundService
{
    private readonly ILogger<CacheMetricsLoggerService> _logger;

    public CacheMetricsLoggerService(ILogger<CacheMetricsLoggerService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        try
        {
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                var (hits, misses, hitRate) = CacheMetrics.GetSnapshot();
                _logger.LogInformation(
                    "[L2 Cache Telemetry] Hits: {Hits} | Misses: {Misses} | Hit Rate: {HitRate:F2}%",
                    hits, misses, hitRate);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
    }
}
