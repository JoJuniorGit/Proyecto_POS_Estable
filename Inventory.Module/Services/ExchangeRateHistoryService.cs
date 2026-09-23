using Core.Helpers;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Inventory.Module.Services;

public class ExchangeRateHistoryService : IExchangeRateHistoryService
{
    private readonly InventoryDbContext _context;

    public ExchangeRateHistoryService(InventoryDbContext context)
    {
        _context = context;
    }

    public async Task<(decimal Rate, DateOnly Date, DateTime? UpdatedAt)> GetTodayRateWithMetadataAsync(CancellationToken cancellationToken = default)
    {
        var today = TimeZoneHelper.GetVenezuelaDate();
        var record = await _context.ExchangeRateHistory
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Date == today, cancellationToken);

        if (record == null)
        {
            record = await _context.ExchangeRateHistory
                .AsNoTracking()
                .Where(r => r.Date <= today)
                .OrderByDescending(r => r.Date)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (record == null)
        {
            return (0m, today, null);
        }

        var roundedRate = PricingCalculator.RoundExchangeRateCeiling(record.Rate);
        return (roundedRate, record.Date, record.UpdatedAt);
    }

    public async Task<List<ExchangeRateRecordDto>> GetHistoryAsync(int limit = 365, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 365);
        return await _context.ExchangeRateHistory
            .AsNoTracking()
            .OrderByDescending(r => r.Date)
            .Take(limit)
            .Select(r => new ExchangeRateRecordDto(r.Date, r.Rate, r.UpdatedAt))
            .ToListAsync(cancellationToken);
    }
}
