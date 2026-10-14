namespace Core.DTOs;

public class ProductImportDto
{
    public string SKU { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public decimal CostPriceUSD { get; set; }
    public decimal ProfitMarginRetail { get; set; }
    public decimal PriceRetailUSD { get; set; }

    public decimal ProfitMarginWholesale { get; set; }
    public decimal PriceWholesaleUSD { get; set; }
    public decimal MinWholesaleQuantity { get; set; } = 6.000m;
    public bool HasWholesale { get; set; } = false;

    public bool IsFractional { get; set; } = false;
    public string UnitOfMeasure { get; set; } = "Und";

    public decimal StockQuantity { get; set; }
    public decimal LowStockThreshold { get; set; } = 5m;

    public bool IsActive { get; set; } = true;

    // Grouping & Variants Import Fields
    public string ProductType { get; set; } = "Normal"; // "Grupo" | "Variante" | "Normal"
    public string? GroupNameOrKey { get; set; }
    public bool IsStockShared { get; set; } = false;
    public bool HasIndependentPricing { get; set; } = false;
    public decimal ConversionFactor { get; set; } = 1.0000m;

    // 8.179-T1 (S5): ruta jerárquica de categoría separada por '>' (ej. "Alimentos > Lácteos").
    // Los segmentos inexistentes se auto-crean y el producto queda asignado a la hoja.
    public string? CategoryPath { get; set; }

    // Legacy Aliases for backwards compatibility
    public decimal Cost { get => CostPriceUSD; set => CostPriceUSD = value; }
    public decimal ProfitPercentage { get => ProfitMarginRetail; set => ProfitMarginRetail = value; }
    public decimal PriceUSD { get => PriceRetailUSD; set => PriceRetailUSD = value; }

    // UI Validation Fields (Ignored by DB)
    public bool IsValid { get; set; } = true;
    public string ErrorMessage { get; set; } = string.Empty;
}
