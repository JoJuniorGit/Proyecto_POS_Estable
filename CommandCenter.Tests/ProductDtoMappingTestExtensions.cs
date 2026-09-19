using Core.DTOs;

namespace CommandCenter.Tests;

internal static class ProductDtoMappingTestExtensions
{
    internal static UpdateProductDto ToUpdateProductDto(this ProductDto product) => new()
    {
        Id = product.Id,
        Name = product.Name,
        SKU = product.SKU,
        Description = product.Description,
        PriceUSD = product.PriceUSD,
        PriceRetailUSD = product.PriceRetailUSD,
        PriceBsS = product.PriceBsS,
        PriceWholesaleUSD = product.PriceWholesaleUSD,
        CostPriceUSD = product.CostPriceUSD,
        ProfitMarginRetail = product.ProfitMarginRetail,
        ProfitMarginWholesale = product.ProfitMarginWholesale,
        MinWholesaleQuantity = product.MinWholesaleQuantity,
        HasWholesale = product.HasWholesale,
        IsFractional = product.IsFractional,
        UnitOfMeasure = product.UnitOfMeasure,
        LowStockThreshold = product.LowStockThreshold,
        IsCashAdvance = product.IsCashAdvance,
        IsActive = product.IsActive,
        ParentProductId = product.ParentProductId,
        IsGroupHeader = product.IsGroupHeader,
        IsStockShared = product.IsStockShared,
        HasIndependentPricing = product.HasIndependentPricing,
        ConversionFactor = product.ConversionFactor,
        GroupKey = product.GroupKey
    };
}
