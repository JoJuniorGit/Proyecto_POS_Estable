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
        CancellationToken cancellationToken = default,
        int? callerUserId = null,
        bool callerIsElevated = false);

    Task<AuthorizationConsumeResult> ConsumeAsync(
        string rawToken,
        int saleId,
        int? actingUserId,
        ManualPriceOverrideContext operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// T3b1 (F2b, S4): consumo single-use del token de la excepción de sobregiro (accion
    /// CreditOverLimit). Espeja <see cref="ConsumeAsync"/> pero liga el token al contexto de
    /// crédito (cliente, venta, monto financiado, crédito disponible) en lugar del producto.
    /// </summary>
    Task<AuthorizationConsumeResult> ConsumeCreditOverLimitAsync(
        string rawToken,
        int saleId,
        int? actingUserId,
        CreditOverLimitContext operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// T2a (Paso 13, S4): consumo single-use del token del egreso de caja chica (accion
    /// PettyCashExpense). Liga el token al contexto exacto (sesión, moneda, montos bimonetarios,
    /// categoría) sin venta asociada; en éxito expone la solicitud y el autorizador.
    /// </summary>
    Task<AuthorizationConsumeResult> ConsumePettyCashExpenseAsync(
        string rawToken,
        int? actingUserId,
        PettyCashExpenseAuthorizationContext operation,
        CancellationToken cancellationToken = default);

    Task<int> ExpireStaleAsync(CancellationToken cancellationToken = default);

    Task<AuthorizationRequestDto?> GetAsync(
        int requestId,
        int viewerUserId,
        bool viewerIsElevated,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// D3/D4: estado visible para el GET REST. Cuando el viewer es el solicitante, la solicitud
    /// esta Approved, sin consumir y dentro de la ventana del token, reemite el token anclado a
    /// <c>ResolvedAt</c> (misma ventana, nunca extendida). Para elevados u otros viewers el token
    /// viaja null.
    /// </summary>
    Task<AuthorizationStatusResult?> GetStatusAsync(
        int requestId,
        int viewerUserId,
        bool viewerIsElevated,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 8.151 (W1, design D4): cancelacion del solicitante; en exito cierra los modales con
    /// AuthorizationResolved (status Cancelled, sin token) para solicitante y elevados.
    /// </summary>
    Task<CancelAuthorizationResult> CancelAsync(
        int requestId,
        int requesterUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 8.150 (T3): resultado de consumo expuesto a T5; agrega InvalidToken a los desenlaces del
/// servicio para que la capa protegida no conozca el contrato del token. T2a (Paso 13): en el
/// consumo del gasto de caja chica viajan además la solicitud consumida y el supervisor
/// autorizador, para completar <c>AuthorizedByUserId</c>/<c>AuthorizationRequestId</c>.
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

public sealed record AuthorizationConsumeResult(
    AuthorizationConsumeStatus Status,
    string? Message = null,
    int? RequestId = null,
    int? AuthorizedByUserId = null);

/// <summary>
/// 8.150 (T4): estado + token recuperable expuestos por el GET REST. El token solo se completa
/// para el solicitante dentro de la ventana de recuperacion.
/// </summary>
public sealed record AuthorizationStatusResult(AuthorizationRequestDto Request, string? Token = null);

public sealed record LocalAuthorizationResult(
    LocalResolveOutcome Outcome,
    AuthorizationRequestDto? Request = null,
    string? Token = null,
    int? SupervisorUserId = null,
    string? SupervisorName = null,
    string? Message = null);
