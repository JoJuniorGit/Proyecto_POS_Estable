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
}
