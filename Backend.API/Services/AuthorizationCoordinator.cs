using Backend.API.Hubs;
using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;

namespace Backend.API.Services;

/// <summary>
/// 8.150 (T3, design D3/D4): compone la maquina de estados (T2), el token efimero y el notifier.
/// El DbContext solo se usa para enumerar las solicitudes vencidas antes del barrido: T2 expira y
/// audita en lote, pero devuelve un conteo, y el push de expiracion necesita cada DTO.
/// </summary>
public class AuthorizationCoordinator : IAuthorizationCoordinator
{
    private readonly IAuthorizationService _authorizationService;
    private readonly IAuthorizationTokenService _tokenService;
    private readonly IAuthorizationNotifier _notifier;
    private readonly SalesDbContext _db;
    private readonly TimeSpan _tokenTtl;

    public AuthorizationCoordinator(
        IAuthorizationService authorizationService,
        IAuthorizationTokenService tokenService,
        IAuthorizationNotifier notifier,
        SalesDbContext db,
        TimeSpan? tokenTtl = null)
    {
        ArgumentNullException.ThrowIfNull(authorizationService);
        ArgumentNullException.ThrowIfNull(tokenService);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(db);

        _tokenTtl = tokenTtl ?? TimeSpan.FromSeconds(60);
        if (_tokenTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenTtl), "El TTL del token debe ser positivo.");
        }

        _authorizationService = authorizationService;
        _tokenService = tokenService;
        _notifier = notifier;
        _db = db;
    }

    public TimeSpan TokenTtl => _tokenTtl;

    public async Task<CreateAuthorizationResult> CreateAsync(
        RequestAuthorizationContract request,
        int requesterUserId,
        string requesterName,
        UserRole requesterRole,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(requesterName);

        var operation = new ManualPriceOverrideContext
        {
            ProductId = request.ProductId,
            Quantity = request.Quantity,
            CustomUnitPriceUsd = request.CustomUnitPriceUsd,
            CustomUnitPriceLocal = request.CustomUnitPriceLocal
        };

        var payload = AuthorizationContextCanonicalizer.BuildManualPriceOverridePayload(operation, request.ProductName);

        var command = new CreateAuthorizationRequestDto
        {
            RequestedByUserId = requesterUserId,
            RequestedByName = requesterName,
            RequesterRole = requesterRole,
            ActionType = AuthorizationActionType.ManualPriceOverride,
            SaleId = request.SaleId,
            ContextJson = payload.ContextJson,
            ContextHash = payload.ContextHash,
            Terminal = request.Terminal
        };

        var result = await _authorizationService.CreateAsync(command, cancellationToken);

        if (result.Outcome == CreateAuthorizationOutcome.Created && result.Request is not null)
        {
            await _notifier.NotifyRequestCreatedAsync(result.Request, cancellationToken);
        }

        return result;
    }

    public async Task<ResolveAuthorizationResult> ResolveAsync(
        int requestId,
        int resolverUserId,
        string resolverName,
        bool approved,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolverName);

        var result = await _authorizationService.ResolveAsync(requestId, resolverUserId, resolverName, approved, reason, cancellationToken);

        if (result.Outcome == ResolveAuthorizationOutcome.Resolved && result.Request is not null)
        {
            var token = approved ? IssueToken(result.Request) : null;
            await _notifier.NotifyResolvedAsync(result.Request, token, cancellationToken);
        }

        return result;
    }

    public async Task<LocalAuthorizationResult> ResolveLocalAsync(
        int requestId,
        string username,
        string password,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var result = await _authorizationService.ResolveLocalAsync(requestId, username, password, reason, cancellationToken);

        var token = result.Outcome == LocalResolveOutcome.Resolved && result.Request is not null
            ? IssueToken(result.Request)
            : null;

        if (result.Outcome == LocalResolveOutcome.Resolved && result.Request is not null && token is not null)
        {
            // D4 (gap T3): la resolucion local tambien cierra los modales elevados y entrega el
            // token a la terminal solicitante; el notifier hace el split usuario/elevados.
            await _notifier.NotifyResolvedAsync(result.Request, token, cancellationToken);
        }

        return new LocalAuthorizationResult(
            result.Outcome,
            result.Request,
            token,
            result.SupervisorUserId,
            result.SupervisorName,
            result.Message);
    }

    public async Task<AuthorizationConsumeResult> ConsumeAsync(
        string rawToken,
        int saleId,
        int? actingUserId,
        ManualPriceOverrideContext operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var claims = _tokenService.Validate(rawToken);
        if (claims is null)
        {
            return new AuthorizationConsumeResult(AuthorizationConsumeStatus.InvalidToken, "El token de autorización no es válido o ya expiró.");
        }

        if (actingUserId is null)
        {
            return new AuthorizationConsumeResult(AuthorizationConsumeStatus.WrongUser);
        }

        var contextHash = AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(operation);

        // El binding se valida contra la venta y el usuario REALES de la peticion: derivarlos de
        // las claims del token los volvia circulares (el token se emitio desde la misma fila).
        var result = await _authorizationService.TryConsumeAsync(
            claims.RequestId,
            actingUserId.Value,
            AuthorizationActionType.ManualPriceOverride,
            saleId,
            contextHash,
            cancellationToken);

        return new AuthorizationConsumeResult(MapConsumeOutcome(result.Outcome), result.Message);
    }

    public async Task<int> ExpireStaleAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var candidateIds = await _db.AuthorizationRequests
            .AsNoTracking()
            .Where(candidate => candidate.Status == AuthorizationStatus.Pending && candidate.ExpiresAt < now)
            .OrderBy(candidate => candidate.Id)
            .Select(candidate => candidate.Id)
            .ToListAsync(cancellationToken);

        var affected = await _authorizationService.ExpireStaleAsync(cancellationToken);

        foreach (var requestId in candidateIds)
        {
            // Confirmacion post-barrido: si un admin gano la carrera entre el snapshot y el
            // UPDATE en lote, la solicitud quedo Approved/Rejected y no se notifica expiracion.
            var request = await _authorizationService.GetAsync(requestId, viewerUserId: 0, viewerIsElevated: true, cancellationToken);
            if (request?.Status == AuthorizationStatus.Expired)
            {
                await _notifier.NotifyExpiredAsync(request, cancellationToken);
            }
        }

        return affected;
    }

    public Task<AuthorizationRequestDto?> GetAsync(
        int requestId,
        int viewerUserId,
        bool viewerIsElevated,
        CancellationToken cancellationToken = default)
        => _authorizationService.GetAsync(requestId, viewerUserId, viewerIsElevated, cancellationToken);

    public async Task<AuthorizationStatusResult?> GetStatusAsync(
        int requestId,
        int viewerUserId,
        bool viewerIsElevated,
        CancellationToken cancellationToken = default)
    {
        var request = await _authorizationService.GetAsync(requestId, viewerUserId, viewerIsElevated, cancellationToken);
        if (request is null)
        {
            return null;
        }

        return new AuthorizationStatusResult(request, TryRecoverToken(request, viewerUserId, viewerIsElevated));
    }

    private string? TryRecoverToken(AuthorizationRequestDto request, int viewerUserId, bool viewerIsElevated)
    {
        // La entrega del token esta restringida al solicitante (design D3): los elevados ven el
        // estado pero jamas el token. La reemision reutiliza ResolvedAt como ancla, de modo que la
        // ventana siempre expira en resolvedAt + TTL y no puede extenderse.
        if (viewerIsElevated || request.RequestedByUserId != viewerUserId)
        {
            return null;
        }

        if (request.Status != AuthorizationStatus.Approved || request.ConsumedAt.HasValue || !request.ResolvedAt.HasValue)
        {
            return null;
        }

        var resolvedAtUtc = request.ResolvedAt.Value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(request.ResolvedAt.Value, DateTimeKind.Utc)
            : request.ResolvedAt.Value.ToUniversalTime();

        if (DateTime.UtcNow > resolvedAtUtc.Add(_tokenTtl))
        {
            return null;
        }

        return IssueToken(request);
    }

    private string IssueToken(AuthorizationRequestDto request)
    {
        if (!request.ResolvedAt.HasValue)
        {
            throw new InvalidOperationException("La solicitud resuelta no tiene ResolvedAt; no se puede emitir el token efímero.");
        }

        return _tokenService.Issue(
            request.RequestedByUserId,
            request.Id,
            request.ActionType,
            request.SaleId,
            request.ContextHash,
            request.ResolvedAt.Value);
    }

    private static AuthorizationConsumeStatus MapConsumeOutcome(ConsumeAuthorizationOutcome outcome) => outcome switch
    {
        ConsumeAuthorizationOutcome.Consumed => AuthorizationConsumeStatus.Consumed,
        ConsumeAuthorizationOutcome.NotFound => AuthorizationConsumeStatus.NotFound,
        ConsumeAuthorizationOutcome.NotApproved => AuthorizationConsumeStatus.NotApproved,
        ConsumeAuthorizationOutcome.AlreadyConsumed => AuthorizationConsumeStatus.AlreadyConsumed,
        ConsumeAuthorizationOutcome.WrongUser => AuthorizationConsumeStatus.WrongUser,
        ConsumeAuthorizationOutcome.WrongAction => AuthorizationConsumeStatus.WrongAction,
        ConsumeAuthorizationOutcome.WrongSale => AuthorizationConsumeStatus.WrongSale,
        ConsumeAuthorizationOutcome.ContextMismatch => AuthorizationConsumeStatus.ContextMismatch,
        ConsumeAuthorizationOutcome.TokenExpired => AuthorizationConsumeStatus.TokenExpired,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Resultado de consumo no soportado.")
    };
}
