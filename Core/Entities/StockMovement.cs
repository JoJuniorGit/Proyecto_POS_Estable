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

    // 8.177-T1 (S1): datos de auditoría del Kardex. Nullable a propósito: los movimientos
    // históricos/legados quedan sin clasificar y la derivación heurística vive en la capa de
    // consulta (T2). Se completan en los productores de movimientos.
    public StockMovementOriginType? OriginType { get; set; }
    public StockMovementReason? ReasonCode { get; set; }
    public string? AuthorizedByUserId { get; set; }
    public decimal? UnitCostUSD { get; set; }
    public int? SupplierInvoiceId { get; set; }
    public string? Observation { get; set; }

    // 8.180-T1 (S1): vínculo con la sesión de conteo físico que aplicó el ajuste (trazabilidad del
    // Paso 6). Nullable: los movimientos de otros orígenes y los históricos no tienen sesión.
    public int? StockCountSessionId { get; set; }
}
