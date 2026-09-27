using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

public interface IInventoryHealthProbe
{
    Task<decimal?> GetTodayRateAsync(CancellationToken cancellationToken = default);
    Task<decimal?> GetLatestRateAsync(CancellationToken cancellationToken = default);
}
