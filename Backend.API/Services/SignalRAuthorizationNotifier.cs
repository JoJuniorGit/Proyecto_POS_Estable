using System.Text.Json;
using Backend.API.Hubs;
using Core.Entities;
using Microsoft.AspNetCore.SignalR;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;

namespace Backend.API.Services;

/// <summary>
/// 8.150 (T4, design D4): traduce los eventos de dominio a SignalR. Las solicitudes se emiten
/// solo a role:elevated; la resolucion viaja al solicitante (con token) y a los elevados (sin
/// token, para cerrar sus modales); la expiracion viaja a ambos grupos.
/// </summary>
public class SignalRAuthorizationNotifier : IAuthorizationNotifier
{
    public const string RequestedEvent = "AuthorizationRequested";
    public const string ResolvedEvent = "AuthorizationResolved";
    public const string ExpiredEvent = "AuthorizationExpired";

    private readonly IHubContext<AuthorizationHub> _hubContext;

    public SignalRAuthorizationNotifier(IHubContext<AuthorizationHub> hubContext)
    {
        ArgumentNullException.ThrowIfNull(hubContext);
        _hubContext = hubContext;
    }

    public async Task NotifyRequestCreatedAsync(AuthorizationRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = new
        {
            requestId = request.Id,
            actionType = request.ActionType.ToString(),
            saleId = request.SaleId,
            requestedByName = request.RequestedByName,
            terminal = request.Terminal,
            context = ParseDisplayContext(request.ContextJson),
            createdAt = request.CreatedAt,
            expiresAt = request.ExpiresAt
        };

        await _hubContext.Clients.Group(AuthorizationHub.ElevatedGroup)
            .SendAsync(RequestedEvent, payload, cancellationToken);
    }

    public async Task NotifyResolvedAsync(AuthorizationRequestDto request, string? token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requesterPayload = new
        {
            requestId = request.Id,
            status = request.Status.ToString(),
            approved = request.Status == AuthorizationStatus.Approved,
            resolvedByName = request.ResolvedByName,
            resolutionMode = request.ResolutionMode?.ToString(),
            reason = request.ResolutionReason,
            token
        };

        await _hubContext.Clients.Group(AuthorizationHub.UserGroup(request.RequestedByUserId))
            .SendAsync(ResolvedEvent, requesterPayload, cancellationToken);

        var closurePayload = new
        {
            requestId = request.Id,
            status = request.Status.ToString(),
            resolvedByName = request.ResolvedByName,
            resolutionMode = request.ResolutionMode?.ToString(),
            reason = request.ResolutionReason
        };

        await _hubContext.Clients.Group(AuthorizationHub.ElevatedGroup)
            .SendAsync(ResolvedEvent, closurePayload, cancellationToken);
    }

    public async Task NotifyExpiredAsync(AuthorizationRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = new
        {
            requestId = request.Id,
            status = request.Status.ToString(),
            expiresAt = request.ExpiresAt,
            expiredAt = request.ResolvedAt
        };

        await _hubContext.Clients.Group(AuthorizationHub.UserGroup(request.RequestedByUserId))
            .SendAsync(ExpiredEvent, payload, cancellationToken);
        await _hubContext.Clients.Group(AuthorizationHub.ElevatedGroup)
            .SendAsync(ExpiredEvent, payload, cancellationToken);
    }

    private static JsonElement? ParseDisplayContext(string contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(contextJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
