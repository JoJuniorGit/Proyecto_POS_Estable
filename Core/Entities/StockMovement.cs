using System;

namespace Core.Entities;

public class StockMovement : BaseEntity
{
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public decimal QuantityChange { get; set; }
    public decimal NewStockLevel { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime MovementDate { get; set; } = DateTime.UtcNow;

    // 8.16-H03: clave estructurada de idempotencia de la deducción (SaleId), independiente del
    // formato del string Reason. Nullable: movimientos no derivados de una venta o históricos.
    public int? SaleId { get; set; }

    // Optional: UserId if we want to track who made the change
    public string? UserId { get; set; }
}
