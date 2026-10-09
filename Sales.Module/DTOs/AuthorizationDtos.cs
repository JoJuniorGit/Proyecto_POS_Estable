using Core.Entities;

namespace Sales.Module.DTOs;

// 8.150 (T2, design D2): contrato de la maquina de estados de autorizaciones remotas.
// Los mensajes exactos viven aqui para que hub, REST y auditoria compartan la misma fuente.
public static class AuthorizationMessages
{
    public const string ElevationNotRequired = "Los usuarios con rol de Administrador o Supervisor no requieren autorización.";
    public const string RequestExpired = "La solicitud expiró; debe generarse una nueva.";
    public const string InvalidCredentials = "Credenciales inválidas o sin privilegios para autorizar.";
    public const string DriverBlocked = "El rol de Conductor no tiene acceso a ventas ni a solicitudes de autorización.";
    public const string SaleAccessDenied = "Acceso denegado: no tiene permisos para operar sobre esta venta.";
    public const string UnknownAction = "La acción de autorización no está registrada.";
    public const string ContextTooLarge = "El contexto de la operación excede el límite de 4 KB.";
    public const string RequestNotFound = "La solicitud de autorización no existe.";

    // 8.151 (W1, design D4): la cancelacion es un privilegio exclusivo del solicitante.
    public const string CancelRequesterOnly = "Solo el solicitante puede cancelar la solicitud.";

    public static string AlreadyResolvedBy(string resolverName) =>
        $"Esta solicitud ya fue resuelta por {resolverName}.";
}

public sealed record CreateAuthorizationRequestDto
{
    public int RequestedByUserId { get; init; }
    public string RequestedByName { get; init; } = string.Empty;
    public UserRole RequesterRole { get; init; }
    public AuthorizationActionType ActionType { get; init; }
    public int? SaleId { get; init; }
    public string ContextJson { get; init; } = string.Empty;
    public string ContextHash { get; init; } = string.Empty;
    public string? Terminal { get; init; }
}

public enum CreateAuthorizationOutcome
{
    Created,
    Deduplicated,
    ElevationNotRequired,
    DriverBlocked,
    SaleAccessDenied,
    UnknownAction,
    ContextTooLarge
}

public sealed record AuthorizationRequestDto
{
    public int Id { get; init; }
    public AuthorizationActionType ActionType { get; init; }
    public int? SaleId { get; init; }
    public int RequestedByUserId { get; init; }
    public string RequestedByName { get; init; } = string.Empty;
    public string? Terminal { get; init; }
    public AuthorizationStatus Status { get; init; }
    public int RemainingLifetimeSeconds { get; init; }
    public AuthorizationResolutionMode? ResolutionMode { get; init; }
    public int? ResolvedByUserId { get; init; }
    public string? ResolvedByName { get; init; }
    public string? ResolutionReason { get; init; }
    public string ContextJson { get; init; } = string.Empty;
    public string ContextHash { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public DateTime? ConsumedAt { get; init; }
}

public sealed record CreateAuthorizationResult(
    CreateAuthorizationOutcome Outcome,
    AuthorizationRequestDto? Request = null,
    string? Message = null);

public enum ResolveAuthorizationOutcome
{
    Resolved,
    AlreadyResolved,
    Expired,
    NotFound
}

public sealed record ResolveAuthorizationResult(
    ResolveAuthorizationOutcome Outcome,
    AuthorizationRequestDto? Request = null,
    string? Message = null);

public enum LocalResolveOutcome
{
    Resolved,
    InvalidCredentials,
    LockedOut,
    AlreadyResolved,
    Expired,
    NotFound
}

public sealed record LocalResolveResult(
    LocalResolveOutcome Outcome,
    AuthorizationRequestDto? Request = null,
    int? SupervisorUserId = null,
    string? SupervisorName = null,
    string? Message = null);

public enum ConsumeAuthorizationOutcome
{
    Consumed,
    NotFound,
    NotApproved,
    AlreadyConsumed,
    WrongUser,
    WrongAction,
    WrongSale,
    ContextMismatch,
    TokenExpired
}

public sealed record ConsumeAuthorizationResult(ConsumeAuthorizationOutcome Outcome, string? Message = null);

// 8.151 (W1, design D4): desenlaces de la cancelacion del solicitante. La transicion
// Pending -> Cancelled es atomica y solo el dueno de la solicitud puede reclamarla.
public enum CancelAuthorizationOutcome
{
    Cancelled,
    AlreadyResolved,
    Expired,
    NotFound,
    Forbidden
}

public sealed record CancelAuthorizationResult(
    CancelAuthorizationOutcome Outcome,
    AuthorizationRequestDto? Request = null,
    string? Message = null);
