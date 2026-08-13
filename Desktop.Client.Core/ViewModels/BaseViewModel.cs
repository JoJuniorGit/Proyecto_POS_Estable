using CommunityToolkit.Mvvm.ComponentModel;
using Desktop.Client.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public abstract class BaseViewModel : ObservableObject, IDisposable
{
    protected readonly IClientStateService _clientState;
    protected readonly IJitterProvider _jitterProvider;
    protected readonly IDialogService? _dialogService;

    private CancellationTokenSource _cts = new();
    private bool _disposed;

    protected CancellationToken CancellationToken => _cts.Token;

    protected BaseViewModel(IClientStateService clientState, IJitterProvider jitterProvider, IDialogService? dialogService = null)
    {
        _clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        _jitterProvider = jitterProvider ?? throw new ArgumentNullException(nameof(jitterProvider));
        _dialogService = dialogService;

        _clientState.FatalErrorActivated += OnFatalErrorActivatedInternal;
        _clientState.FatalErrorReset += OnFatalErrorResetInternal;
    }

    private void OnFatalErrorActivatedInternal()
    {
        if (_disposed) return;
        try
        {
            _cts.Cancel();
        }
        catch { }
    }

    private async void OnFatalErrorResetInternal()
    {
        if (_disposed) return;
        try
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            int jitterMs = _jitterProvider.GetJitterDelayMs(500, 2000);
            if (jitterMs > 0)
            {
                await Task.Delay(jitterMs, _cts.Token);
            }

            await OnResumeAfterRecoveryAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            /* Silenced cleanly: cancelled during jitter delay or recovery task */
        }
        catch (Exception ex) when (ex is not FatalErrorException)
        {
            /* Silenced: non-fatal UI exception handling */
        }
    }

    protected async Task SafeInitializeAsync(Func<CancellationToken, Task> initTask)
    {
        if (_clientState.IsFatalErrorActive || _disposed) return;
        try
        {
            await initTask(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            /* Silenced cleanly: cancelled by Dispose or circuit breaker activation */
        }
        catch (Exception ex) when (ex is not FatalErrorException)
        {
            /* Silenced: non-fatal UI exception handling */
        }
    }

    protected virtual async Task OnResumeAfterRecoveryAsync(CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _clientState.FatalErrorActivated -= OnFatalErrorActivatedInternal;
            _clientState.FatalErrorReset -= OnFatalErrorResetInternal;

            try
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            catch { }
        }

        _disposed = true;
    }
}
