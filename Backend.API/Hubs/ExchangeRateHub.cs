using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Backend.API.Hubs;

[Authorize]
public class ExchangeRateHub : Hub
{
    // Hub protegido para emisión de tasas, notificaciones y cierres de venta ([8D-A1]).

    public static string UserGroup(int userId) => $"user:{userId}";

    /// <summary>
    /// 8.157 (SEC-07): espejo de AuthorizationHub — cada conexión se agrupa por usuario (user:{id})
    /// para que la revocación de credenciales pueda expulsarla con un push a ese grupo.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var idClaim = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? Context.User?.FindFirst("sub")?.Value;

        if (int.TryParse(idClaim, out var userId) && userId > 0)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        }

        await base.OnConnectedAsync();
    }
}
