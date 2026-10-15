using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Backend.API.Hubs;

[Authorize]
public class ExchangeRateHub : Hub
{
    // Hub protegido para emisión de tasas, notificaciones y cierres de venta ([8D-A1]).

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.Collections.Concurrent.ConcurrentDictionary<string, byte>> UserConnections = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ConnectionUsers = new();

    public static string UserGroup(int userId) => $"user:{userId}";

    internal static System.Collections.Generic.IReadOnlyCollection<string> DrainUserConnections(int userId)
    {
        if (!UserConnections.TryRemove(userId, out var set))
        {
            return System.Array.Empty<string>();
        }

        foreach (var connectionId in set.Keys)
        {
            ConnectionUsers.TryRemove(connectionId, out _);
        }

        return set.Keys.ToArray();
    }

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
            var userSet = UserConnections.GetOrAdd(userId, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, byte>());
            userSet[Context.ConnectionId] = 0;
            ConnectionUsers[Context.ConnectionId] = userId;

            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (ConnectionUsers.TryRemove(Context.ConnectionId, out var userId)
            && UserConnections.TryGetValue(userId, out var set))
        {
            set.TryRemove(Context.ConnectionId, out _);
        }

        return base.OnDisconnectedAsync(exception);
    }
}
