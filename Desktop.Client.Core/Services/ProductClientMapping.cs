using Core.DTOs;
using System.Linq;

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
        GroupKey = source.GroupKey,
        // 8.179-T2 (S3): clonar conserva las categorías del producto origen (semántica de reemplazo).
        CategoryIds = source.Categories?.Select(c => c.Id).ToList()
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
        GroupKey = source.GroupKey,
        // 8.184-T3 (S4): reenvío obligatorio de las reglas por artículo — sin esto cada edición,
        // auto-save o guardado de variantes limpiaría el override (null = heredar global).
        RoundingRuleUsd = source.RoundingRuleUsd,
        RoundingRuleCashBs = source.RoundingRuleCashBs,
        // 8.179-T2 (S3): los flujos que parten de un ProductDto releído (auto-save, variantes)
        // envían el set completo actual; el GET por Id ya incluye Categories.
        CategoryIds = source.Categories?.Select(c => c.Id).ToList()
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
        GroupKey = source.GroupKey,
        // 8.184-T3 (S4): el diálogo envía la selección completa de reglas por artículo (null = heredar).
        RoundingRuleUsd = source.RoundingRuleUsd,
        RoundingRuleCashBs = source.RoundingRuleCashBs,
        // 8.179-T2 (S3): el diálogo envía SIEMPRE la lista completa mostrada (marcar/desmarcar =
        // reemplazo); null solo cuando la carga falló y no debe pisar la asignación del servidor.
        CategoryIds = source.CategoryIds
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
        // 8.184-T3 (S4): el snapshot local refleja lo recién guardado (null = heredar global); sin
        // esto un flujo futuro que remapee el item del grid reenviaría las reglas viejas.
        target.RoundingRuleUsd = source.RoundingRuleUsd;
        target.RoundingRuleCashBs = source.RoundingRuleCashBs;
        return target;
    }
}
