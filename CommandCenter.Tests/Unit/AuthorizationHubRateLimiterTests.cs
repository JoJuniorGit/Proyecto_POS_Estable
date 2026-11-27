using System;
using System.Collections.Generic;
using Backend.API.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class AuthorizationHubRateLimiterTests
{
    [Fact]
    public void AllowsUpToLimit_ThenRejectsWithRetryAndResetsAfterWindow()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
        var limiter = CreateLimiter(time, permitLimit: 2, windowSeconds: 60);

        Assert.True(limiter.TryAcquire(7, "RequestAuthorization", out var firstRetry));
        Assert.Equal(0, firstRetry);
        Assert.True(limiter.TryAcquire(7, "RequestAuthorization", out _));

        Assert.False(limiter.TryAcquire(7, "RequestAuthorization", out var retryAfterSeconds));
        Assert.Equal(60, retryAfterSeconds);

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.True(limiter.TryAcquire(7, "RequestAuthorization", out _));
    }

    [Fact]
    public void SeparatesUsersAndOperations()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
        var limiter = CreateLimiter(time, permitLimit: 1, windowSeconds: 60);

        Assert.True(limiter.TryAcquire(1, "RequestAuthorization", out _));
        Assert.False(limiter.TryAcquire(1, "RequestAuthorization", out _));
        Assert.True(limiter.TryAcquire(2, "RequestAuthorization", out _));
        Assert.True(limiter.TryAcquire(1, "ResolveAuthorization", out _));
    }

    private static AuthorizationHubRateLimiter CreateLimiter(
        FakeTimeProvider time,
        int permitLimit,
        int windowSeconds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:AuthorizationHubPermitLimit"] = permitLimit.ToString(),
                ["RateLimiting:AuthorizationHubWindowSeconds"] = windowSeconds.ToString()
            })
            .Build();

        return new AuthorizationHubRateLimiter(configuration, time);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
