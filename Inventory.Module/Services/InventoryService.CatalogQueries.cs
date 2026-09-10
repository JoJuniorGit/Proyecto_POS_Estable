using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.Services;

public partial class InventoryService
{
    public async Task<List<Core.DTOs.ProductQuickInfoDto>> GetSuggestionsAsync(string filter, bool activeOnly, System.Threading.CancellationToken token)
    {
        IQueryable<Product> query = _context.Products.AsNoTracking();

        if (activeOnly)
        {
            query = query.Where(p => p.IsActive && !p.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            string trimmed = filter.Trim();
            string lower = trimmed.ToLower();

            // Direct barcode match check for variants or standalone products
            var exactSkuMatch = await query
                .Where(p => p.SKU == trimmed)
                .Select(p => new Core.DTOs.ProductQuickInfoDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    SKU = p.SKU,
                    PriceUSD = p.PriceUSD,
                    PriceRetailUSD = p.PriceRetailUSD,
                    PriceWholesaleUSD = p.PriceWholesaleUSD,
                    PriceBsS = p.PriceBsS,
                    StockQuantity = p.StockQuantity,
                    ReservedQuantity = p.ReservedQuantity,
                    IsCashAdvance = p.IsCashAdvance,
                    IsActive = p.IsActive,
                    ProfitPercentage = p.ProfitPercentage,
                    ParentProductId = p.ParentProductId,
                    ParentIsStockShared = p.ParentProduct != null && p.ParentProduct.IsStockShared,
                    IsGroupHeader = p.IsGroupHeader,
                    IsStockShared = p.IsStockShared,
                    HasIndependentPricing = p.HasIndependentPricing,
                    ConversionFactor = p.ConversionFactor,
                    VariantCount = p.IsGroupHeader ? p.Variants.Count(v => !v.IsDeleted) : 0,
                    ConsolidatedStock = p.IsGroupHeader
                        ? (p.IsStockShared ? p.StockQuantity : (p.Variants.Where(v => !v.IsDeleted).Sum(v => (decimal?)v.StockQuantity) ?? 0m))
                        : p.StockQuantity
                })
                .FirstOrDefaultAsync(token);

            if (exactSkuMatch != null)
            {
                return new List<Core.DTOs.ProductQuickInfoDto> { exactSkuMatch };
            }

            // For text/partial search, return root products (parents or standalone)
            query = query.Where(p => p.ParentProductId == null && (p.Name.ToLower().Contains(lower) || p.SKU.ToLower().Contains(lower)));
        }
        else
        {
            query = query.Where(p => p.ParentProductId == null);
        }

        return await query
            .OrderBy(p => p.Name)
            .Take(10)
            .Select(p => new Core.DTOs.ProductQuickInfoDto
            {
                Id = p.Id,
                Name = p.Name,
                SKU = p.SKU,
                PriceUSD = p.PriceUSD,
                PriceRetailUSD = p.PriceRetailUSD,
                PriceWholesaleUSD = p.PriceWholesaleUSD,
                PriceBsS = p.PriceBsS,
                StockQuantity = p.StockQuantity,
                ReservedQuantity = p.ReservedQuantity,
                IsCashAdvance = p.IsCashAdvance,
                IsActive = p.IsActive,
                ProfitPercentage = p.ProfitPercentage,
                ParentProductId = p.ParentProductId,
                ParentIsStockShared = p.ParentProduct != null && p.ParentProduct.IsStockShared,
                IsGroupHeader = p.IsGroupHeader,
                IsStockShared = p.IsStockShared,
                HasIndependentPricing = p.HasIndependentPricing,
                ConversionFactor = p.ConversionFactor,
                VariantCount = p.IsGroupHeader ? p.Variants.Count(v => !v.IsDeleted) : 0,
                ConsolidatedStock = p.IsGroupHeader
                    ? (p.IsStockShared ? p.StockQuantity : (p.Variants.Where(v => !v.IsDeleted).Sum(v => (decimal?)v.StockQuantity) ?? 0m))
                    : p.StockQuantity
            })
            .ToListAsync(token);
    }

    public async Task<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>> GetProductsPagedAsync(string? filter, int page, int pageSize, string? statusFilter = null, string? sortBy = null, bool isDescending = false, System.Threading.CancellationToken token = default)
    {
        IQueryable<Product> query = _context.Products.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            var status = statusFilter.ToLower().Trim();
            if (status == "active")
            {
                query = query.Where(p => p.IsActive && !p.IsDeleted);
            }
            else if (status == "inactive")
            {
                query = query.Where(p => !p.IsActive && !p.IsDeleted);
            }
            else if (status == "deleted" || status == "archived")
            {
                query = query.Where(p => p.IsDeleted);
            }
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            string lower = filter.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(lower) || p.SKU.ToLower().Contains(lower));
        }

        var totalCount = await query.CountAsync(token);

        query = (sortBy?.ToLower().Trim()) switch
        {
            "name" => isDescending 
                ? query.OrderByDescending(p => p.Name) 
                : query.OrderBy(p => p.Name),

            "price" or "priceretail" => isDescending 
                ? query.OrderByDescending(p => p.PriceRetailUSD > 0 ? p.PriceRetailUSD : p.PriceUSD).ThenByDescending(p => p.Name)
                : query.OrderBy(p => p.PriceRetailUSD > 0 ? p.PriceRetailUSD : p.PriceUSD).ThenBy(p => p.Name),

            "cost" or "costprice" => isDescending 
                ? query.OrderByDescending(p => p.CostPriceUSD > 0 ? p.CostPriceUSD : p.Cost).ThenByDescending(p => p.Name)
                : query.OrderBy(p => p.CostPriceUSD > 0 ? p.CostPriceUSD : p.Cost).ThenBy(p => p.Name),

            "stock" or "stockquantity" => isDescending 
                ? query.OrderByDescending(p => p.StockQuantity).ThenByDescending(p => p.Name)
                : query.OrderBy(p => p.StockQuantity).ThenBy(p => p.Name),

            "sku" or "barcode" => isDescending 
                ? query.OrderByDescending(p => (p.SKU ?? "").Length).ThenByDescending(p => p.SKU)
                : query.OrderBy(p => (p.SKU ?? "").Length).ThenBy(p => p.SKU),

            _ => isDescending ? query.OrderByDescending(p => p.Name) : query.OrderBy(p => p.Name)
        };

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new Core.DTOs.ProductDto
            {
                Id = p.Id,
                Name = p.Name,
                SKU = p.SKU,
                Description = p.Description,
                PriceUSD = p.PriceUSD,
                PriceRetailUSD = p.PriceRetailUSD,
                PriceWholesaleUSD = p.PriceWholesaleUSD,
                CostPriceUSD = p.CostPriceUSD,
                ProfitMarginRetail = p.ProfitMarginRetail,
                ProfitMarginWholesale = p.ProfitMarginWholesale,
                MinWholesaleQuantity = p.MinWholesaleQuantity,
                HasWholesale = p.HasWholesale,
                IsFractional = p.IsFractional,
                PriceBsS = p.PriceBsS,
                Cost = p.Cost,
                StockQuantity = p.StockQuantity,
                ProfitPercentage = p.ProfitPercentage,
                UnitOfMeasure = p.UnitOfMeasure,
                LowStockThreshold = p.LowStockThreshold,
                IsCashAdvance = p.IsCashAdvance,
                IsActive = p.IsActive,
                IsDeleted = p.IsDeleted,
                ReservedQuantity = p.ReservedQuantity,
                ParentProductId = p.ParentProductId,
                ParentIsStockShared = p.ParentProduct != null && p.ParentProduct.IsStockShared,
                IsGroupHeader = p.IsGroupHeader,
                IsStockShared = p.IsStockShared,
                HasIndependentPricing = p.HasIndependentPricing,
                ConversionFactor = p.ConversionFactor,
                GroupKey = p.GroupKey,
                VariantCount = p.IsGroupHeader ? p.Variants.Count(v => !v.IsDeleted) : 0,
                ConsolidatedStock = p.IsGroupHeader
                    ? (p.IsStockShared ? p.StockQuantity : (p.Variants.Where(v => !v.IsDeleted).Sum(v => (decimal?)v.StockQuantity) ?? 0m))
                    : p.StockQuantity,
            })
            .ToListAsync(token);

        return new Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>
        {
            Items = items,
            TotalCount = totalCount,
            HasMore = (page * pageSize) < totalCount
        };
    }
}
