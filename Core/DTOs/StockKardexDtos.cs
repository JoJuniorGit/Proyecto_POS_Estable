using System;
using System.Collections.Generic;
using Core.Entities;

namespace Core.DTOs;

/// <summary>
/// 8.177-T2 (S2): sentido del movimiento para el filtro del Kardex.
/// </summary>
public enum StockKardexDirection
{
    All = 0,
    In = 1,
    Out = 2
}

/// <summary>
/// 8.177-T2 (S2): filtros combinables del Centro de Auditoría de Inventario. Los instantes de
/// fecha llegan en UTC (el cliente resuelve el rango local→UTC); el servicio no reinterpreta
/// zonas horarias sobre ellos.
/// </summary>
public class StockKardexFilterDto
{
    public DateTime? DateFromUtc { get; set; }
    public DateTime? DateToUtc { get; set; }

    /// <summary>Usuario a filtrar: matchea el ejecutor O el autorizador del movimiento.</summary>
    public string? UserId { get; set; }

    public int? ProductId { get; set; }

    /// <summary>Motivos estandarizados a incluir. El filtro se aplica sobre el valor EFECTIVO
    /// (persistido o derivado históricamente desde el texto libre).</summary>
    public IReadOnlyList<StockMovementReason>? ReasonCodes { get; set; }

    /// <summary>Tipos de origen a incluir. Igual que <see cref="ReasonCodes"/>: valor efectivo.</summary>
    public IReadOnlyList<StockMovementOriginType>? OriginTypes { get; set; }

    public StockKardexDirection Direction { get; set; } = StockKardexDirection.All;
}

/// <summary>
/// 8.177-T2 (S1/S2): fila unificada del Kardex. <see cref="Source"/> distingue la tabla de
/// origen: "Active" (StockMovements) o "Archived" (StockMovements_Archive, decisión L2).
/// </summary>
public class StockKardexRowDto
{
    /// <summary>"Active" o "Archived": tabla de origen de la fila.</summary>
    public string Source { get; set; } = "Active";

    /// <summary>Id lógico del movimiento para la UI: Id de la tabla activa o OriginalMovementId en el archivo.</summary>
    public int MovementId { get; set; }

    /// <summary>Id del registro en StockMovements_Archive (solo filas archivadas).</summary>
    public int? ArchivedId { get; set; }

    /// <summary>Id original del movimiento antes de archivarse (solo filas archivadas).</summary>
    public int? OriginalMovementId { get; set; }

    public int ProductId { get; set; }
    public string SKU { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public DateTime MovementDateUtc { get; set; }
    public decimal QuantityChange { get; set; }

    /// <summary>Stock previo al movimiento (NewStockLevel − QuantityChange).</summary>
    public decimal StockBefore { get; set; }

    public decimal NewStockLevel { get; set; }

    /// <summary>Costo unitario histórico snapshot; null en movimientos legados sin costo.</summary>
    public decimal? UnitCostUSD { get; set; }

    /// <summary>Δ × costo unitario, redondeado fiscalmente a 2 decimales (AwayFromZero). Null sin costo.</summary>
    public decimal? TotalValueUSD { get; set; }

    /// <summary>Tipo de origen EFECTIVO (persistido o derivado históricamente).</summary>
    public StockMovementOriginType? OriginType { get; set; }

    /// <summary>Motivo EFECTIVO (persistido o derivado históricamente desde <see cref="Reason"/>).</summary>
    public StockMovementReason? ReasonCode { get; set; }

    /// <summary>Texto libre original del movimiento (se conserva para auditoría).</summary>
    public string Reason { get; set; } = string.Empty;

    public string? ExecutedByUserId { get; set; }
    public string? AuthorizedByUserId { get; set; }
    public int? SaleId { get; set; }
    public int? SupplierInvoiceId { get; set; }
    public string? Observation { get; set; }
}

/// <summary>
/// 8.177-T2 (S2): KPIs del Kardex bajo los filtros activos. Las filas sin costo snapshot
/// (<see cref="UnvaluedMovements"/>) quedan excluidas de las sumas de valor.
/// </summary>
public class StockKardexSummaryDto
{
    public long TotalMovements { get; set; }
    public decimal NetVolume { get; set; }
    public decimal EntriesValueUsd { get; set; }

    /// <summary>Magnitud positiva del valor de las salidas valorizadas.</summary>
    public decimal LossesValueUsd { get; set; }

    public decimal NetValueUsd { get; set; }

    /// <summary>Filas sin costo unitario snapshot (excluidas de las sumas de valor).</summary>
    public long UnvaluedMovements { get; set; }
}
