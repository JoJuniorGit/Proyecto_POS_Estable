using System;

namespace Core.Entities;

/// <summary>
/// 8.180-T1 (S1): ítem contado de una sesión de conteo físico. <see cref="CountedQuantity"/> es lo
/// único que crece durante el conteo ciego; el teórico y el costo unitario quedan NULL hasta el
/// cierre (<see cref="StockCountSession.ClosedAt"/>), cuando se congelan para que el descuadre y
/// los ajustes sean reproducibles aunque el stock o el costo cambien después.
/// </summary>
public class StockCountItem : BaseEntity
{
    public int StockCountSessionId { get; set; }
    public StockCountSession Session { get; set; } = null!;

    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public decimal CountedQuantity { get; set; }

    /// <summary>Existencia teórica congelada al cerrar la sesión; null mientras está Open.</summary>
    public decimal? TheoreticalStock { get; set; }

    /// <summary>Costo unitario USD congelado al cerrar la sesión; null mientras está Open.</summary>
    public decimal? UnitCostUSD { get; set; }
}
