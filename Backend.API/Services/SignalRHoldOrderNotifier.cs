using Backend.API.Hubs;
using Microsoft.AspNetCore.SignalR;
using Sales.Module.Interfaces;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Services;

public class SignalRHoldOrderNotifier : IHoldOrderNotifier
{
    private readonly IHubContext<ExchangeRateHub> _hubContext;

    public SignalRHoldOrderNotifier(IHubContext<ExchangeRateHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task NotifyHoldOrdersChangedAsync(CancellationToken cancellationToken = default)
    {
        await _hubContext.Clients.All.SendAsync("OnHoldSalesUpdated", cancellationToken);
    }
}
