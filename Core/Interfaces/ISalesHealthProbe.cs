using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

public interface ISalesHealthProbe
{
    Task<bool> CanConnectAsync(CancellationToken cancellationToken = default);
    Task<(int AppliedCount, int PendingCount)> GetMigrationStatusAsync(CancellationToken cancellationToken = default);
}
