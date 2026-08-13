namespace Core.DTOs;

public class ProductDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SKU { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal PriceUSD { get; set; }
    public decimal PriceRetailUSD { get; set; }
    public decimal PriceWholesaleUSD { get; set; }
    public decimal CostPriceUSD { get; set; }
    public decimal ProfitMarginRetail { get; set; }
    public decimal ProfitMarginWholesale { get; set; }
    public decimal MinWholesaleQuantity { get; set; } = 6.000m;
    public bool HasWholesale { get; set; } = false;
    public bool IsFractional { get; set; } = false;
    public Core.Entities.UnitOfMeasureType UnitOfMeasure { get; set; } = Core.Entities.UnitOfMeasureType.Und;
    public string UnitOfMeasureStr => UnitOfMeasure.ToString();
    public decimal PriceBsS { get; set; }
    public decimal Cost { get; set; }
    public int StockQuantity { get; set; }
    public decimal ProfitPercentage { get; set; }
    public int LowStockThreshold { get; set; }
    public bool IsCashAdvance { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; } = false;
    public int ReservedQuantity { get; set; }
    public int AvailableQuantity => StockQuantity - ReservedQuantity;
}
