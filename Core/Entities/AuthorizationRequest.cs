using System;

namespace Core.Entities;

public class AuthorizationRequest
{
    public int Id { get; set; }

    public AuthorizationActionType ActionType { get; set; }

    public int? SaleId { get; set; }

    public int RequestedByUserId { get; set; }

    public string RequestedByName { get; set; } = string.Empty;

    public string? Terminal { get; set; }

    public AuthorizationStatus Status { get; set; } = AuthorizationStatus.Pending;

    public AuthorizationResolutionMode? ResolutionMode { get; set; }

    public int? ResolvedByUserId { get; set; }

    public string? ResolvedByName { get; set; }

    public string? ResolutionReason { get; set; }

    public string ContextJson { get; set; } = string.Empty;

    public string ContextHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public DateTime? ConsumedAt { get; set; }
}
