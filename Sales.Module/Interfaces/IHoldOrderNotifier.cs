using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

public interface IHoldOrderNotifier
{
    Task NotifyHoldOrdersChangedAsync(CancellationToken cancellationToken = default);
}
