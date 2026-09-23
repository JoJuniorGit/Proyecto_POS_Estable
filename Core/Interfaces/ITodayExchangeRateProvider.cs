using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

public interface ITodayExchangeRateProvider
{
    Task<decimal> GetEffectiveTodayRateAsync(CancellationToken cancellationToken);
}
