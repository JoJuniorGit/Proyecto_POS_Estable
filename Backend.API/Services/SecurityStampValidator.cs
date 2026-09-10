using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Sales.Module.Data;
using Core.Logging;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("CommandCenter.Tests")]

namespace Backend.API.Services;

/// <summary>
/// Validates user security stamps to enable immediate or micro-cached token revocation.
/// Two-tier cache:
///  - Mixed fingerprint (45 s): standard requests (sliding window).
///  - Strict window (5 s): sensitive monetary endpoints that set forceImmediateCheck limit the
///    DB hit to once every 5 seconds per user instead of once per request (8.7-M8).
/// </summary>
public class SecurityStampValidator : ISecurityStampValidator
{
    private readonly SalesDbContext _db;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StrictWindow = TimeSpan.FromSeconds(5);
    private sealed record CachedStamp(string Value, DateTime IssuedAtUtc);

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

        if (_cache.TryGetValue(cacheKey, out CachedStamp? cached))
        {
            var maxAge = ResolveCacheWindow(forceImmediateCheck);
            if (cached != null
                && string.Equals(cached.Value, tokenStamp, StringComparison.Ordinal)
                && DateTime.UtcNow - cached.IssuedAtUtc < maxAge)
            {
                return true;
            }
        }

        var user = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.SecurityStamp, u.IsActive })
            .FirstOrDefaultAsync();

        if (user == null || !user.IsActive)
        {
            _cache.Remove(cacheKey);
            return false;
        }

        if (string.Equals(user.SecurityStamp, tokenStamp, StringComparison.Ordinal))
        {
            _cache.Set(cacheKey, new CachedStamp(user.SecurityStamp, DateTime.UtcNow), new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ResolveCacheWindow(forceImmediateCheck),
                Size = 1
            });
            return true;
        }

        // Stamp mismatch: session has been revoked or credentials changed
        _cache.Remove(cacheKey);
        return false;
    }

    internal static TimeSpan ResolveCacheWindow(bool forceImmediateCheck)
        => forceImmediateCheck ? StrictWindow : CacheDuration;

    public void InvalidateUserStamp(int userId)
    {
        if (userId > 0)
        {
            try
            {
                _cache.Remove($"sec_stamp_{userId}");
            }
            catch (Exception ex)
            {
                AppLogger.LogWarn($"[CACHE] Failed to remove security stamp cache entry for userId={userId}: {ex.Message}");
            }
        }
    }

    public async Task RevokeUserStampAsync(int userId)
    {
        if (userId <= 0)
        {
            return;
        }

        // 8.7-B1: rota el stamp en BD → cualquier JWT emitido antes queda revocado (stamp mismatch).
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            await _db.SaveChangesAsync();
        }

        InvalidateUserStamp(userId);
    }
}