using Desktop.Client.Services;
using Microsoft.AspNetCore.SignalR.Client;
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

    // ── 8.157 (SEC-07): ForceDisconnect (revocación de sesión) ────────────

    [Fact]
    public void ExchangeRateService_ForceDisconnectEvent_MatchesServerPushConstant()
    {
        Assert.Equal("ForceDisconnect", ExchangeRateService.ForceDisconnectEvent);
    }

    [Fact]
    public async Task ExchangeRateService_StopAfterForceDisconnectAsync_RaisesForceDisconnectedOnceAndIsIdempotent()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:5000/") };
        var service = new ExchangeRateService(httpClient, enableRealtime: false);
        var raised = 0;
        service.ForceDisconnected += (_, _) => raised++;

        await service.StopAfterForceDisconnectAsync();
        await service.StopAfterForceDisconnectAsync();
        await service.StopAfterForceDisconnectAsync();

        Assert.Equal(1, raised);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task ExchangeRateService_StopAfterForceDisconnectAsync_AfterDispose_DoesNotRaise()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:5000/") };
        var service = new ExchangeRateService(httpClient, enableRealtime: false);
        var raised = 0;
        service.ForceDisconnected += (_, _) => raised++;

        await service.DisposeAsync();
        await service.StopAfterForceDisconnectAsync();

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task ExchangeRateService_TryStopHubForRevocationAsync_ActiveState_InvokesStop()
    {
        var stopCalls = 0;

        var stopError = await ExchangeRateService.TryStopHubForRevocationAsync(
            HubConnectionState.Connected,
            () =>
            {
                stopCalls++;
                return Task.CompletedTask;
            });

        Assert.Equal(1, stopCalls);
        Assert.Null(stopError);
    }

    [Fact]
    public async Task ExchangeRateService_TryStopHubForRevocationAsync_DisconnectedState_SkipsStop()
    {
        var stopCalls = 0;

        var stopError = await ExchangeRateService.TryStopHubForRevocationAsync(
            HubConnectionState.Disconnected,
            () =>
            {
                stopCalls++;
                return Task.CompletedTask;
            });

        Assert.Equal(0, stopCalls);
        Assert.Null(stopError);
    }

    [Fact]
    public async Task ExchangeRateService_TryStopHubForRevocationAsync_StopThrows_ReturnsErrorFailSoft()
    {
        var expected = new InvalidOperationException("socket roto");

        var stopError = await ExchangeRateService.TryStopHubForRevocationAsync(
            HubConnectionState.Reconnecting,
            () => Task.FromException(expected));

        Assert.Same(expected, stopError);
    }
}
