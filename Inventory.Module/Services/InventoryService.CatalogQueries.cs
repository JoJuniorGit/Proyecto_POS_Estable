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
                    // 8.186-T1 (S1): el canal Mayor necesita la bandera real para gatear el efectivo.
                    HasWholesale = p.HasWholesale,
                    RoundingRuleCashBs = p.RoundingRuleCashBs
                })
                .FirstOrDefaultAsync(token);

            if (exactSkuMatch != null)
            {
                exactSkuMatch.RoundingRuleCashBs ??= defaultCashRule;
                // 8.186-T1 (S1/S3): efectivo Bs.S proyectado en la misma pasada (tasa/toggle una vez).
                await ProjectEffectiveBsSBatchAsync(new List<Core.DTOs.ProductQuickInfoDto> { exactSkuMatch }, defaultCashRule, token);
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
                // 8.186-T1 (S1): el canal Mayor necesita la bandera real para gatear el efectivo.
                HasWholesale = p.HasWholesale,
                RoundingRuleCashBs = p.RoundingRuleCashBs
            })
            .ToListAsync(token);

        foreach (var item in items)
        {
            item.RoundingRuleCashBs ??= defaultCashRule;
        }

        // 8.186-T1 (S1/S3): proyección server-side del efectivo con UNA lectura de tasa/regla/toggle.
        await ProjectEffectiveBsSBatchAsync(items, defaultCashRule, token);

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
                // 8.192: Batch loading de categorias posterior para evitar subconsulta correlacionada N+1
            })
            .ToListAsync(token);

        // 8.192: Carga en lote de categorias asociadas para los items de la pagina actual
        if (items.Count > 0)
        {
            var productIds = items.Select(x => x.Id).ToList();
            var categoryLinks = await _context.ProductCategories.AsNoTracking()
                .Where(pc => productIds.Contains(pc.ProductId))
                .OrderBy(pc => pc.Category.Name)
                .Select(pc => new { pc.ProductId, Category = new Core.DTOs.CategoryRefDto(pc.CategoryId, pc.Category.Name) })
                .ToListAsync(token);

            var categoryLookup = categoryLinks
                .GroupBy(x => x.ProductId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<Core.DTOs.CategoryRefDto>)g.Select(x => x.Category).ToList());

            foreach (var item in items)
            {
                if (categoryLookup.TryGetValue(item.Id, out var cats))
                {
                    item.Categories = cats;
                }
            }
        }

        // 8.186-T1 (S1/S3): proyección server-side del efectivo Detal/Mayor para la grilla web con
        // UNA lectura de regla/tasa/toggle por request (la regla cruda ya viaja en el DTO).
        if (items.Count > 0)
        {
            var defaultCashRule = await ResolveDefaultCashRoundingRuleAsync();
            await ProjectEffectiveBsSBatchAsync(items, defaultCashRule, token);
        }

        return new Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>
        {
            Items = items,
            TotalCount = totalCount,
            HasMore = (page * pageSize) < totalCount
        };
    }

    /// <summary>
    /// Paso 14 (F2-WU4, F2-S4): productos del filtro de categoría. El subárbol se resuelve con el
    /// mismo BFS en memoria que el resto del catálogo; <paramref name="includeSubtree"/> false
    /// limita la búsqueda a la categoría raíz. Los Ids devueltos son distintos y ordenados para
    /// que el filtro sea determinista entre proveedores.
    /// </summary>
    public async Task<IReadOnlyList<int>> GetProductIdsByCategoryAsync(
        IReadOnlyCollection<int> categoryIds,
        bool includeSubtree,
        System.Threading.CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(categoryIds);
        if (categoryIds.Count == 0)
        {
            return Array.Empty<int>();
        }

        var resolvedCategoryIds = new HashSet<int>();
        foreach (var categoryId in categoryIds.Distinct())
        {
            if (!includeSubtree)
            {
                resolvedCategoryIds.Add(categoryId);
                continue;
            }

            // ResolveCategorySubtreeIdsAsync includes the root and returns an empty set when the
            // category does not exist, so unknown ids never magnify the report.
            foreach (var subtreeId in await ResolveCategorySubtreeIdsAsync(categoryId, cancellationToken))
            {
                resolvedCategoryIds.Add(subtreeId);
            }
        }

        if (resolvedCategoryIds.Count == 0)
        {
            return Array.Empty<int>();
        }

        var productIds = await _context.ProductCategories.AsNoTracking()
            .Where(link => resolvedCategoryIds.Contains(link.CategoryId))
            .Select(link => link.ProductId)
            .Distinct()
            .ToListAsync(cancellationToken);

        productIds.Sort();
        return productIds;
    }

    /// <summary>
    /// Paso 14 (F2-WU4, F2-S4): nombre y categoría de cada producto para los desgloses. Cuando un
    /// producto pertenece a varias categorías se elige la de menor Id para que el reporte no
    /// cambie de categoría entre corridas; los productos sin categoría se devuelven con los
    /// campos de categoría en null.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, Core.DTOs.ProfitabilityProductInfoDto>> GetProductCategoryInfoAsync(
        IReadOnlyCollection<int> productIds,
        System.Threading.CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        var ids = productIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, Core.DTOs.ProfitabilityProductInfoDto>();
        }

        var products = await _context.Products.AsNoTracking()
            .Where(product => ids.Contains(product.Id))
            .Select(product => new { product.Id, product.Name })
            .ToListAsync(cancellationToken);

        var links = await _context.ProductCategories.AsNoTracking()
            .Where(link => ids.Contains(link.ProductId))
            .Select(link => new { link.ProductId, link.CategoryId, link.Category.Name })
            .ToListAsync(cancellationToken);

        var firstCategoryByProduct = links
            .GroupBy(link => link.ProductId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(link => link.CategoryId).First());

        var info = new Dictionary<int, Core.DTOs.ProfitabilityProductInfoDto>(products.Count);
        foreach (var product in products)
        {
            firstCategoryByProduct.TryGetValue(product.Id, out var category);
            info[product.Id] = new Core.DTOs.ProfitabilityProductInfoDto
            {
                ProductId = product.Id,
                ProductName = product.Name,
                CategoryId = category?.CategoryId,
                CategoryName = category?.Name
            };
        }

        return info;
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
