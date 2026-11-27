using Backend.API.Hubs;
using Microsoft.AspNetCore.SignalR;
using Sales.Module.Interfaces;

namespace Backend.API.Services;

/// <summary>
/// 8.212-WU5: SignalR fan-out for committed cash drawer changes. The caller invokes this only after
/// the service operation returned, never while the session advisory lock is held.
/// </summary>
public sealed class SignalRCashDrawerNotifier : ICashDrawerChangedNotifier
{
    private readonly IHubContext<ExchangeRateHub> _hubContext;

    public SignalRCashDrawerNotifier(IHubContext<ExchangeRateHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task NotifyCashDrawerChangedAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        await _hubContext.Clients.All.SendAsync("CashDrawerChanged", sessionId, cancellationToken);
    }
}
