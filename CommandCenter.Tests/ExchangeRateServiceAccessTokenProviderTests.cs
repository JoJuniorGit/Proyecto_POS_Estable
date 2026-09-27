using System;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests;

public class ExchangeRateServiceAccessTokenProviderTests
{
    private static UserDto CreateUser() => new() { Id = 1, Name = "Cajero", Role = UserRole.Cashier };

    [Fact]
    public async Task CreateAccessTokenProvider_ReturnsCurrentSessionTokenAndReflectsSessionChanges()
    {
        var session = new UserSession();
        var provider = ExchangeRateService.CreateAccessTokenProvider(new Uri("https://192.168.1.50:5001/"), session);

        Assert.Null(await provider());

        session.SetUser(CreateUser(), "token-1");
        Assert.Equal("token-1", await provider());

        session.Logout();
        Assert.Null(await provider());

        session.SetUser(CreateUser(), "token-2");
        Assert.Equal("token-2", await provider());
    }

    [Fact]
    public async Task CreateAccessTokenProvider_NonLoopbackHttp_ReturnsNullEvenWithSession()
    {
        var session = new UserSession();
        session.SetUser(CreateUser(), "token-1");

        var provider = ExchangeRateService.CreateAccessTokenProvider(new Uri("http://192.168.1.50:5000/"), session);

        Assert.Null(await provider());
    }

    [Fact]
    public async Task CreateAccessTokenProvider_LoopbackHttp_ReturnsSessionToken()
    {
        var session = new UserSession();
        session.SetUser(CreateUser(), "token-1");

        var provider = ExchangeRateService.CreateAccessTokenProvider(new Uri("http://localhost:5000/"), session);

        Assert.Equal("token-1", await provider());
    }

    [Fact]
    public async Task CreateAccessTokenProvider_WithoutSession_ReturnsNull()
    {
        var provider = ExchangeRateService.CreateAccessTokenProvider(new Uri("https://192.168.1.50:5001/"), null);

        Assert.Null(await provider());
    }

    [Fact]
    public void CreateAccessTokenProvider_NullHubUri_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ExchangeRateService.CreateAccessTokenProvider(null!, new UserSession()));
    }
}
