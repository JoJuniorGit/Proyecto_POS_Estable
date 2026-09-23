using Core.DTOs;

namespace Desktop.Client.Services;

public static class ProductClientMapping
{
    public static CreateProductDto ToCreateProductDto(ProductDto source) => new()
    {
        Name = source.Name,
        SKU = source.SKU,
        Description = source.Description,
        PriceUSD = source.PriceUSD,
        PriceRetailUSD = source.PriceRetailUSD,
        PriceBsS = source.PriceBsS,
        PriceWholesaleUSD = source.PriceWholesaleUSD,
        CostPriceUSD = source.CostPriceUSD,
        ProfitMarginRetail = source.ProfitMarginRetail,
        ProfitMarginWholesale = source.ProfitMarginWholesale,
        MinWholesaleQuantity = source.MinWholesaleQuantity,
        HasWholesale = source.HasWholesale,
        IsFractional = source.IsFractional,
        UnitOfMeasure = source.UnitOfMeasure,
        LowStockThreshold = source.LowStockThreshold,
        StockQuantity = source.StockQuantity,
        IsCashAdvance = source.IsCashAdvance,
        IsActive = source.IsActive,
        ParentProductId = source.ParentProductId,
        IsGroupHeader = source.IsGroupHeader,
        IsStockShared = source.IsStockShared,
        HasIndependentPricing = source.HasIndependentPricing,
        ConversionFactor = source.ConversionFactor,
        GroupKey = source.GroupKey
    };

    public static UpdateProductDto ToUpdateProductDto(ProductDto source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        SKU = source.SKU,
        Description = source.Description,
        PriceUSD = source.PriceUSD,
        PriceRetailUSD = source.PriceRetailUSD,
        PriceBsS = source.PriceBsS,
        PriceWholesaleUSD = source.PriceWholesaleUSD,
        CostPriceUSD = source.CostPriceUSD,
        ProfitMarginRetail = source.ProfitMarginRetail,
        ProfitMarginWholesale = source.ProfitMarginWholesale,
        MinWholesaleQuantity = source.MinWholesaleQuantity,
        HasWholesale = source.HasWholesale,
        IsFractional = source.IsFractional,
        UnitOfMeasure = source.UnitOfMeasure,
        LowStockThreshold = source.LowStockThreshold,
        IsCashAdvance = source.IsCashAdvance,
        IsActive = source.IsActive,
        ParentProductId = source.ParentProductId,
        IsGroupHeader = source.IsGroupHeader,
        IsStockShared = source.IsStockShared,
        HasIndependentPricing = source.HasIndependentPricing,
        ConversionFactor = source.ConversionFactor,
        GroupKey = source.GroupKey
    };

    public static UpdateProductDto ToUpdateProductDto(CreateProductDto source, int id) => new()
    {
        Id = id,
        Name = source.Name,
        SKU = source.SKU,
        Description = source.Description,
        PriceUSD = source.PriceUSD,
        PriceRetailUSD = source.PriceRetailUSD,
        PriceBsS = source.PriceBsS,
        PriceWholesaleUSD = source.PriceWholesaleUSD,
        CostPriceUSD = source.CostPriceUSD,
        ProfitMarginRetail = source.ProfitMarginRetail,
        ProfitMarginWholesale = source.ProfitMarginWholesale,
        MinWholesaleQuantity = source.MinWholesaleQuantity,
        HasWholesale = source.HasWholesale,
        IsFractional = source.IsFractional,
        UnitOfMeasure = source.UnitOfMeasure,
        LowStockThreshold = source.LowStockThreshold,
        IsCashAdvance = source.IsCashAdvance,
        IsActive = source.IsActive,
        ParentProductId = source.ParentProductId,
        IsGroupHeader = source.IsGroupHeader,
        IsStockShared = source.IsStockShared,
        HasIndependentPricing = source.HasIndependentPricing,
        ConversionFactor = source.ConversionFactor,
        GroupKey = source.GroupKey
    };

    public static ProductDto Merge(ProductDto target, CreateProductDto source)
    {
        target.Name = source.Name;
        target.SKU = source.SKU ?? string.Empty;
        target.Description = source.Description;
        target.PriceUSD = source.PriceUSD;
        target.PriceRetailUSD = source.PriceRetailUSD;
        target.PriceWholesaleUSD = source.PriceWholesaleUSD;
        target.CostPriceUSD = source.CostPriceUSD;
        target.Cost = source.CostPriceUSD;
        target.ProfitMarginRetail = source.ProfitMarginRetail;
        target.ProfitPercentage = source.ProfitMarginRetail;
        target.ProfitMarginWholesale = source.ProfitMarginWholesale;
        target.MinWholesaleQuantity = source.MinWholesaleQuantity;
        target.HasWholesale = source.HasWholesale;
        target.IsFractional = source.IsFractional;
        target.UnitOfMeasure = source.UnitOfMeasure;
        target.PriceBsS = source.PriceBsS;
        target.LowStockThreshold = source.LowStockThreshold;
        target.IsCashAdvance = source.IsCashAdvance;
        target.IsActive = source.IsActive;
        target.ParentProductId = source.ParentProductId;
        target.IsGroupHeader = source.IsGroupHeader;
        target.IsStockShared = source.IsStockShared;
        target.HasIndependentPricing = source.HasIndependentPricing;
        target.ConversionFactor = source.ConversionFactor;
        target.GroupKey = source.GroupKey;
        return target;
    }
}
