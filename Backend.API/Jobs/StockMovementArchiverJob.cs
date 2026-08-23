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

    private readonly TimeSpan _period;
    private readonly TimeSpan _retentionPeriod;

    public StockMovementArchiverJob(
        IServiceProvider serviceProvider,
        ILogger<StockMovementArchiverJob> logger,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        int intervalHours = configuration != null && int.TryParse(configuration["Archiver:IntervalHours"], out var hours) && hours > 0
            ? hours
            : 24;
        _period = TimeSpan.FromHours(intervalHours);

        int retentionDays = configuration != null && int.TryParse(configuration["Archiver:RetentionDays"], out var days) && days > 0
            ? days
            : 365;
        _retentionPeriod = TimeSpan.FromDays(retentionDays);
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
