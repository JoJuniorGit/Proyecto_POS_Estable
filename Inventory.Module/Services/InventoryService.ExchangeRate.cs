using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Inventory.Module.Services;

public partial class InventoryService
{
    public async Task<decimal> GetTodayExchangeRateAsync()
    {
        if (_cache != null && _cache.TryGetValue(ExchangeRateCacheKey, out decimal cachedRate) && cachedRate > 0)
        {
            return cachedRate;
        }

        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var record = await _context.ExchangeRateHistory.AsNoTracking().FirstOrDefaultAsync(r => r.Date == today);
        decimal rate = 0m;
        if (record != null && record.Rate > 0)
        {
            rate = record.Rate;
        }
        else
        {
            var lastRecord = await _context.ExchangeRateHistory.AsNoTracking().Where(r => r.Date <= today).OrderByDescending(r => r.Date).FirstOrDefaultAsync();
            rate = lastRecord?.Rate ?? 0m;
        }

        if (rate > 0)
        {
            try
            {
                _cache?.Set(ExchangeRateCacheKey, rate, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
                    Size = 1
                });
            }
            catch { }
        }
        return rate;
    }

    public void InvalidateTodayExchangeRateCache()
    {
        _cache?.Remove(ExchangeRateCacheKey);
    }
}
