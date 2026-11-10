using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

/// <summary>
/// 8.211-T3c3a (S11): detects whether the installation already holds financial data whose
/// monetary interpretation would be silently rewritten by a base change. The probe is read-only:
/// it never writes, migrates or reinterprets recorded amounts.
/// </summary>
public interface IMonetaryTransitionGuard
{
    Task<bool> HasFinancialDataAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 8.211-T6-U10: serializes the guard probe and the profile save. PostgreSQL uses an advisory
    /// lock; providers without it return null (the controller proceeds without a cross-instance lock).
    /// </summary>
    Task<IAsyncDisposable?> AcquireTransitionLockAsync(CancellationToken cancellationToken = default);
}

