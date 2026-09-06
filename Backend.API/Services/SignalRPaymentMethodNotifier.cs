using Backend.API.Hubs;
using Microsoft.AspNetCore.SignalR;
using Sales.Module.Interfaces;
using System.Threading.Tasks;

namespace Backend.API.Services;

/// <summary>
/// Emite el evento OnPaymentMethodsUpdated a todos los clientes SignalR conectados
/// cuando se produce cualquier cambio en el catálogo de métodos de pago.
/// </summary>
public class SignalRPaymentMethodNotifier : IPaymentMethodNotifier
{
    private readonly IHubContext<ExchangeRateHub> _hubContext;

    public SignalRPaymentMethodNotifier(IHubContext<ExchangeRateHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task NotifyPaymentMethodsUpdatedAsync()
    {
        await _hubContext.Clients.All.SendAsync("OnPaymentMethodsUpdated");
    }
}
