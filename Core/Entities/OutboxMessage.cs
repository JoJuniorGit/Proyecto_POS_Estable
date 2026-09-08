using System;

namespace Core.Entities;

public enum OutboxStatus
{
    Pending,
    Dispatching,
    Processed,
    DeadLetter
}

public class OutboxMessage
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
    public DateTime? DispatchedAtUtc { get; set; }
    public string Status { get; set; } = "Pending";
    public int RetryCount { get; set; } = 0;
    public DateTime NextRetryUtc { get; set; } = DateTime.UtcNow;
    public string? ErrorMessage { get; set; }
}
