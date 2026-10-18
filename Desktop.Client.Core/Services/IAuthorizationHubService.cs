using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Core.Entities;

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

    /// <summary>8.151 (W3, R6/design D4a): el hub recupero el socket; la cola admin debe reconciliarse.</summary>
    event EventHandler? Reconnected;

    Task<AuthorizationRequestInfo> RequestAuthorizationAsync(IAuthorizationRequestContext context);
    Task<AuthorizationResolveInfo> ResolveAuthorizationAsync(int requestId, bool approved, string? reason = null);
    Task<AuthorizationStatusInfo?> GetStatusAsync(int requestId);
    Task<LocalResolveInfo> LocalResolveAsync(int requestId, string username, string password, string? reason = null);

    /// <summary>8.151 (W3, R4-client): retiro del solicitante por REST (el hub no expone cancelacion).</summary>
    Task CancelRequestAsync(int requestId);
}

/// <summary>
/// 8.150 (T9): contrato comun de una accion protegida. El discriminador <see cref="ActionType"/>
/// permite al transporte elegir el payload correcto sin reinterpretar campos de otra accion.
/// </summary>
public interface IAuthorizationRequestContext
{
    int SaleId { get; }

    AuthorizationActionType ActionType { get; }

    string? Terminal { get; }
}

/// <summary>8.150 (T9): contexto tipado de la accion protegida v1 (ManualPriceOverride).</summary>
public sealed record AuthorizationRequestContext : IAuthorizationRequestContext
{
    public int SaleId { get; init; }
    public int ProductId { get; init; }
    public string? ProductName { get; init; }
    public decimal Quantity { get; init; }
    public decimal? CustomUnitPriceUsd { get; init; }
    public decimal? CustomUnitPriceLocal { get; init; }
    public string? Terminal { get; init; }

    // El override manual conserva su contrato historico: sin actionType explicito en el payload.
    public AuthorizationActionType ActionType => AuthorizationActionType.ManualPriceOverride;
}

/// <summary>
/// T4-prep (Paso 11/12, S4): contexto del sobregiro de cupo en venta a credito. El transporte
/// envia actionType = CreditOverLimit (valor numerico del enum) y el contexto exacto que el
/// backend canonicaliza para ligar el token de autorizacion (cliente, venta y montos).
/// </summary>
public sealed record CreditOverLimitAuthorizationRequestContext : IAuthorizationRequestContext
{
    public int SaleId { get; init; }
    public int CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public decimal FinancedUSD { get; init; }
    public decimal AvailableCreditUSD { get; init; }
    public string? Terminal { get; init; }

    public AuthorizationActionType ActionType => AuthorizationActionType.CreditOverLimit;
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

    /// <summary>Accion parseada del push (el backend publica el nombre del enum como string).</summary>
    [JsonIgnore]
    public AuthorizationActionType? ParsedActionType =>
        Enum.TryParse<AuthorizationActionType>(ActionType, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : null;

    /// <summary>
    /// T4-prep (S4): reconstruye el contexto CreditOverLimit del push para que la UI de supervision
    /// muestre cliente y montos. Devuelve null si la accion es otra o el contexto esta incompleto.
    /// </summary>
    public CreditOverLimitAuthorizationRequestContext? GetCreditOverLimitContext()
    {
        if (ParsedActionType != AuthorizationActionType.CreditOverLimit
            || Context is not { ValueKind: JsonValueKind.Object } context)
        {
            return null;
        }

        if (!context.TryGetProperty("customerId", out var customerElement) || !customerElement.TryGetInt32(out var customerId))
        {
            return null;
        }

        if (!context.TryGetProperty("financedUSD", out var financedElement) || !financedElement.TryGetDecimal(out var financedUsd))
        {
            return null;
        }

        if (!context.TryGetProperty("availableCreditUSD", out var availableElement) || !availableElement.TryGetDecimal(out var availableCreditUsd))
        {
            return null;
        }

        int? saleId = SaleId;
        if (!saleId.HasValue
            && context.TryGetProperty("saleId", out var saleElement)
            && saleElement.TryGetInt32(out var contextSaleId))
        {
            saleId = contextSaleId;
        }

        if (!saleId.HasValue)
        {
            return null;
        }

        string? customerName = context.TryGetProperty("customerName", out var nameElement)
            && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;

        return new CreditOverLimitAuthorizationRequestContext
        {
            SaleId = saleId.Value,
            CustomerId = customerId,
            CustomerName = customerName,
            FinancedUSD = financedUsd,
            AvailableCreditUSD = availableCreditUsd,
            Terminal = Terminal
        };
    }
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
