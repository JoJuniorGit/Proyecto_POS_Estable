namespace Core.DTOs;

public class ProductQuickInfoDto
{
    public int Id { get; set; }
    public string SKU { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal PriceUSD { get; set; }
    public decimal PriceRetailUSD { get; set; }
    public decimal PriceWholesaleUSD { get; set; }
    public decimal PriceBsS { get; set; } // The "tagged" price in local currency
    public bool HasWholesale { get; set; }
    public bool IsFractional { get; set; }
    public Core.Entities.UnitOfMeasureType UnitOfMeasure { get; set; } = Core.Entities.UnitOfMeasureType.Und;
    public decimal MinWholesaleQuantity { get; set; } = 6.000m;
    public decimal StockQuantity { get; set; }
    public bool IsCashAdvance { get; set; }
    public bool IsActive { get; set; }
    public decimal ProfitPercentage { get; set; }
    public decimal ReservedQuantity { get; set; }
    public decimal AvailableQuantity => StockQuantity - ReservedQuantity;

    // Parent/Group and Variant Properties
    public int? ParentProductId { get; set; }
    public bool IsGroupHeader { get; set; }
    public int VariantCount { get; set; }
    public decimal ConsolidatedStock { get; set; }
}
