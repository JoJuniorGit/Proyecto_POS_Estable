using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Hubs;
using Backend.API.Services;
using Core.Entities;
using Inventory.Module.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.API.Jobs;

public class BcvExchangeRateJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BcvExchangeRateJob> _logger;

    public BcvExchangeRateJob(IServiceProvider serviceProvider, ILogger<BcvExchangeRateJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BCV Exchange Rate Background Service is starting with a 2-hour periodic sync cycle.");

        // Periodic timer every 2 hours as per system specifications
        using var timer = new PeriodicTimer(TimeSpan.FromHours(2));

        try
        {
            // Initial run at startup: wait 5 seconds for database and infrastructure initialization
            await Task.Delay(5000, stoppingToken);

            await SyncRateAsync(stoppingToken);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SyncRateAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("BCV Exchange Rate Background Service is stopping gracefully.");
        }
    }

    public const long BcvSyncLockId = 7483921048576102L;

    public async Task SyncRateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

            bool isNpgsql = dbContext.Database.IsNpgsql();
            bool lockAcquired = false;

            if (isNpgsql)
            {
                await dbContext.Database.OpenConnectionAsync(cancellationToken);
                try
                {
                    lockAcquired = await dbContext.Database
                        .SqlQueryRaw<bool>("SELECT pg_try_advisory_lock({0}) AS \"Value\"", BcvSyncLockId)
                        .FirstOrDefaultAsync(cancellationToken);

                    if (!lockAcquired)
                    {
                        _logger.LogInformation("[BCV Job] Otra instancia del backend ya está ejecutando la sincronización del BCV (advisory lock activo). Omitiendo este ciclo.");
                        return;
                    }

                    await ExecuteSyncInternalAsync(scope, dbContext, cancellationToken);
                }
                    finally
                    {
                        if (lockAcquired)
                        {
                            try
                            {
                                await dbContext.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock({0})", BcvSyncLockId);
                            }
                            catch (Exception unlockEx)
                            {
                                _logger.LogWarning(unlockEx, "[BCV Job] Advertencia al liberar advisory lock del BCV.");
                            }
                        }

                        try
                        {
                            await dbContext.Database.CloseConnectionAsync();
                        }
                        catch (Exception closeEx)
                        {
                            _logger.LogWarning(closeEx, "[BCV Job] Advertencia al cerrar conexión del advisory lock.");
                        }
                    }
            }
            else
            {
                await ExecuteSyncInternalAsync(scope, dbContext, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BCV Scraper Audit] Error syncing BCV exchange rate in background job: {Message}. Active rate is preserved.", ex.Message);
        }
    }

    private async Task ExecuteSyncInternalAsync(IServiceScope scope, InventoryDbContext dbContext, CancellationToken cancellationToken)
    {
        var scraperService = scope.ServiceProvider.GetRequiredService<BcvScraperService>();
        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<ExchangeRateHub>>();

        var rawRate = await scraperService.GetOfficialUsdRateAsync(cancellationToken);
        if (!rawRate.HasValue)
        {
            _logger.LogWarning("[BCV Scraper Audit] Scraper returned null. BCV site might be unreachable or unresponsive. Active system rate is preserved.");
            return;
        }

        // Defensive range validation
        if (rawRate.Value <= 0 || rawRate.Value >= 1_000_000m)
        {
            _logger.LogWarning("[BCV Scraper Audit] Scraped rate {RawRate} was rejected because it is outside the valid range (0, 1000000). Current system rate is preserved.", rawRate.Value);
            return;
        }

        // Ceiling rounding to 2 decimal places (redondeo hacia arriba: ej. 804.6301 -> 804.64)
        decimal roundedRate = Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(rawRate.Value);

        // Resolve date according to Venezuela legal time zone (America/Caracas / UTC-4)
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var existing = await dbContext.ExchangeRateHistory.FirstOrDefaultAsync(r => r.Date == today, cancellationToken);
        
        bool changed = false;

        if (existing != null)
        {
            if (existing.Rate != roundedRate)
            {
                existing.Rate = roundedRate;
                existing.UpdatedAt = DateTime.UtcNow;
                changed = true;
            }
        }
        else
        {
            dbContext.ExchangeRateHistory.Add(new ExchangeRateHistory
            {
                Date = today,
                Rate = roundedRate,
                UpdatedAt = DateTime.UtcNow
            });
            changed = true;
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("System exchange rate updated to {Rate} for Venezuela date {Date}", roundedRate, today);

            var inventoryService = scope.ServiceProvider.GetRequiredService<Core.Interfaces.IInventoryService>();
            inventoryService.InvalidateTodayExchangeRateCache();

            // Recalculate OnHold sales
            var salesService = scope.ServiceProvider.GetRequiredService<Sales.Module.Interfaces.ISalesService>();
            await salesService.RecalculateOnHoldSalesAsync(roundedRate);

            // Broadcast to clients via SignalR
            await hubContext.Clients.All.SendAsync("ReceiveRateUpdate", roundedRate, cancellationToken);
            await hubContext.Clients.All.SendAsync("OnHoldSalesUpdated", cancellationToken);
        }
        else
        {
            _logger.LogInformation("BCV rate hasn't changed from today's value ({Rate}). No database update needed.", roundedRate);
        }
    }
}
