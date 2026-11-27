using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;

namespace Backend.API.Services;

public interface IAuthorizationHubRateLimiter
{
    /// <summary>
    /// 8.206: ventana fija por usuario y operación. Devuelve false cuando se agotó el cupo.
    /// </summary>
    bool TryAcquire(int userId, string operation, out int retryAfterSeconds);
}

/// <summary>
/// 8.206 (rate limit del hub): protege las invocaciones SignalR de autorizaciones, que no pasan
/// por el middleware HTTP de rate limiting una vez establecida la conexión.
/// </summary>
public sealed class AuthorizationHubRateLimiter : IAuthorizationHubRateLimiter
{
    private const int DefaultPermitLimit = 10;
    private const int DefaultWindowSeconds = 60;
    private const int CleanupThreshold = 1024;

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;

    public AuthorizationHubRateLimiter(IConfiguration configuration, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _permitLimit = Math.Max(1, configuration.GetValue("RateLimiting:AuthorizationHubPermitLimit", DefaultPermitLimit));
        var windowSeconds = Math.Max(1, configuration.GetValue("RateLimiting:AuthorizationHubWindowSeconds", DefaultWindowSeconds));
        _window = TimeSpan.FromSeconds(windowSeconds);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryAcquire(int userId, string operation, out int retryAfterSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        retryAfterSeconds = 0;
        if (userId <= 0)
        {
            // La identidad inválida se rechaza antes de llegar aquí; no consume cupo.
            return true;
        }

        var key = $"{userId}:{operation}";
        var now = _timeProvider.GetUtcNow();
        var window = _windows.GetOrAdd(key, _ => new Window(now));

        lock (window)
        {
            if (now - window.Start >= _window)
            {
                window.Start = now;
                window.Count = 0;
            }

            if (window.Count >= _permitLimit)
            {
                var remaining = window.Start + _window - now;
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                return false;
            }

            window.Count++;
        }

        if (_windows.Count > CleanupThreshold)
        {
            CleanupExpired(now);
        }

        return true;
    }

    private void CleanupExpired(DateTimeOffset now)
    {
        foreach (var entry in _windows)
        {
            lock (entry.Value)
            {
                if (now - entry.Value.Start >= _window)
                {
                    _windows.TryRemove(entry.Key, out _);
                }
            }
        }
    }

    private sealed class Window
    {
        public Window(DateTimeOffset start)
        {
            Start = start;
        }

        public DateTimeOffset Start { get; set; }

        public int Count { get; set; }
    }
}
