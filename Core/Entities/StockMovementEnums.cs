namespace Core.Entities;

// 8.177-T1 (S1): clasificación estandarizada del Kardex auditable. El texto libre `Reason` se
// conserva por compatibilidad, pero el Centro de Auditoría filtra y agrupa por estos dos ejes:
// Tipo de Origen (Manual / Importación / Sistema) y Motivo Estandarizado (merma, vencimiento,
// entrada por compra, corrección de conteo, venta, devolución, carga inicial, ajuste de import).

/// <summary>
/// 8.177-T1: quién/qué disparó el movimiento de inventario.
/// </summary>
public enum StockMovementOriginType
{
    /// <summary>Operación iniciada por un usuario (ajuste manual desde catálogo).</summary>
    Manual,

    /// <summary>Entrada por documento externo: factura de proveedor o importación masiva.</summary>
    Import,

    /// <summary>Operación automática del sistema (venta, carga inicial de catálogo).</summary>
    System
}

/// <summary>
/// 8.177-T1: motivo estandarizado del movimiento, independiente del formato del texto libre.
/// </summary>
public enum StockMovementReason
{
    /// <summary>Salida por venta (incluida la deducción de una venta entregada a crédito).</summary>
    Sale,

    /// <summary>Entrada por factura de proveedor confirmada.</summary>
    PurchaseEntry,

    /// <summary>Salida por merma o daño del producto.</summary>
    Damage,

    /// <summary>Salida por vencimiento.</summary>
    Expiry,

    /// <summary>Corrección derivada de un conteo físico.</summary>
    PhysicalCountCorrection,

    /// <summary>Reingreso por devolución de cliente.</summary>
    Return,

    /// <summary>Carga inicial al crear un producto con stock.</summary>
    InitialLoad,

    /// <summary>Ajuste de stock producido por una importación masiva desde archivo.</summary>
    ImportAdjustment
}
