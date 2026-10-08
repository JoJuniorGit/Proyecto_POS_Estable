using Backend.API.Hubs;
using Core.Entities;
using Sales.Module.DTOs;

namespace Backend.API.Services;

/// <summary>
/// 8.150 (T3, design D3/D4): orquestacion del flujo de autorizaciones remotas. Hub y
/// controladores (T4) quedan finos: creacion con contexto canonico, emision del token efimero,
/// notificaciones y consumo single-use viven aqui.
/// </summary>
public interface IAuthorizationCoordinator
{
    TimeSpan TokenTtl { get; }

    Task<CreateAuthorizationResult> CreateAsync(
        RequestAuthorizationContract request,
        int requesterUserId,
        string requesterName,
        UserRole requesterRole,
        CancellationToken cancellationToken = default);

    Task<ResolveAuthorizationResult> ResolveAsync(
        int requestId,
        int resolverUserId,
        string resolverName,
        bool approved,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<LocalAuthorizationResult> ResolveLocalAsync(
        int requestId,
        string username,
        string password,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<AuthorizationConsumeResult> ConsumeAsync(
        string rawToken,
        ManualPriceOverrideContext operation,
        CancellationToken cancellationToken = default);

    Task<int> ExpireStaleAsync(CancellationToken cancellationToken = default);

    Task<AuthorizationRequestDto?> GetAsync(
        int requestId,
        int viewerUserId,
        bool viewerIsElevated,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 8.150 (T3): resultado de consumo expuesto a T5; agrega InvalidToken a los desenlaces del
/// servicio para que la capa protegida no conozca el contrato del token.
/// </summary>
public enum AuthorizationConsumeStatus
{
    Consumed,
    InvalidToken,
    NotFound,
    NotApproved,
    AlreadyConsumed,
    WrongUser,
    WrongAction,
    WrongSale,
    ContextMismatch,
    TokenExpired
}

public sealed record AuthorizationConsumeResult(AuthorizationConsumeStatus Status, string? Message = null);

public sealed record LocalAuthorizationResult(
    LocalResolveOutcome Outcome,
    AuthorizationRequestDto? Request = null,
    string? Token = null,
    int? SupervisorUserId = null,
    string? SupervisorName = null,
    string? Message = null);
