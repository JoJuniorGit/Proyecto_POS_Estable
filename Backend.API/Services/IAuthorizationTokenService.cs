using Core.Entities;

namespace Backend.API.Services;

/// <summary>
/// 8.150 (T3, design D3): contrato del token efimero de autorizacion remota. Emision ligada a
/// la resolucion (ResolvedAt) y validacion estricta de firma, emisor, audiencia y vigencia.
/// </summary>
public interface IAuthorizationTokenService
{
    TimeSpan TokenTtl { get; }

    string Issue(
        int cashierUserId,
        int requestId,
        AuthorizationActionType actionType,
        int? saleId,
        string contextHash,
        DateTime resolvedAt);

    AuthorizationTokenClaims? Validate(string? rawToken);
}

/// <summary>
/// 8.150 (T3, design D3): nombres de los claims custom del token de autorizacion.
/// </summary>
public static class AuthorizationTokenClaimNames
{
    public const string ActionType = "act";
    public const string SaleId = "sal";
    public const string ContextHash = "ctx";
}

public sealed record AuthorizationTokenClaims(
    int CashierUserId,
    int RequestId,
    AuthorizationActionType ActionType,
    int? SaleId,
    string ContextHash,
    DateTime ExpiresAt);
