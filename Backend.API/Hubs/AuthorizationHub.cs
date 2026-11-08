using System.Security.Claims;
using Backend.API.Services;
using Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Sales.Module.DTOs;
using Sales.Module.Services;

namespace Backend.API.Hubs;

/// <summary>
/// 8.150 (T4, design D4): hub de autorizaciones remotas. Agrupa cada conexion por usuario
/// (user:{sub}) y, si el rol es Admin/Manager, tambien en role:elevated; create/resolve delegan
/// en <see cref="IAuthorizationCoordinator"/> con la identidad tomada del JWT del contexto.
/// </summary>
[Authorize]
public class AuthorizationHub : Hub
{
    public const string ElevatedGroup = "role:elevated";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.Collections.Concurrent.ConcurrentDictionary<string, byte>> UserConnections = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ConnectionUsers = new();

    private readonly IAuthorizationCoordinator _coordinator;
    private readonly IAuthorizationHubRateLimiter? _rateLimiter;

    public AuthorizationHub(
        IAuthorizationCoordinator coordinator,
        IAuthorizationHubRateLimiter? rateLimiter = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
        _rateLimiter = rateLimiter;
    }

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

    public override async Task OnConnectedAsync()
    {
        if (TryReadIdentity(Context.User, out var identity))
        {
            var userSet = UserConnections.GetOrAdd(identity.UserId, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, byte>());
            userSet[Context.ConnectionId] = 0;
            ConnectionUsers[Context.ConnectionId] = identity.UserId;

            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(identity.UserId));

            if (AuthorizationService.IsElevatedRole(identity.Role))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, ElevatedGroup);
            }
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

    // E2E: al responder, OnConnectedAsync (alta al grupo) ya terminó. Evita que un push
    // enviado inmediatamente después de StartAsync se pierda por una carrera de suscripción.
    [HubMethodName("Ping")]
    public Task PingAsync() => Task.CompletedTask;

    [HubMethodName("RequestAuthorization")]
    public async Task<HubAuthorizationRequestResult> RequestAuthorizationAsync(RequestAuthorizationContract request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryReadIdentity(Context.User, out var identity))
        {
            return new HubAuthorizationRequestResult(false);
        }

        if (IsRateLimited(identity.UserId, "RequestAuthorization", out var requestRetryAfterSeconds))
        {
            return new HubAuthorizationRequestResult(
                false,
                Message: $"Demasiadas solicitudes de autorización. Reintente en {requestRetryAfterSeconds} segundos.");
        }

        var result = await _coordinator.CreateAsync(
            request,
            identity.UserId,
            identity.Name,
            identity.Role,
            Context.ConnectionAborted);

        if (result.Request is not null
            && result.Outcome is CreateAuthorizationOutcome.Created or CreateAuthorizationOutcome.Deduplicated)
        {
            return new HubAuthorizationRequestResult(
                true,
                result.Request.Id,
                result.Request.Status.ToString(),
                result.Request.ExpiresAt,
                result.Request.RemainingLifetimeSeconds,
                result.Outcome == CreateAuthorizationOutcome.Deduplicated);
        }

        return new HubAuthorizationRequestResult(false, Message: result.Message);
    }

    [HubMethodName("ResolveAuthorization")]
    public async Task<HubAuthorizationResolveResult> ResolveAuthorizationAsync(
        int requestId,
        bool approved,
        string? reason = null)
    {
        if (!TryReadIdentity(Context.User, out var identity) || !AuthorizationService.IsElevatedRole(identity.Role))
        {
            // La elevacion la exige el design D4: un no elevado recibe un resultado denegado,
            // nunca una excepcion ni la posibilidad de resolver.
            return new HubAuthorizationResolveResult(false);
        }

        if (IsRateLimited(identity.UserId, "ResolveAuthorization", out var resolveRetryAfterSeconds))
        {
            return new HubAuthorizationResolveResult(
                false,
                Message: $"Demasiadas resoluciones de autorización. Reintente en {resolveRetryAfterSeconds} segundos.");
        }

        var result = await _coordinator.ResolveAsync(
            requestId,
            identity.UserId,
            identity.Name,
            approved,
            reason,
            Context.ConnectionAborted);

        if (result.Outcome == ResolveAuthorizationOutcome.Resolved && result.Request is not null)
        {
            return new HubAuthorizationResolveResult(
                true,
                result.Request.Status.ToString(),
                result.Request.ResolvedByName,
                result.Request.ResolutionReason);
        }

        return new HubAuthorizationResolveResult(
            false,
            result.Request?.Status.ToString(),
            result.Request?.ResolvedByName,
            result.Request?.ResolutionReason,
            result.Message);
    }

    private bool IsRateLimited(int userId, string operation, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        return _rateLimiter is not null
            && !_rateLimiter.TryAcquire(userId, operation, out retryAfterSeconds);
    }

    private static bool TryReadIdentity(ClaimsPrincipal? user, out HubIdentity identity)
    {
        identity = default;

        if (user is null)
        {
            return false;
        }

        var idClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value;
        if (!int.TryParse(idClaim, out var userId) || userId <= 0)
        {
            return false;
        }

        var roleClaim = user.FindFirst(ClaimTypes.Role)?.Value;
        if (!Enum.TryParse<UserRole>(roleClaim, ignoreCase: true, out var role))
        {
            return false;
        }

        var name = user.FindFirst(ClaimTypes.Name)?.Value;
        identity = new HubIdentity(userId, string.IsNullOrWhiteSpace(name) ? $"Usuario {userId}" : name, role);
        return true;
    }

    private readonly record struct HubIdentity(int UserId, string Name, UserRole Role);
}

/// <summary>
/// 8.150 (T4): resultado compacto de creacion expuesto al cliente del hub ({requestId, expiresAt}).
/// </summary>
public sealed record HubAuthorizationRequestResult(
    bool Success,
    int? RequestId = null,
    string? Status = null,
    DateTime? ExpiresAt = null,
    int RemainingLifetimeSeconds = 0,
    bool Deduplicated = false,
    string? Message = null);

/// <summary>
/// 8.150 (T4): resultado de resolucion remota con el mensaje exacto de carrera/expiracion.
/// </summary>
public sealed record HubAuthorizationResolveResult(
    bool Success,
    string? Status = null,
    string? ResolvedByName = null,
    string? Reason = null,
    string? Message = null);
