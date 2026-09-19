using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Sales.Module.Entities;

public class SaleItem
{
    public int Id { get; set; }

    public int SaleId { get; set; }
    public Sale Sale { get; set; } = null!;

    [Column(TypeName = "decimal(18,3)")]
    public decimal Quantity { get; set; }

    // Financial Snapshot
    [Column(TypeName = "decimal(18,4)")]
    public decimal UnitPrice { get; set; }
    [Column(TypeName = "decimal(18,4)")]
    public decimal Subtotal { get; set; } // Quantity * UnitPrice

    [Column(TypeName = "decimal(18,4)")]
    public decimal UnitPriceBsS { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal SubtotalBsS { get; set; }

    // Snapshot del costo unitario del catalogo al momento de la venta (COGS historico, Paso 14
    // de docs/Ideas.txt). Nullable a proposito: NULL = costo desconocido (lineas anteriores a
    // 8.142), nunca 0, para no fabricar margen del 100% sobre el historico.
    [Column(TypeName = "decimal(18,2)")]
    public decimal? UnitCostUSD { get; set; }

    public int ProductId { get; set; }

    [Required]
    public string ProductName { get; set; } = string.Empty;

    [NotMapped]
    public bool IsWholesaleApplied { get; set; }

    public bool IsCustomPrice { get; set; } = false;

    [NotMapped]
    public bool IsFractional { get; set; }

    [NotMapped]
    public Core.Entities.UnitOfMeasureType UnitOfMeasure { get; set; } = Core.Entities.UnitOfMeasureType.Und;
}
