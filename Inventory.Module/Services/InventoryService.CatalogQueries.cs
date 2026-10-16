using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.Services;

public partial class InventoryService
{
    public async Task<List<Core.DTOs.ProductQuickInfoDto>> GetSuggestionsAsync(string filter, bool activeOnly, System.Threading.CancellationToken token, int? categoryId = null)
    {
        IQueryable<Product> query = _context.Products.AsNoTracking();

        // 8.185-T2 (S1/S2): default global de efectivo leído una vez; la regla de cada ítem se
        // resuelve en memoria (override ?? default), sin costo ni consultas por artículo.
        var defaultCashRule = await ResolveDefaultCashRoundingRuleAsync();

        if (activeOnly)
        {
            query = query.Where(p => p.IsActive && !p.IsDeleted);
        }

        // 8.179-T1 (S4): filtro por subárbol completo de la categoría (POS Web).
        if (categoryId.HasValue)
        {
            var subtreeIds = await ResolveCategorySubtreeIdsAsync(categoryId.Value, token);
            query = query.Where(p => _context.ProductCategories.Any(pc => pc.ProductId == p.Id && subtreeIds.Contains(pc.CategoryId)));
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
                        : p.StockQuantity,
                    RoundingRuleCashBs = p.RoundingRuleCashBs
                })
                .FirstOrDefaultAsync(token);

            if (exactSkuMatch != null)
            {
                exactSkuMatch.RoundingRuleCashBs ??= defaultCashRule;
                return new List<Core.DTOs.ProductQuickInfoDto> { exactSkuMatch };
            }

            // For text/partial search, return root products (parents or standalone)
            if (_context.Database.IsNpgsql())
            {
                query = query.Where(p => p.ParentProductId == null && (EF.Functions.ILike(p.Name, $"%{lower}%") || EF.Functions.ILike(p.SKU, $"%{lower}%")));
            }
            else
            {
                query = query.Where(p => p.ParentProductId == null && (p.Name.ToLower().Contains(lower) || p.SKU.ToLower().Contains(lower)));
            }
        }
        else
        {
            query = query.Where(p => p.ParentProductId == null);
        }

        var items = await query
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
                    : p.StockQuantity,
                RoundingRuleCashBs = p.RoundingRuleCashBs
            })
            .ToListAsync(token);

        foreach (var item in items)
        {
            item.RoundingRuleCashBs ??= defaultCashRule;
        }

        return items;
    }

    public async Task<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>> GetProductsPagedAsync(string? filter, int page, int pageSize, string? statusFilter = null, string? sortBy = null, bool isDescending = false, System.Threading.CancellationToken token = default, int? categoryId = null)
    {
        IQueryable<Product> query = _context.Products.AsNoTracking();

        // 8.179-T1 (S4): categoryId se agrega AL FINAL de la firma para no romper llamadas
        // posicionales existentes; filtra por la categoría y todas sus descendientes.
        if (categoryId.HasValue)
        {
            var subtreeIds = await ResolveCategorySubtreeIdsAsync(categoryId.Value, token);
            query = query.Where(p => _context.ProductCategories.Any(pc => pc.ProductId == p.Id && subtreeIds.Contains(pc.CategoryId)));
        }

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
            if (_context.Database.IsNpgsql())
            {
                query = query.Where(p => EF.Functions.ILike(p.Name, $"%{lower}%") || EF.Functions.ILike(p.SKU, $"%{lower}%"));
            }
            else
            {
                query = query.Where(p => p.Name.ToLower().Contains(lower) || p.SKU.ToLower().Contains(lower));
            }
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
                // 8.185-fix: las reglas de redondeo por articulo viajan en el listado paginado del
                // catalogo — sin estas dos columnas la grilla WPF las pierde (regla null => Bs.S
                // canonico en pantalla mientras el POS, que resuelve server-side, muestra el efectivo).
                RoundingRuleUsd = p.RoundingRuleUsd,
                RoundingRuleCashBs = p.RoundingRuleCashBs,
                VariantCount = p.IsGroupHeader ? p.Variants.Count(v => !v.IsDeleted) : 0,
                ConsolidatedStock = p.IsGroupHeader
                    ? (p.IsStockShared ? p.StockQuantity : (p.Variants.Where(v => !v.IsDeleted).Sum(v => (decimal?)v.StockQuantity) ?? 0m))
                    : p.StockQuantity,
                // 8.179-T1 (S3): subconsulta EF al junction (Product no tiene navegación inversa).
                Categories = _context.ProductCategories
                    .Where(pc => pc.ProductId == p.Id)
                    .OrderBy(pc => pc.Category.Name)
                    .Select(pc => new Core.DTOs.CategoryRefDto(pc.CategoryId, pc.Category.Name))
                    .ToList(),
            })
            .ToListAsync(token);

        return new Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>
        {
            Items = items,
            TotalCount = totalCount,
            HasMore = (page * pageSize) < totalCount
        };
    }

    // 8.179-T1 (S4): set categoría + descendientes calculado en memoria (BFS) para ser
    // provider-agnóstico (InMemory/SQLite/PostgreSQL) sin CTE recursivo. Categoría desconocida → vacío.
    private async Task<List<int>> ResolveCategorySubtreeIdsAsync(int categoryId, System.Threading.CancellationToken cancellationToken)
    {
        var nodes = await _context.Categories.AsNoTracking()
            .Select(c => new { c.Id, c.ParentCategoryId })
            .ToListAsync(cancellationToken);

        if (!nodes.Any(n => n.Id == categoryId))
        {
            return new List<int>();
        }

        var subtreeIds = new HashSet<int> { categoryId };
        var queue = new Queue<int>();
        queue.Enqueue(categoryId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var child in nodes.Where(n => n.ParentCategoryId == current))
            {
                if (subtreeIds.Add(child.Id))
                {
                    queue.Enqueue(child.Id);
                }
            }
        }

        return subtreeIds.ToList();
    }
}
