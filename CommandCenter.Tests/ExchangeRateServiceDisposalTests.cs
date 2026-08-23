using Desktop.Client.Services;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests;

public class ExchangeRateServiceDisposalTests
{
    [Fact]
    public void ExchangeRateService_Dispose_MultipleCalls_AreIdempotentAndDoNotThrow()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:5000/") };
        var service = new ExchangeRateService(httpClient);

        // First call
        var ex1 = Record.Exception(() => service.Dispose());
        Assert.Null(ex1);

        // Second call
        var ex2 = Record.Exception(() => service.Dispose());
        Assert.Null(ex2);

        // Third call
        var ex3 = Record.Exception(() => service.Dispose());
        Assert.Null(ex3);
    }

    [Fact]
    public async Task ExchangeRateService_DisposeAsync_MultipleCalls_AreIdempotentAndDoNotThrow()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:5000/") };
        var service = new ExchangeRateService(httpClient);

        // First call
        var ex1 = await Record.ExceptionAsync(async () => await service.DisposeAsync());
        Assert.Null(ex1);

        // Second call
        var ex2 = await Record.ExceptionAsync(async () => await service.DisposeAsync());
        Assert.Null(ex2);

        // Third call
        var ex3 = await Record.ExceptionAsync(async () => await service.DisposeAsync());
        Assert.Null(ex3);
    }

    [Fact]
    public async Task ExchangeRateService_Dispose_FollowedBy_DisposeAsync_DoesNotThrow()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:5000/") };
        var service = new ExchangeRateService(httpClient);

        // Synchronous dispose first
        var ex1 = Record.Exception(() => service.Dispose());
        Assert.Null(ex1);

        // Asynchronous dispose second
        var ex2 = await Record.ExceptionAsync(async () => await service.DisposeAsync());
        Assert.Null(ex2);
    }

    [Fact]
    public async Task ExchangeRateService_DisposeAsync_FollowedBy_Dispose_DoesNotThrow()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:5000/") };
        var service = new ExchangeRateService(httpClient);

        // Asynchronous dispose first
        var ex1 = await Record.ExceptionAsync(async () => await service.DisposeAsync());
        Assert.Null(ex1);

        // Synchronous dispose second
        var ex2 = Record.Exception(() => service.Dispose());
        Assert.Null(ex2);
    }
}
