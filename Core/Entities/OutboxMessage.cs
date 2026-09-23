using System;

namespace Core.Entities;

public class OutboxMessage
{
    // SEAM-04: Status sigue siendo string porque migrarlo a un entero exige una migracion de datos
    // sobre las filas existentes; la constante unica es la referencia compartida por entidad y job.
    public const string PendingStatus = "Pending";

    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
    public DateTime? DispatchedAtUtc { get; set; }
    public string Status { get; set; } = PendingStatus;
    public int RetryCount { get; set; } = 0;
    public DateTime NextRetryUtc { get; set; } = DateTime.UtcNow;
    public string? ErrorMessage { get; set; }
}
