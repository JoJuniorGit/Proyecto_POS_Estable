using System;

namespace Core.Entities;

public class StockMovementArchive : BaseEntity
{
    public int OriginalMovementId { get; set; }
    public int ProductId { get; set; }
    public decimal QuantityChange { get; set; }
    public decimal NewStockLevel { get; set; }
    public string Reason { get; set; } = string.Empty;
    // 8.16-H03: se conserva la clave de idempotencia estructurada en el archivo.
    public int? SaleId { get; set; }
    public DateTime MovementDate { get; set; }
    public string? UserId { get; set; }
    public DateTime ArchivedAtUtc { get; set; } = DateTime.UtcNow;
}
