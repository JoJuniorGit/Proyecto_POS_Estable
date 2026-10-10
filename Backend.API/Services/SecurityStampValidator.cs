using System;
using System.Linq;
using System.Threading.Tasks;
using Backend.API.Hubs;
using Microsoft.AspNetCore.SignalR;
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
/// 8.157 (SEC-07): además empuja "ForceDisconnect" a las conexiones SignalR del usuario
/// (grupos user:{id} de ambos hubs) cuando se revocan credenciales.
/// </summary>
public class SecurityStampValidator : ISecurityStampValidator
{
    private const string ForceDisconnectEvent = "ForceDisconnect";

    private readonly SalesDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IHubContext<AuthorizationHub>? _authorizationHub;
    private readonly IHubContext<ExchangeRateHub>? _exchangeRateHub;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StrictWindow = TimeSpan.FromSeconds(5);
    private sealed record CachedStamp(string Value, DateTime IssuedAtUtc);

    /// <summary>
    /// Los IHubContexts son opcionales: las construcciones directas sin SignalR (tests unitarios
    /// del validador) siguen compilando y el push queda como no-op.
    /// </summary>
    public SecurityStampValidator(
        SalesDbContext db,
        IMemoryCache cache,
        IHubContext<AuthorizationHub>? authorizationHub = null,
        IHubContext<ExchangeRateHub>? exchangeRateHub = null)
    {
        _db = db;
        _cache = cache;
        _authorizationHub = authorizationHub;
        _exchangeRateHub = exchangeRateHub;
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

        // 8.157 (SEC-07): la revocación también expulsa los sockets activos del usuario.
        await DisconnectUserSessionsAsync(userId);
    }

    /// <summary>
    /// 8.157 (SEC-07, REQ-HREV-01): invalida la caché del sello y empuja "ForceDisconnect"
    /// (sin payload) a las conexiones del usuario en ambos hubs.
    /// </summary>
    public async Task InvalidateUserSessionsAsync(int userId)
    {
        InvalidateUserStamp(userId);
        await DisconnectUserSessionsAsync(userId);
    }

    /// <summary>
    /// 8.157 (SEC-07): envía "ForceDisconnect" al grupo user:{id} de cada hub con contexto
    /// inyectado. Fail-soft (D4): el fallo del transporte se registra y NUNCA propaga, porque la
    /// revocación de credenciales no debe depender del canal en tiempo real.
    /// </summary>
    public async Task DisconnectUserSessionsAsync(int userId)
    {
        if (userId <= 0)
        {
            return;
        }

        string group = AuthorizationHub.UserGroup(userId);
        await TrySendForceDisconnectAsync(_authorizationHub, group, userId, nameof(AuthorizationHub));
        await TrySendForceDisconnectAsync(_exchangeRateHub, group, userId, nameof(ExchangeRateHub));
    }

    private static async Task TrySendForceDisconnectAsync<T>(
        IHubContext<T>? hub,
        string group,
        int userId,
        string hubName) where T : Hub
    {
        if (hub is null)
        {
            return;
        }

        try
        {
            await hub.Clients.Group(group).SendAsync(ForceDisconnectEvent);
        }
        catch (Exception ex)
        {
            AppLogger.LogWarn(
                $"[SIGNALR] ForceDisconnect push failed for UserId={userId} on hub {hubName}: {ex.Message}",
                "SecurityStampValidator");
        }
    }
}