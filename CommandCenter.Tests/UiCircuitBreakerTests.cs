using System;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Xunit;

namespace CommandCenter.Tests;

public class TestJitterProvider : IJitterProvider
{
    private readonly int _fixedDelayMs;

    public TestJitterProvider(int fixedDelayMs = 0)
    {
        _fixedDelayMs = fixedDelayMs;
    }

    public int GetJitterDelayMs(int minMs, int maxMs)
    {
        return _fixedDelayMs;
    }
}

public class ConcreteTestViewModel : BaseViewModel
{
    public bool Initialized { get; private set; }
    public bool Resumed { get; private set; }
    public CancellationToken LastTokenPassed { get; private set; }

    public ConcreteTestViewModel(IClientStateService clientState, IJitterProvider jitterProvider)
        : base(clientState, jitterProvider)
    {
    }

    public async Task InitializeAsync()
    {
        await SafeInitializeAsync(async (ct) =>
        {
            LastTokenPassed = ct;
            await Task.Delay(50, ct);
            Initialized = true;
        });
    }

    protected override async Task OnResumeAfterRecoveryAsync(CancellationToken cancellationToken)
    {
        LastTokenPassed = cancellationToken;
        Resumed = true;
        await Task.CompletedTask;
    }
}

public class UiCircuitBreakerTests
{
    [Fact]
    public void ClientStateService_SymmetricalEventsAndLockingWorkCorrectly()
    {
        var clientState = new ClientStateService();
        int activatedCount = 0;
        int resetCount = 0;

        clientState.FatalErrorActivated += () => activatedCount++;
        clientState.FatalErrorReset += () => resetCount++;

        Assert.False(clientState.IsFatalErrorActive);

        // First activation: returns true, fires event
        bool firstResult = clientState.TryActivateFatalError();
        Assert.True(firstResult);
        Assert.True(clientState.IsFatalErrorActive);
        Assert.Equal(1, activatedCount);

        // Second activation: returns false, no duplicate event
        bool secondResult = clientState.TryActivateFatalError();
        Assert.False(secondResult);
        Assert.Equal(1, activatedCount);

        // Reset: changes to false, fires reset event
        clientState.ResetFatalError();
        Assert.False(clientState.IsFatalErrorActive);
        Assert.Equal(1, resetCount);
    }

    [Fact]
    public async Task BaseViewModel_AbortsInFlightRequest_OnFatalErrorActivated()
    {
        var clientState = new ClientStateService();
        var jitterProvider = new TestJitterProvider(0);
        using var vm = new ConcreteTestViewModel(clientState, jitterProvider);

        var initTask = vm.InitializeAsync();

        // Trigger Fatal Error mid-execution
        clientState.TryActivateFatalError();

        await initTask; // Should complete cleanly without throwing OperationCanceledException

        Assert.False(vm.Initialized);
        Assert.True(vm.LastTokenPassed.IsCancellationRequested);
    }

    [Fact]
    public async Task BaseViewModel_ResumesAfterRecovery_WithJitterProvider()
    {
        var clientState = new ClientStateService();
        var jitterProvider = new TestJitterProvider(0); // 0ms delay for instant test
        using var vm = new ConcreteTestViewModel(clientState, jitterProvider);

        clientState.TryActivateFatalError();
        Assert.False(vm.Resumed);

        clientState.ResetFatalError();

        // Give a tiny delay for async event completion
        await Task.Delay(50);

        Assert.True(vm.Resumed);
        Assert.False(vm.LastTokenPassed.IsCancellationRequested);
    }

    [Fact]
    public void BaseViewModel_Dispose_UnsubscribesEventsAndCancelsToken()
    {
        var clientState = new ClientStateService();
        var jitterProvider = new TestJitterProvider(0);

        var vm = new ConcreteTestViewModel(clientState, jitterProvider);
        vm.Dispose();

        clientState.TryActivateFatalError();
        clientState.ResetFatalError();

        Assert.False(vm.Resumed);
    }
}
