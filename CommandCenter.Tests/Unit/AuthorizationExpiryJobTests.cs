using Backend.API.Jobs;
using Backend.API.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.150-T6 (spec S5 / design D6): barrido periodico de expiraciones. El job delega cada tick en
/// <see cref="IAuthorizationCoordinator"/> (la auditoria y el push viven en el coordinator), no
/// debe morir por un tick fallido y debe detenerse en cancelacion sin excepcion. Las aserciones
/// se apoyan en senales, nunca en precision temporal.
/// </summary>
public class AuthorizationExpiryJobTests
{
    private static IConfiguration BuildConfiguration(int? expirySweepSeconds)
    {
        var values = new Dictionary<string, string?>();
        if (expirySweepSeconds.HasValue)
        {
            values["Authorization:ExpirySweepSeconds"] = expirySweepSeconds.Value.ToString();
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ServiceProvider BuildProvider(Mock<IAuthorizationCoordinator> coordinator)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => coordinator.Object);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task StartedJob_CallsExpireStaleAtLeastOnce()
    {
        var coordinator = new Mock<IAuthorizationCoordinator>();
        var firstSweep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator
            .Setup(c => c.ExpireStaleAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                firstSweep.TrySetResult();
                return Task.FromResult(0);
            });

        await using var provider = BuildProvider(coordinator);
        var job = new AuthorizationExpiryJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<AuthorizationExpiryJob>>(),
            BuildConfiguration(expirySweepSeconds: null));

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);
        await firstSweep.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await job.StopAsync(CancellationToken.None);

        coordinator.Verify(c => c.ExpireStaleAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task FailingSweep_IsCaughtAndLogged_AndDoesNotStopSubsequentSweeps()
    {
        var coordinator = new Mock<IAuthorizationCoordinator>();
        var secondSweep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int sweepCalls = 0;
        coordinator
            .Setup(c => c.ExpireStaleAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                int call = Interlocked.Increment(ref sweepCalls);
                if (call == 1)
                {
                    throw new InvalidOperationException("fallo simulado del barrido");
                }

                secondSweep.TrySetResult();
                return Task.FromResult(0);
            });

        var logger = new Mock<ILogger<AuthorizationExpiryJob>>();
        await using var provider = BuildProvider(coordinator);
        var job = new AuthorizationExpiryJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            logger.Object,
            BuildConfiguration(expirySweepSeconds: 1));

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);
        await secondSweep.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await job.StopAsync(CancellationToken.None);

        Assert.True(sweepCalls >= 2, $"Se esperaban al menos 2 barridos; se observaron {sweepCalls}.");
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Cancellation_StopsLoopWithoutThrowing()
    {
        var coordinator = new Mock<IAuthorizationCoordinator>();
        var firstSweep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator
            .Setup(c => c.ExpireStaleAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                firstSweep.TrySetResult();
                return Task.FromResult(0);
            });

        await using var provider = BuildProvider(coordinator);
        var job = new AuthorizationExpiryJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<AuthorizationExpiryJob>>(),
            BuildConfiguration(expirySweepSeconds: 120));

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);
        await firstSweep.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cts.Cancel();
        await job.StopAsync(CancellationToken.None);
        await job.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ConfiguredInterval_RepeatsSweeps()
    {
        var coordinator = new Mock<IAuthorizationCoordinator>();
        var secondSweep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int sweepCalls = 0;
        coordinator
            .Setup(c => c.ExpireStaleAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref sweepCalls) >= 2)
                {
                    secondSweep.TrySetResult();
                }

                return Task.FromResult(0);
            });

        await using var provider = BuildProvider(coordinator);
        var job = new AuthorizationExpiryJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<AuthorizationExpiryJob>>(),
            BuildConfiguration(expirySweepSeconds: 1));

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);
        // El segundo barrido debe llegar dentro de 4 s: si el intervalo configurado (1 s) se
        // ignorara y aplicara el default de 5 s, la senal no arribaria.
        await secondSweep.Task.WaitAsync(TimeSpan.FromSeconds(4));
        cts.Cancel();
        await job.StopAsync(CancellationToken.None);

        Assert.True(sweepCalls >= 2, $"Se esperaban al menos 2 barridos con intervalo configurado de 1 s; se observaron {sweepCalls}.");
    }
}
