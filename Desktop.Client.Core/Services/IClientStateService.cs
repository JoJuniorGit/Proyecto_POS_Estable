using System;

namespace Desktop.Client.Services;

public interface IClientStateService
{
    bool IsFatalErrorActive { get; }
    bool TryActivateFatalError();
    void ResetFatalError();
    event Action? FatalErrorActivated;
    event Action? FatalErrorReset;
}
