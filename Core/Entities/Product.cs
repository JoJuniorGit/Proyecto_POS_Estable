using System.ComponentModel.DataAnnotations;

namespace Core.Entities;

public enum UnitOfMeasureType
{
    Und = 0,
    Kg = 1,
    Grs = 2,
    Lb = 3,
    Oz = 4,
    Lt = 5,
    Ml = 6
}

public class Product : BaseEntity
{
    [Required(ErrorMessage = "El nombre del producto es obligatorio.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "El nombre no puede exceder 100 caracteres.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(50, ErrorMessage = "El SKU no puede exceder 50 caracteres.")]
    public string SKU { get; set; } = string.Empty; // Barcode

    public string Description { get; set; } = string.Empty;

    [Range(0, 1000000, ErrorMessage = "El precio no puede ser negativo.")]
    public decimal PriceUSD { get; set; }

    [Range(0, 1000000, ErrorMessage = "El precio al detal no puede ser negativo.")]
    public decimal PriceRetailUSD { get; set; }

    [Range(0, 1000000, ErrorMessage = "El precio al mayor no puede ser negativo.")]
    public decimal PriceWholesaleUSD { get; set; }

    [Range(0, 1000000, ErrorMessage = "El costo no puede ser negativo.")]
    public decimal CostPriceUSD { get; set; }

    [Range(0, 1000, ErrorMessage = "El margen al detal debe estar entre 0% y 1000%.")]
    public decimal ProfitMarginRetail { get; set; }

    [Range(0, 1000, ErrorMessage = "El margen al mayor debe estar entre 0% y 1000%.")]
    public decimal ProfitMarginWholesale { get; set; }

    [Range(0, 1000000, ErrorMessage = "La cantidad mínima mayorista no puede ser negativa.")]
    public decimal MinWholesaleQuantity { get; set; } = 6.000m;

    public bool HasWholesale { get; set; } = false;
    public bool IsFractional { get; set; } = false;
    public UnitOfMeasureType UnitOfMeasure { get; set; } = UnitOfMeasureType.Und;
    public string UnitOfMeasureStr => UnitOfMeasure.ToString();

    public decimal PriceBsS { get; set; } // The "tagged" price in local currency
    public decimal LastConversionRate { get; set; } // The rate used to calculate PriceBsS

    [Range(0, 1000000, ErrorMessage = "El costo no puede ser negativo.")]
    public decimal Cost { get; set; }

    [Range(0, 1000000, ErrorMessage = "El stock no puede ser negativo.")]
    public decimal StockQuantity { get; set; }

    [Range(0, 1000, ErrorMessage = "El porcentaje de ganancia debe estar entre 0% y 1000%.")]
    public decimal ProfitPercentage { get; set; }

    [Range(0, 1000000, ErrorMessage = "El umbral de stock bajo no puede ser negativo.")]
    public decimal LowStockThreshold { get; set; }

    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; } = false;
    
    // Identifies the product used for requesting physical cash from the register
    public bool IsCashAdvance { get; set; } = false;

    public decimal ReservedQuantity { get; set; }

    // Parent/Group and Variant Properties
    public int? ParentProductId { get; set; }
    public Product? ParentProduct { get; set; }
    public ICollection<Product> Variants { get; set; } = new List<Product>();

    public bool IsGroupHeader { get; set; } = false;

    // Variant Group Capabilities (Only applicable if IsGroupHeader == true, immutable once created)
    public bool IsStockShared { get; set; } = false;
    public bool HasIndependentPricing { get; set; } = false;

    /// <summary>
    /// Multiplicador de consumo de stock sobre el producto padre cuando IsStockShared = true.
    /// Define cuántas unidades base del padre consume 1 unidad de esta variante.
    /// Para productos no compartidos o independientes siempre se normaliza a 1.0000.
    /// </summary>
    [Range(0.0001, 1000000.0, ErrorMessage = "El factor de conversión debe ser mayor a 0.")]
    public decimal ConversionFactor { get; set; } = 1.0000m;

    [StringLength(50)]
    public string? GroupKey { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
