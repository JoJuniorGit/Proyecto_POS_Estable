using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Threading.Tasks;

namespace Inventory.Module.Services;

public class SystemSettingsService : ISystemSettingsService
{
    private const string CachePrefix = "system_setting_";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private readonly InventoryDbContext _context;
    private readonly IMemoryCache? _cache;

    public SystemSettingsService(InventoryDbContext context, IMemoryCache? cache = null)
    {
        _context = context;
        _cache = cache;
    }

    // 8.7-M6: caché de 30 s por clave (mismo patrón del micro-caché de tasa) para no consultar
    // SelectedTimeZoneId/RateDeviationTolerancePct en cada request (antes era 4-5 queries).
    public async Task<string?> GetSettingAsync(string key)
    {
        var cacheKey = CachePrefix + key;
        if (_cache != null && _cache.TryGetValue(cacheKey, out string? cached) && cached != null)
        {
            return cached;
        }

        var setting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == key);
        var value = setting?.Value;

        // 8.9-L12: no cachear string.Empty — un key inexistente no debe quedar "falsamente
        // resuelto" durante el TTL devolviendo "" en lugar de null. Los keys ausentes se
        // re-consultan en la siguiente petición.
        if (_cache != null)
        {
            if (!string.IsNullOrEmpty(value))
            {
                _cache.Set(cacheKey, value, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = CacheTtl,
                    Size = 1
                });
            }
            else
            {
                _cache.Remove(cacheKey);
            }
        }

        return value;
    }

    public async Task SetSettingAsync(string key, string value)
    {
        var setting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting == null)
        {
            setting = new SystemSetting
            {
                Key = key,
                Value = value,
                LastUpdated = DateTime.UtcNow
            };
            _context.SystemSettings.Add(setting);
        }
        else
        {
            setting.Value = value;
            setting.LastUpdated = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        // Invalidar la caché (efecto inmediato sin esperar TTL).
        if (_cache != null)
        {
            _cache.Remove(CachePrefix + key);
        }
    }
}
