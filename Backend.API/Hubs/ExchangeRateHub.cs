using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Backend.API.Hubs;

[Authorize]
public class ExchangeRateHub : Hub
{
    // Hub protegido para emisión de tasas, notificaciones y cierres de venta ([8D-A1]).
}
