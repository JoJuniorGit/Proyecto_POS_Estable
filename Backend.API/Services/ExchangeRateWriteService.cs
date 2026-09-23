using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Hubs;
using Core.Entities;
using Core.Logging;
using Inventory.Module.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Interfaces;

namespace Backend.API.Services;

public class ExchangeRateWriteService : IExchangeRateWriteService
{
    private readonly InventoryDbContext _context;
    private readonly Core.Interfaces.IInventoryService _inventoryService;
    private readonly ISalesService? _salesService;
    private readonly IHubContext<ExchangeRateHub>? _hubContext;

    public ExchangeRateWriteService(
        InventoryDbContext context,
        Core.Interfaces.IInventoryService inventoryService,
        ISalesService? salesService = null,
        IHubContext<ExchangeRateHub>? hubContext = null)
    {
        _context = context;
        _inventoryService = inventoryService;
        _salesService = salesService;
        _hubContext = hubContext;
    }

    public async Task<bool> UpsertTodayRateAsync(decimal roundedRate, CancellationToken cancellationToken = default)
    {
        roundedRate = Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(roundedRate);

        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var existing = await _context.ExchangeRateHistory
            .FirstOrDefaultAsync(r => r.Date == today, cancellationToken);

        bool changed;
        if (existing != null)
        {
            changed = existing.Rate != roundedRate;
            existing.Rate = roundedRate;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _context.ExchangeRateHistory.Add(new ExchangeRateHistory
            {
                Date = today,
                Rate = roundedRate,
                UpdatedAt = DateTime.UtcNow
            });
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        await _context.SaveChangesAsync(cancellationToken);

        _inventoryService.InvalidateTodayExchangeRateCache();

        if (_salesService != null)
        {
            await _salesService.RecalculateOnHoldSalesAsync(roundedRate, cancellationToken);
        }

        if (_hubContext != null)
        {
            await _hubContext.Clients.All.SendAsync("ReceiveRateUpdate", roundedRate, cancellationToken);
            await _hubContext.Clients.All.SendAsync("OnHoldSalesUpdated", cancellationToken);
        }

        return true;
    }
}

public static class ExchangeRateResolver
{
    public static async Task<decimal> ReadEffectiveTodayRateAsync(
        InventoryDbContext inventoryContext,
        ICashDrawerService cashDrawerService,
        CancellationToken cancellationToken)
    {
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var record = await inventoryContext.ExchangeRateHistory
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Date == today, cancellationToken);

        if (record == null)
        {
            record = await inventoryContext.ExchangeRateHistory
                .AsNoTracking()
                .Where(r => r.Date <= today)
                .OrderByDescending(r => r.Date)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (record != null && record.Rate > 0)
            return Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(record.Rate);

        var activeSession = await cashDrawerService.GetActiveSessionAsync(cancellationToken);
        if (activeSession != null && activeSession.OpeningExchangeRate > 0)
            return Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(activeSession.OpeningExchangeRate);

        return 0m;
    }
}