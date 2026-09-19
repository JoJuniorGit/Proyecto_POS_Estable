using System.Collections.Generic;
using System.Linq;
using Core.DTOs;
using Core.Entities;

namespace Core.Extensions;

public static class ProductMappingExtensions
{
    public static ProductDto ToDto(this Product product, bool canViewCost = true)
    {
        return new ProductDto
        {
            Id = product.Id,
            Name = product.Name,
            SKU = product.SKU,
            Description = product.Description,
            PriceUSD = product.PriceUSD,
            PriceRetailUSD = product.PriceRetailUSD,
            PriceWholesaleUSD = product.PriceWholesaleUSD,
            CostPriceUSD = canViewCost ? product.CostPriceUSD : 0m,
            ProfitMarginRetail = canViewCost ? product.ProfitMarginRetail : 0m,
            ProfitMarginWholesale = canViewCost ? product.ProfitMarginWholesale : 0m,
            MinWholesaleQuantity = product.MinWholesaleQuantity,
            HasWholesale = product.HasWholesale,
            IsFractional = product.IsFractional,
            PriceBsS = product.PriceBsS,
            Cost = canViewCost ? product.Cost : 0m,
            StockQuantity = product.StockQuantity,
            ProfitPercentage = canViewCost ? product.ProfitPercentage : 0m,
            UnitOfMeasure = product.UnitOfMeasure,
            LowStockThreshold = product.LowStockThreshold,
            IsCashAdvance = product.IsCashAdvance,
            IsActive = product.IsActive,
            IsDeleted = product.IsDeleted,
            ReservedQuantity = product.ReservedQuantity,
            ParentProductId = product.ParentProductId,
            ParentIsStockShared = product.ParentProduct != null && product.ParentProduct.IsStockShared,
            IsGroupHeader = product.IsGroupHeader,
            IsStockShared = product.IsStockShared,
            HasIndependentPricing = product.HasIndependentPricing,
            ConversionFactor = product.ConversionFactor,
            GroupKey = product.GroupKey,
            VariantCount = product.Variants != null ? product.Variants.Count(v => !v.IsDeleted) : 0,
            ConsolidatedStock = product.IsGroupHeader
                ? (product.IsStockShared ? product.StockQuantity : (product.Variants?.Where(v => !v.IsDeleted).Sum(v => (decimal?)v.StockQuantity) ?? 0m))
                : product.StockQuantity,
            Variants = product.Variants?.Select(v => v.ToDto(canViewCost)).ToList()
        };
    }

    public static Product ToEntity(this CreateProductDto request, decimal canonicalPriceBsS)
    {
        return new Product
        {
            Name = request.Name,
            SKU = request.SKU ?? string.Empty,
            Description = request.Description ?? string.Empty,
            PriceUSD = request.PriceUSD > 0 ? request.PriceUSD : request.PriceRetailUSD,
            PriceRetailUSD = request.PriceRetailUSD > 0 ? request.PriceRetailUSD : request.PriceUSD,
            PriceBsS = canonicalPriceBsS,
            PriceWholesaleUSD = request.PriceWholesaleUSD,
            CostPriceUSD = request.CostPriceUSD,
            ProfitMarginRetail = request.ProfitMarginRetail,
            ProfitMarginWholesale = request.ProfitMarginWholesale,
            MinWholesaleQuantity = request.MinWholesaleQuantity,
            HasWholesale = request.HasWholesale,
            IsFractional = request.IsFractional,
            UnitOfMeasure = request.UnitOfMeasure,
            LowStockThreshold = request.LowStockThreshold,
            StockQuantity = request.StockQuantity,
            IsCashAdvance = request.IsCashAdvance,
            IsActive = request.IsActive,
            ParentProductId = request.ParentProductId,
            IsGroupHeader = request.IsGroupHeader,
            IsStockShared = request.IsStockShared,
            HasIndependentPricing = request.HasIndependentPricing,
            ConversionFactor = request.ConversionFactor,
            GroupKey = request.GroupKey
        };
    }

    public static void UpdateFromDto(this Product existing, UpdateProductDto request, decimal canonicalPriceBsS)
    {
        existing.Name = request.Name;
        if (!string.IsNullOrWhiteSpace(request.SKU))
        {
            existing.SKU = request.SKU;
        }
        existing.Description = request.Description ?? string.Empty;
        existing.PriceRetailUSD = request.PriceRetailUSD > 0 ? request.PriceRetailUSD : request.PriceUSD;
        existing.PriceUSD = existing.PriceRetailUSD;
        existing.PriceWholesaleUSD = request.PriceWholesaleUSD;
        existing.CostPriceUSD = request.CostPriceUSD;
        existing.ProfitMarginRetail = request.ProfitMarginRetail;
        existing.ProfitMarginWholesale = request.ProfitMarginWholesale;
        existing.MinWholesaleQuantity = request.MinWholesaleQuantity;
        existing.HasWholesale = request.HasWholesale;
        existing.IsFractional = request.IsFractional;
        existing.UnitOfMeasure = request.UnitOfMeasure;
        existing.LowStockThreshold = request.LowStockThreshold;
        existing.IsCashAdvance = request.IsCashAdvance;
        existing.IsActive = request.IsActive;
        existing.ParentProductId = request.ParentProductId;
        existing.IsGroupHeader = request.IsGroupHeader;
        existing.IsStockShared = request.IsStockShared;
        existing.HasIndependentPricing = request.HasIndependentPricing;
        existing.ConversionFactor = request.ConversionFactor;
        existing.GroupKey = request.GroupKey;
        existing.PriceBsS = canonicalPriceBsS;
    }

    public static ProductDto MaskCosts(this ProductDto item)
    {
        item.CostPriceUSD = 0m;
        item.ProfitMarginRetail = 0m;
        item.ProfitMarginWholesale = 0m;
        item.Cost = 0m;
        item.ProfitPercentage = 0m;
        if (item.Variants != null)
        {
            foreach (var v in item.Variants)
            {
                v.MaskCosts();
            }
        }
        return item;
    }

    public static List<ProductDto> MaskCosts(this List<ProductDto> items)
    {
        foreach (var item in items)
        {
            item.MaskCosts();
        }
        return items;
    }
}
