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

    // 8.177-T1: espejo de los datos de auditoría del movimiento original. La decisión L2 del
    // mantenedor exige que el archiver preserve TODOS los campos de auditoría porque el Centro
    // consulta la unión activa + archivo.
    public StockMovementOriginType? OriginType { get; set; }
    public StockMovementReason? ReasonCode { get; set; }
    public string? AuthorizedByUserId { get; set; }
    public decimal? UnitCostUSD { get; set; }
    public int? SupplierInvoiceId { get; set; }
    public string? Observation { get; set; }

    /// <summary>
    /// 8.177-T1: mapeo único movimiento → archivo (usado por StockMovementArchiverJob). Copia
    /// todos los campos de auditoría y estampa la marca de archivado.
    /// </summary>
    public static StockMovementArchive FromStockMovement(StockMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);

        return new StockMovementArchive
        {
            OriginalMovementId = movement.Id,
            ProductId = movement.ProductId,
            QuantityChange = movement.QuantityChange,
            NewStockLevel = movement.NewStockLevel,
            Reason = movement.Reason,
            SaleId = movement.SaleId,
            MovementDate = movement.MovementDate,
            UserId = movement.UserId,
            OriginType = movement.OriginType,
            ReasonCode = movement.ReasonCode,
            AuthorizedByUserId = movement.AuthorizedByUserId,
            UnitCostUSD = movement.UnitCostUSD,
            SupplierInvoiceId = movement.SupplierInvoiceId,
            Observation = movement.Observation,
            ArchivedAtUtc = DateTime.UtcNow
        };
    }
}
