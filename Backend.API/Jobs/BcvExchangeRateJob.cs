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
        _logger.LogInformation("BCV Exchange Rate Background Service is starting.");

        // We use a periodic timer to check every few hours.
        using var timer = new PeriodicTimer(TimeSpan.FromHours(4));

        try
        {
            // Initial run at startup
            // Wait a few seconds to let DB and other services initialize properly, avoiding startup race conditions
            await Task.Delay(5000, stoppingToken);

            await SyncRateAsync(stoppingToken);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SyncRateAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("BCV Exchange Rate Background Service is stopping.");
        }
    }

    private async Task SyncRateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var scraperService = scope.ServiceProvider.GetRequiredService<BcvScraperService>();
            var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<ExchangeRateHub>>();

            var rate = await scraperService.GetOfficialUsdRateAsync(cancellationToken);
            if (rate.HasValue)
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var existing = await dbContext.ExchangeRateHistory.FirstOrDefaultAsync(r => r.Date == today, cancellationToken);
                
                bool changed = false;

                if (existing != null)
                {
                    if (existing.Rate != rate.Value)
                    {
                        existing.Rate = rate.Value;
                        existing.UpdatedAt = DateTime.UtcNow;
                        changed = true;
                    }
                }
                else
                {
                    dbContext.ExchangeRateHistory.Add(new ExchangeRateHistory
                    {
                        Date = today,
                        Rate = rate.Value,
                        UpdatedAt = DateTime.UtcNow
                    });
                    changed = true;
                }

                if (changed)
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("System exchange rate updated to {Rate}", rate.Value);

                    // Broadcast to clients via SignalR
                    await hubContext.Clients.All.SendAsync("ReceiveRateUpdate", rate.Value, cancellationToken);
                }
                else
                {
                    _logger.LogInformation("BCV rate hasn't changed from today's value ({Rate}). No database update needed.", rate.Value);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing BCV exchange rate in background job.");
        }
    }
}
