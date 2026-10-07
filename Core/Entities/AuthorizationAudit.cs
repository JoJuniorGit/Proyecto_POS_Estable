using System;

namespace Core.Entities;

public class AuthorizationAudit
{
    public int Id { get; set; }

    public int RequestId { get; set; }

    public AuthorizationActionType ActionType { get; set; }

    public int? SaleId { get; set; }

    public string? Terminal { get; set; }

    public int RequestedByUserId { get; set; }

    public string RequestedByName { get; set; } = string.Empty;

    public AuthorizationStatus Status { get; set; }

    public AuthorizationResolutionMode? ResolutionMode { get; set; }

    public int? ResolvedByUserId { get; set; }

    public string? ResolvedByName { get; set; }

    public string? Reason { get; set; }

    public string? ContextJson { get; set; }

    public DateTime RequestedAt { get; set; }

    public DateTime ResolvedAt { get; set; }
}
