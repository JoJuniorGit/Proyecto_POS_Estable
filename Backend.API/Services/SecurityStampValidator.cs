using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Sales.Module.Data;
using Core.Logging;

namespace Backend.API.Services;

/// <summary>
/// Validates user security stamps to enable immediate or micro-cached token revocation.
/// Enforces a 5-10 second micro-cache sliding window for standard requests,
/// and immediate database verification for sensitive monetary endpoints.
/// </summary>
public class SecurityStampValidator : ISecurityStampValidator
{
    private readonly SalesDbContext _db;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    public SecurityStampValidator(SalesDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<bool> ValidateStampAsync(int userId, string tokenStamp, bool forceImmediateCheck = false)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(tokenStamp))
        {
            return false;
        }

        string cacheKey = $"sec_stamp_{userId}";

        if (!forceImmediateCheck)
        {
            try
            {
                if (_cache.TryGetValue(cacheKey, out string? cachedStamp) && cachedStamp != null)
                {
                    return string.Equals(cachedStamp, tokenStamp, StringComparison.Ordinal);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarn($"[CACHE] Failed to read security stamp from cache: {ex.Message}");
            }
        }

        var user = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.SecurityStamp, u.IsActive })
            .FirstOrDefaultAsync();

        if (user == null || !user.IsActive)
        {
            try { _cache.Remove(cacheKey); } catch { }
            return false;
        }

        if (string.Equals(user.SecurityStamp, tokenStamp, StringComparison.Ordinal))
        {
            try
            {
                _cache.Set(cacheKey, user.SecurityStamp, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = CacheDuration,
                    Size = 1
                });
            }
            catch (Exception ex)
            {
                AppLogger.LogWarn($"[CACHE] Failed to set security stamp in cache: {ex.Message}");
            }
            return true;
        }

        // Stamp mismatch: session has been revoked or credentials changed
        try { _cache.Remove(cacheKey); } catch { }
        return false;
    }

    public void InvalidateUserStamp(int userId)
    {
        if (userId > 0)
        {
            try
            {
                _cache.Remove($"sec_stamp_{userId}");
            }
            catch { }
        }
    }
}
