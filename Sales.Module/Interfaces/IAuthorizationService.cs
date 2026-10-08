using Core.Entities;
using Sales.Module.DTOs;

namespace Sales.Module.Interfaces;

/// <summary>
/// 8.150 (T2, design D2): maquina de estados de autorizaciones remotas. Create/dedupe,
/// resolucion race-safe (remota y local), expiracion y consumo single-use del token efimero.
/// La elevacion y la emision del token quedan fuera: el llamador resuelve el rol del viewer
/// y T3 emite el token; esta capa solo valida y consume.
/// </summary>
public interface IAuthorizationService
{
    Task<CreateAuthorizationResult> CreateAsync(
        CreateAuthorizationRequestDto request,
        CancellationToken cancellationToken = default);

    Task<ResolveAuthorizationResult> ResolveAsync(
        int requestId,
        int resolverUserId,
        string resolverName,
        bool approved,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<LocalResolveResult> ResolveLocalAsync(
        int requestId,
        string username,
        string password,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<int> ExpireStaleAsync(CancellationToken cancellationToken = default);

    Task<ConsumeAuthorizationResult> TryConsumeAsync(
        int requestId,
        int userId,
        AuthorizationActionType actionType,
        int? saleId,
        string contextHash,
        CancellationToken cancellationToken = default);

    Task<AuthorizationRequestDto?> GetAsync(
        int requestId,
        int viewerUserId,
        bool viewerIsElevated,
        CancellationToken cancellationToken = default);
}
