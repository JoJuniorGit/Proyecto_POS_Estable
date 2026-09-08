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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StockMovementArchiverJob> _logger;

    private readonly TimeSpan _period;
    private readonly TimeSpan _retentionPeriod;

    public StockMovementArchiverJob(
        IServiceScopeFactory scopeFactory,
        ILogger<StockMovementArchiverJob> logger,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    {
        _scopeFactory = scopeFactory;
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
        try
        {
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
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
    }

    private async Task ArchiveOldRecordsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        var cutoffDate = DateTime.UtcNow.Subtract(_retentionPeriod);

        _logger.LogInformation("Starting StockMovement maintenance. Archiving records older than {CutoffDate}", cutoffDate);

        int totalArchived = 0;
        const int batchSize = 1000;

        while (!stoppingToken.IsCancellationRequested)
        {
            var batch = await context.StockMovements
                .AsNoTracking()
                .Where(m => m.MovementDate < cutoffDate)
                .OrderBy(m => m.MovementDate)
                .Take(batchSize)
                .Select(m => new
                {
                    m.Id,
                    m.ProductId,
                    m.QuantityChange,
                    m.NewStockLevel,
                    m.Reason,
                    m.MovementDate,
                    m.UserId
                })
                .ToListAsync(stoppingToken);

            if (batch.Count == 0)
            {
                break;
            }

            var archiveEntries = batch.Select(m => new Core.Entities.StockMovementArchive
            {
                OriginalMovementId = m.Id,
                ProductId = m.ProductId,
                QuantityChange = m.QuantityChange,
                NewStockLevel = m.NewStockLevel,
                Reason = m.Reason,
                MovementDate = m.MovementDate,
                UserId = m.UserId,
                ArchivedAtUtc = DateTime.UtcNow
            }).ToList();

            // 8.7-L4: INSERT de archivo y DELETE de original en la MISMA transacción atómica.
            // Un fallo a mitad de lote revierte ambos, evitando pérdida de datos.
            // 8.16-H02: la transacción manual debe vivir DENTRO de CreateExecutionStrategy().ExecuteAsync()
            // para no lanzar InvalidOperationException bajo NpgsqlRetryingExecutionStrategy en producción.
            await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = context.Database.IsRelational()
                    ? await context.Database.BeginTransactionAsync(stoppingToken)
                    : null;

                context.StockMovements_Archive.AddRange(archiveEntries);
                await context.SaveChangesAsync(stoppingToken);

                var batchIds = batch.Select(m => m.Id).ToList();
                if (context.Database.IsRelational())
                {
                    await context.StockMovements
                        .Where(m => batchIds.Contains(m.Id))
                        .ExecuteDeleteAsync(stoppingToken);
                }
                else
                {
                    var entitiesToDelete = await context.StockMovements
                        .Where(m => batchIds.Contains(m.Id))
                        .ToListAsync(stoppingToken);
                    context.StockMovements.RemoveRange(entitiesToDelete);
                    await context.SaveChangesAsync(stoppingToken);
                }

                if (transaction != null)
                {
                    await transaction.CommitAsync(stoppingToken);
                }
            });

            totalArchived += batch.Count;
            await Task.Delay(100, stoppingToken);
        }

        if (totalArchived > 0)
        {
            _logger.LogInformation("StockMovement maintenance completed. Archived and removed {TotalArchived} old records.", totalArchived);
            Core.Logging.AppLogger.LogSecurityAudit($"[STOCK_MOVEMENT_ARCHIVE] Se archivaron y purgaron {totalArchived} movimientos de stock anteriores a {cutoffDate:O}.");
        }
    }
}
