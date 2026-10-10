using System.Security.Claims;
using Backend.API.Services;
using Core.Logging;
using Microsoft.AspNetCore.SignalR;

namespace Backend.API.Hubs;

/// <summary>
/// 8.157 (SEC-07, REQ-HREV-02): filtro global de hubs que revalida el sello de seguridad en cada
/// conexión e invocación con la ventana estricta de 5 s (forceImmediateCheck). Un sello ausente o
/// revocado aborta la conexión y lanza HubException con los mensajes EXACTOS del
/// SecurityStampValidationMiddleware; un sello vigente continúa el pipeline sin cambios. Así las
/// reconexiones automáticas de clientes con tokens revocados quedan rechazadas.
/// </summary>
public class StampValidationHubFilter : IHubFilter
{
    public const string MissingStampMessage = "El token no posee un sello de seguridad válido. Por favor inicie sesión nuevamente.";
    public const string InvalidStampMessage = "La sesión ha sido revocada o las credenciales del usuario cambiaron. Inicie sesión nuevamente.";

    private readonly ISecurityStampValidator _validator;

    public StampValidationHubFilter(ISecurityStampValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validator = validator;
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        await ValidateAsync(context.Context);
        await next(context);
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        await ValidateAsync(invocationContext.Context);
        return await next(invocationContext);
    }

    public Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next) => next(context, exception);

    private async Task ValidateAsync(HubCallerContext context)
    {
        var userIdClaim = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                       ?? context.User?.FindFirst("sub")?.Value;

        if (!int.TryParse(userIdClaim, out var userId) || userId <= 0)
        {
            Reject(context, userId: null, MissingStampMessage);
            throw new HubException(MissingStampMessage);
        }

        var stampClaim = context.User?.FindFirst("security_stamp")?.Value;
        if (string.IsNullOrWhiteSpace(stampClaim))
        {
            Reject(context, userId, MissingStampMessage);
            throw new HubException(MissingStampMessage);
        }

        if (!await _validator.ValidateStampAsync(userId, stampClaim, forceImmediateCheck: true))
        {
            Reject(context, userId, InvalidStampMessage);
            throw new HubException(InvalidStampMessage);
        }
    }

    private static void Reject(HubCallerContext context, int? userId, string message)
    {
        AppLogger.LogWarn(
            $"[AUTH] Hub request rejected: {message} ConnectionId={context.ConnectionId}, UserId={userId?.ToString() ?? "missing"}",
            "StampValidationHubFilter");
        context.Abort();
    }
}
