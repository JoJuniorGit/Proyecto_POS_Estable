using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Jobs;

/// <summary>
/// A background hosted service that periodically deletes or archives old StockMovement records to prevent database bloating.
/// It runs natively within the ASP.NET Core DI container.
/// </summary>
public class StockMovementArchiverJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StockMovementArchiverJob> _logger;

    // Run every 24 hours
    private readonly TimeSpan _period = TimeSpan.FromHours(24);

    // Retention policy: Keep records for 1 year
    private readonly TimeSpan _retentionPeriod = TimeSpan.FromDays(365);

    public StockMovementArchiverJob(IServiceProvider serviceProvider, ILogger<StockMovementArchiverJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ArchiveOldRecordsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while cleaning up old StockMovement records.");
            }
        }
    }

    private async Task ArchiveOldRecordsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        var cutoffDate = DateTime.UtcNow.Subtract(_retentionPeriod);

        _logger.LogInformation("Starting StockMovement maintenance. Deleting records older than {CutoffDate}", cutoffDate);

        // Execute bulk delete safely directly on DB to avoid fetching thousands of entries into RAM
        var oldRecords = context.StockMovements.Where(m => m.MovementDate < cutoffDate);
        int deletedCount = await oldRecords.ExecuteDeleteAsync(stoppingToken);

        _logger.LogInformation("StockMovement maintenance completed. Removed {DeletedCount} old records.", deletedCount);
    }
}
