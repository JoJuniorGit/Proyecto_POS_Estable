using System;

namespace Desktop.Client.Services;

public class ClientStateService : IClientStateService
{
    private readonly object _fatalErrorLock = new object();
    public bool IsFatalErrorActive { get; private set; }

    public event Action? FatalErrorActivated;
    public event Action? FatalErrorReset;

    public bool TryActivateFatalError()
    {
        lock (_fatalErrorLock)
        {
            if (IsFatalErrorActive) return false;
            IsFatalErrorActive = true;
        }
        FatalErrorActivated?.Invoke();
        return true;
    }

    public void ResetFatalError()
    {
        lock (_fatalErrorLock)
        {
            if (!IsFatalErrorActive) return;
            IsFatalErrorActive = false;
        }
        FatalErrorReset?.Invoke();
    }
}
