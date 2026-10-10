using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

/// <summary>
/// 8.150 (T9, design D7): contrato del cliente WPF del hub /hubs/authorization. Los eventos
/// replican los del notificador SignalR del backend; create/resolve usan el hub cuando el socket
/// esta conectado y el par REST del controlador cuando esta caido.
/// </summary>
public interface IAuthorizationHubService : IDisposable
{
    event EventHandler<AuthorizationRequestedPayload>? AuthorizationRequested;
    event EventHandler<AuthorizationResolvedPayload>? AuthorizationResolved;
    event EventHandler<AuthorizationExpiredPayload>? AuthorizationExpired;

    Task<AuthorizationRequestInfo> RequestAuthorizationAsync(AuthorizationRequestContext context);
    Task<AuthorizationResolveInfo> ResolveAuthorizationAsync(int requestId, bool approved, string? reason = null);
    Task<AuthorizationStatusInfo?> GetStatusAsync(int requestId);
    Task<LocalResolveInfo> LocalResolveAsync(int requestId, string username, string password, string? reason = null);
}

/// <summary>8.150 (T9): contexto tipado de la accion protegida v1 (ManualPriceOverride).</summary>
public sealed record AuthorizationRequestContext
{
    public int SaleId { get; init; }
    public int ProductId { get; init; }
    public string? ProductName { get; init; }
    public decimal Quantity { get; init; }
    public decimal? CustomUnitPriceUsd { get; init; }
    public decimal? CustomUnitPriceLocal { get; init; }
    public string? Terminal { get; init; }
}

public sealed record AuthorizationRequestInfo(
    int RequestId,
    DateTimeOffset ExpiresAt,
    bool Deduplicated = false,
    int RemainingLifetimeSeconds = 0);

public sealed record AuthorizationResolveInfo(string? Status, string? ResolvedByName, string? Reason);

public sealed record LocalResolveInfo(
    string? Token,
    int? SupervisorUserId = null,
    string? SupervisorName = null,
    string? Status = null);

/// <summary>8.150 (T9): estado de la solicitud expuesto por GET /api/authorizations/{id}.</summary>
public sealed class AuthorizationStatusInfo
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("actionType")]
    public string? ActionType { get; init; }

    [JsonPropertyName("saleId")]
    public int? SaleId { get; init; }

    [JsonPropertyName("requestedByName")]
    public string? RequestedByName { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    public bool Approved => string.Equals(Status, "Approved", StringComparison.OrdinalIgnoreCase);

    [JsonPropertyName("resolutionMode")]
    public string? ResolutionMode { get; init; }

    [JsonPropertyName("resolvedByName")]
    public string? ResolvedByName { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("contextJson")]
    public string? ContextJson { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }

    [JsonPropertyName("token")]
    public string? Token { get; init; }

    [JsonPropertyName("remainingLifetimeSeconds")]
    public int RemainingLifetimeSeconds { get; init; }
}

/// <summary>8.150 (T9): payload de AuthorizationRequested (solo role:elevated).</summary>
public sealed class AuthorizationRequestedPayload : EventArgs
{
    [JsonPropertyName("requestId")]
    public int RequestId { get; init; }

    [JsonPropertyName("actionType")]
    public string? ActionType { get; init; }

    [JsonPropertyName("saleId")]
    public int? SaleId { get; init; }

    [JsonPropertyName("requestedByName")]
    public string? RequestedByName { get; init; }

    [JsonPropertyName("terminal")]
    public string? Terminal { get; init; }

    [JsonPropertyName("context")]
    public JsonElement? Context { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>8.150 (T9): payload de AuthorizationResolved hacia el solicitante y los elevados.</summary>
public sealed class AuthorizationResolvedPayload : EventArgs
{
    [JsonPropertyName("requestId")]
    public int RequestId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("approved")]
    public bool Approved { get; init; }

    [JsonPropertyName("resolvedByName")]
    public string? ResolvedByName { get; init; }

    [JsonPropertyName("resolutionMode")]
    public string? ResolutionMode { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("token")]
    public string? Token { get; init; }
}

/// <summary>8.150 (T9): payload de AuthorizationExpired hacia ambos grupos.</summary>
public sealed class AuthorizationExpiredPayload : EventArgs
{
    [JsonPropertyName("requestId")]
    public int RequestId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }

    [JsonPropertyName("expiredAt")]
    public DateTimeOffset? ExpiredAt { get; init; }
}

/// <summary>
/// 8.150 (T9): error de contrato del hub o del par REST con el codigo HTTP observado. El mensaje
/// es el ProblemDetails del backend (p. ej. el texto exacto de credenciales invalidas o de carrera).
/// </summary>
public sealed class AuthorizationHubException : Exception
{
    public AuthorizationHubException(string message, int statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}
