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
    public string Name { get; set; } = string.Empty;
    public string SKU { get; set; } = string.Empty; // Barcode
    public string Description { get; set; } = string.Empty;
    public decimal PriceUSD { get; set; }
    public decimal PriceRetailUSD { get; set; }
    public decimal PriceWholesaleUSD { get; set; }
    public decimal CostPriceUSD { get; set; }
    public decimal ProfitMarginRetail { get; set; }
    public decimal ProfitMarginWholesale { get; set; }
    public decimal MinWholesaleQuantity { get; set; } = 6.000m;
    public bool HasWholesale { get; set; } = false;
    public bool IsFractional { get; set; } = false;
    public UnitOfMeasureType UnitOfMeasure { get; set; } = UnitOfMeasureType.Und;
    public string UnitOfMeasureStr => UnitOfMeasure.ToString();
    public decimal PriceBsS { get; set; } // The "tagged" price in local currency
    public decimal LastConversionRate { get; set; } // The rate used to calculate PriceBsS
    public decimal Cost { get; set; }
    public decimal StockQuantity { get; set; }
    public decimal ProfitPercentage { get; set; }
    public decimal LowStockThreshold { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; } = false;
    
    // Identifies the product used for requesting physical cash from the register
    public bool IsCashAdvance { get; set; } = false;

    public decimal ReservedQuantity { get; set; }

    [System.ComponentModel.DataAnnotations.Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
