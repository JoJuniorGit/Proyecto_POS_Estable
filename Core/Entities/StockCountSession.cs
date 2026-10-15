using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Core.Entities;

/// <summary>
/// 8.180-T1 (S1): sesión de conteo físico ciego. Agrupa los ítems contados (conocimiento ciego:
/// el operador nunca ve la existencia teórica), opcionalmente acotada al subárbol de una
/// categoría (<see cref="ScopeCategoryId"/>; null = todo el inventario). El teórico y el costo
/// solo se congelan al cerrar (<see cref="ClosedAt"/>) y los ajustes se aplican una única vez
/// (<see cref="AppliedAt"/>/<see cref="AppliedByUserId"/>). Lleva token de concurrencia xmin
/// (configurado en InventoryDbContext) para que dos applies concurrentes no se pisen.
/// </summary>
public class StockCountSession : BaseEntity
{
    [StringLength(80)]
    public string Name { get; set; } = string.Empty;

    public StockCountStatus Status { get; set; } = StockCountStatus.Open;

    /// <summary>Categoría raíz del alcance del conteo (subárbol completo); null = todos los productos.</summary>
    public int? ScopeCategoryId { get; set; }
    public Category? ScopeCategory { get; set; }

    public string? CreatedByUserId { get; set; }

    [StringLength(300)]
    public string? Notes { get; set; }

    public DateTime? ClosedAt { get; set; }
    public DateTime? AppliedAt { get; set; }
    public string? AppliedByUserId { get; set; }

    public ICollection<StockCountItem> Items { get; set; } = new List<StockCountItem>();
}
