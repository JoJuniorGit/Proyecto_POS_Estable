using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
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

    // 8.211-T6-U10: a single query for the five profile keys; cache is refreshed per key after
    // the read. This avoids composing a profile from values read at different times.
    public async Task<IReadOnlyDictionary<string, string?>> GetSettingsAsync(IReadOnlyCollection<string> keys, System.Threading.CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var requested = keys.Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (requested.Count == 0)
        {
            return result;
        }

        var rows = await _context.SystemSettings.AsNoTracking()
            .Where(setting => requested.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);

        foreach (var key in requested)
        {
            rows.TryGetValue(key, out var value);
            result[key] = value;
            if (_cache is not null)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    _cache.Set(CachePrefix + key, value, new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = CacheTtl,
                        Size = 1
                    });
                }
                else
                {
                    _cache.Set(CachePrefix + key, NullSentinel, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = NegativeCacheTtl,
                        Size = 1
                    });
                }
            }
        }

        return result;
    }

    public Task SetSettingAsync(string key, string value)
        => SetSettingsAsync(new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value });

    // 8.211-T1 (S2): escritura batch atómica. Un solo SaveChangesAsync cubre todas las claves y la
    // invalidación de caché se hace después del commit, de modo que una lectura concurrente nunca
    // observe una mezcla de valores viejos y nuevos.
    public async Task SetSettingsAsync(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach (var pair in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
        }

        if (values.Count == 0)
        {
            return;
        }

        var keys = values.Keys.ToList();
        var existingSettings = (await _context.SystemSettings
                .Where(s => keys.Contains(s.Key))
                .ToListAsync())
            .ToDictionary(s => s.Key, StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        foreach (var pair in values)
        {
            if (existingSettings.TryGetValue(pair.Key, out var setting))
            {
                setting.Value = pair.Value;
                setting.LastUpdated = now;
                continue;
            }

            _context.SystemSettings.Add(new SystemSetting
            {
                Key = pair.Key,
                Value = pair.Value,
                LastUpdated = now
            });
        }

        await _context.SaveChangesAsync();

        // Invalidar la caché (efecto inmediato sin esperar TTL).
        if (_cache != null)
        {
            foreach (var pair in values)
            {
                _cache.Remove(CachePrefix + pair.Key);
            }
        }
    }
}


