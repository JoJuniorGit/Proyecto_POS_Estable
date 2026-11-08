namespace Sales.Module.Interfaces;

/// <summary>
/// 8.212-WU5: publishes a post-commit drawer change so other terminals refresh their balances.
/// Implementations must never be called inside the session advisory lock.
/// </summary>
public interface ICashDrawerChangedNotifier
{
    Task NotifyCashDrawerChangedAsync(int sessionId, CancellationToken cancellationToken = default);
}
