using System.ComponentModel.DataAnnotations;
using Core.Entities;

namespace Backend.API.DTOs;

/// <summary>
/// DTO de entrada para la creación de productos.
/// Protege contra Mass Assignment excluyendo RowVersion, StockQuantity y ReservedQuantity ([8B-CR1]).
/// </summary>
public class CreateProductDto
{
    [Required(ErrorMessage = "El nombre del producto es obligatorio.")]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? SKU { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    [Range(0, 1000000)]
    public decimal PriceUSD { get; set; }

    [Range(0, 1000000)]
    public decimal PriceRetailUSD { get; set; }

    [Range(0, 1000000)]
    public decimal PriceWholesaleUSD { get; set; }

    [Range(0, 1000000)]
    public decimal CostPriceUSD { get; set; }

    [Range(0, 10000)]
    public decimal ProfitMarginRetail { get; set; }

    [Range(0, 10000)]
    public decimal ProfitMarginWholesale { get; set; }

    public decimal MinWholesaleQuantity { get; set; } = 6.000m;
    public bool HasWholesale { get; set; }
    public bool IsFractional { get; set; }
    public UnitOfMeasureType UnitOfMeasure { get; set; } = UnitOfMeasureType.Und;
    public decimal LowStockThreshold { get; set; }
    public bool IsCashAdvance { get; set; }
    public bool IsActive { get; set; } = true;

    // Jerarquía de grupos y variantes
    public int? ParentProductId { get; set; }
    public bool IsGroupHeader { get; set; }
    public bool IsStockShared { get; set; }
    public bool HasIndependentPricing { get; set; }
    public decimal ConversionFactor { get; set; } = 1.0000m;
    public string? GroupKey { get; set; }
}

/// <summary>
/// DTO de entrada para la actualización de productos ([8B-CR1]).
/// </summary>
public class UpdateProductDto
{
    public int Id { get; set; }

    [Required(ErrorMessage = "El nombre del producto es obligatorio.")]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? SKU { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    [Range(0, 1000000)]
    public decimal PriceUSD { get; set; }

    [Range(0, 1000000)]
    public decimal PriceRetailUSD { get; set; }

    [Range(0, 1000000)]
    public decimal PriceWholesaleUSD { get; set; }

    [Range(0, 1000000)]
    public decimal CostPriceUSD { get; set; }

    [Range(0, 10000)]
    public decimal ProfitMarginRetail { get; set; }

    [Range(0, 10000)]
    public decimal ProfitMarginWholesale { get; set; }

    public decimal MinWholesaleQuantity { get; set; } = 6.000m;
    public bool HasWholesale { get; set; }
    public bool IsFractional { get; set; }
    public UnitOfMeasureType UnitOfMeasure { get; set; } = UnitOfMeasureType.Und;
    public decimal LowStockThreshold { get; set; }
    public bool IsCashAdvance { get; set; }
    public bool IsActive { get; set; }

    // Jerarquía de grupos y variantes
    public int? ParentProductId { get; set; }
    public bool IsGroupHeader { get; set; }
    public bool IsStockShared { get; set; }
    public bool HasIndependentPricing { get; set; }
    public decimal ConversionFactor { get; set; } = 1.0000m;
    public string? GroupKey { get; set; }
}
