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
    private const string NullSentinel = "__SYSTEM_SETTING_NULL__";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(15);
    private readonly InventoryDbContext _context;
    private readonly IMemoryCache? _cache;

    public SystemSettingsService(InventoryDbContext context, IMemoryCache? cache = null)
    {
        _context = context;
        _cache = cache;
    }

    // 8.7-M6 / PERF-03: caché de 30 s por clave con negative caching (15 s) para claves inexistentes
    // evitando DB hammering repetitivo en configuraciones opcionales o no inicializadas.
    public async Task<string?> GetSettingAsync(string key)
    {
        var cacheKey = CachePrefix + key;
        if (_cache != null && _cache.TryGetValue(cacheKey, out string? cached) && cached != null)
        {
            return cached == NullSentinel ? null : cached;
        }

        var setting = await _context.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        var value = setting?.Value;

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
                _cache.Set(cacheKey, NullSentinel, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = NegativeCacheTtl,
                    Size = 1
                });
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
