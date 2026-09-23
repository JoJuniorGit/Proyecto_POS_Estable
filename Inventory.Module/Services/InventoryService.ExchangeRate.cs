using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Inventory.Module.Services;

public partial class InventoryService
{
    public async Task<decimal> GetTodayExchangeRateAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        if (_cache != null && _cache.TryGetValue(ExchangeRateCacheKey, out decimal cachedRate) && cachedRate > 0)
        {
            return Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(cachedRate);
        }

        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var record = await _context.ExchangeRateHistory.AsNoTracking().FirstOrDefaultAsync(r => r.Date == today, cancellationToken);
        decimal rate = 0m;
        if (record != null && record.Rate > 0)
        {
            rate = record.Rate;
        }
        else
        {
            var lastRecord = await _context.ExchangeRateHistory.AsNoTracking().Where(r => r.Date <= today).OrderByDescending(r => r.Date).FirstOrDefaultAsync(cancellationToken);
            rate = lastRecord?.Rate ?? 0m;
        }

        // 8.103: la lectura para cálculo SIEMPRE devuelve la referencia redondeada (techo 2d),
        // aunque el registro persistido conserve un valor histórico con más decimales.
        if (rate > 0)
        {
            rate = Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(rate);
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
            catch (Exception ex)
            {
                Core.Logging.AppLogger.LogWarn($"[InventoryService] Fallo al escribir la tasa BCV de hoy en la caché. {ex.Message}");
            }
        }
        return rate;
    }

    public void InvalidateTodayExchangeRateCache()
    {
        _cache?.Remove(ExchangeRateCacheKey);
    }
}
