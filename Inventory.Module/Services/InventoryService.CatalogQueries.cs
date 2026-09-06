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
                RowVersion = p.RowVersion
            })
            .ToListAsync(token);

        return new Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>
        {
            Items = items,
            TotalCount = totalCount,
            HasMore = (page * pageSize) < totalCount
        };
    }

    public async Task<List<Core.DTOs.ProductDto>> GetVariantOptionsAsync(int parentProductId)
    {
        var parent = await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parentProductId);
        bool isStockShared = parent?.IsStockShared ?? false;
        decimal parentStock = parent?.StockQuantity ?? 0m;
        bool hasIndepPricing = parent?.HasIndependentPricing ?? false;

        return await _context.Products
            .AsNoTracking()
            .Where(p => p.ParentProductId == parentProductId && !p.IsDeleted && p.IsActive)
            .OrderBy(p => p.Name)
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
                StockQuantity = isStockShared ? parentStock : p.StockQuantity,
                ProfitPercentage = p.ProfitPercentage,
                UnitOfMeasure = p.UnitOfMeasure,
                LowStockThreshold = p.LowStockThreshold,
                IsActive = p.IsActive,
                IsDeleted = p.IsDeleted,
                ReservedQuantity = isStockShared ? (parent != null ? parent.ReservedQuantity : 0m) : p.ReservedQuantity,
                ParentProductId = p.ParentProductId,
                ParentIsStockShared = isStockShared,
                IsGroupHeader = p.IsGroupHeader,
                IsStockShared = isStockShared,
                HasIndependentPricing = hasIndepPricing,
                ConversionFactor = p.ConversionFactor,
                GroupKey = p.GroupKey,
                ConsolidatedStock = isStockShared ? parentStock : p.StockQuantity,
                RowVersion = p.RowVersion
            })
            .ToListAsync();
    }

    public async Task<List<Core.DTOs.ProductDto>> GetParentProductsAsync()
    {
        return await _context.Products
            .AsNoTracking()
            .Where(p => p.IsGroupHeader && !p.IsDeleted && p.IsActive)
            .OrderBy(p => p.Name)
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
                IsActive = p.IsActive,
                IsDeleted = p.IsDeleted,
                IsGroupHeader = p.IsGroupHeader,
                IsStockShared = p.IsStockShared,
                HasIndependentPricing = p.HasIndependentPricing,
                ConversionFactor = p.ConversionFactor,
                GroupKey = p.GroupKey,
                VariantCount = p.Variants.Count(v => !v.IsDeleted),
                ConsolidatedStock = p.IsStockShared 
                    ? p.StockQuantity 
                    : (p.Variants.Where(v => !v.IsDeleted).Sum(v => (decimal?)v.StockQuantity) ?? 0m),
                RowVersion = p.RowVersion
            })
            .ToListAsync();
    }

    public async Task<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>> GetCandidateVariantsPagedAsync(int parentId, string? filter, int page, int pageSize, System.Threading.CancellationToken token = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 10;
        if (pageSize > 100) pageSize = 100;

        var query = _context.Products.AsNoTracking()
            .Where(p => !p.IsDeleted && p.IsActive && p.Id != parentId && !p.IsGroupHeader && p.ParentProductId != parentId && !p.IsCashAdvance);

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var lower = filter.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(lower) || p.SKU.ToLower().Contains(lower));
        }

        int totalCount = await query.CountAsync(token);

        var items = await query
            .OrderBy(p => p.Name)
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
                ParentIsStockShared = false,
                IsGroupHeader = p.IsGroupHeader,
                IsStockShared = p.IsStockShared,
                HasIndependentPricing = p.HasIndependentPricing,
                ConversionFactor = p.ConversionFactor,
                GroupKey = p.GroupKey,
                ConsolidatedStock = p.StockQuantity,
                RowVersion = p.RowVersion
            })
            .ToListAsync(token);

        return new Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>
        {
            Items = items,
            TotalCount = totalCount,
            HasMore = (page * pageSize) < totalCount
        };
    }

    public async Task<List<Core.DTOs.ProductDto>> LinkVariantsBatchAsync(int parentId, List<int> productIds, System.Threading.CancellationToken token = default)
    {
        EnsureCatalogMutationPermission();

        if (productIds == null || !productIds.Any())
        {
            return await GetVariantOptionsAsync(parentId);
        }

        var distinctIds = productIds.Distinct().ToList();

        var parent = await _context.Products.FirstOrDefaultAsync(p => p.Id == parentId, token);
        if (parent == null || parent.IsDeleted)
        {
            throw new KeyNotFoundException($"Producto padre con ID {parentId} no encontrado.");
        }
        if (!parent.IsGroupHeader)
        {
            throw new InvalidOperationException($"El producto '{parent.Name}' (ID {parentId}) no está configurado como producto padre / agrupador.");
        }

        var candidates = await _context.Products
            .Where(p => distinctIds.Contains(p.Id))
            .ToListAsync(token);

        if (candidates.Count != distinctIds.Count)
        {
            var foundIds = candidates.Select(c => c.Id).ToHashSet();
            var missingIds = distinctIds.Where(id => !foundIds.Contains(id)).ToList();
            throw new KeyNotFoundException($"No se encontraron los siguientes productos: {string.Join(", ", missingIds)}");
        }

        foreach (var prod in candidates)
        {
            if (prod.IsDeleted)
            {
                throw new InvalidOperationException($"El producto '{prod.Name}' (ID {prod.Id}) está eliminado.");
            }
            if (prod.IsGroupHeader)
            {
                throw new InvalidOperationException($"El producto '{prod.Name}' (ID {prod.Id}) es un producto padre y no puede ser vinculado como variante.");
            }
            if (prod.IsCashAdvance)
            {
                throw new InvalidOperationException($"El producto '{prod.Name}' (ID {prod.Id}) es de avance de efectivo y no puede ser variante.");
            }
            if (prod.Id == parentId)
            {
                throw new InvalidOperationException("Un producto no puede ser variante de sí mismo.");
            }
        }

        await using var tx = _context.Database.IsRelational() ? await _context.Database.BeginTransactionAsync(token) : null;
        try
        {
            foreach (var child in candidates)
            {
                child.ParentProductId = parent.Id;
                child.GroupKey = parent.GroupKey;

                if (!parent.HasIndependentPricing)
                {
                    child.CostPriceUSD = parent.CostPriceUSD;
                    child.ProfitMarginRetail = parent.ProfitMarginRetail;
                    child.PriceRetailUSD = parent.PriceRetailUSD;
                    child.ProfitMarginWholesale = parent.ProfitMarginWholesale;
                    child.PriceWholesaleUSD = parent.PriceWholesaleUSD;
                    child.HasWholesale = parent.HasWholesale;
                    child.MinWholesaleQuantity = parent.MinWholesaleQuantity;
                    child.PriceUSD = parent.PriceUSD;
                    child.PriceBsS = parent.PriceBsS;
                }

                if (parent.IsStockShared)
                {
                    child.StockQuantity = 0m;
                    if (child.ConversionFactor <= 0m)
                    {
                        child.ConversionFactor = 1.0000m;
                    }
                }
            }

            await _context.SaveChangesAsync(token);
            if (tx != null)
            {
                await tx.CommitAsync(token);
            }

            InvalidateAllProductCaches();
            Core.Logging.AppLogger.LogSecurityAudit($"[Catalog] Vinculación masiva de variantes al padre ID {parentId} ({parent.SKU}): {string.Join(", ", candidates.Select(c => $"{c.Id}:{c.SKU}"))} por usuario: {_currentUserService?.UserId ?? "System"}");

            return await GetVariantOptionsAsync(parentId);
        }
        catch (Exception)
        {
            if (tx != null)
            {
                await tx.RollbackAsync(token);
            }
            throw;
        }
    }

    public async Task<Core.DTOs.ProductDto> UnlinkVariantAsync(int parentId, int variantId, System.Threading.CancellationToken token = default)
    {
        EnsureCatalogMutationPermission();

        var parent = await _context.Products.FirstOrDefaultAsync(p => p.Id == parentId, token);
        if (parent == null || parent.IsDeleted)
        {
            throw new KeyNotFoundException($"Producto padre con ID {parentId} no encontrado.");
        }

        var variant = await _context.Products.FirstOrDefaultAsync(p => p.Id == variantId, token);
        if (variant == null || variant.IsDeleted)
        {
            throw new KeyNotFoundException($"Variante con ID {variantId} no encontrada.");
        }

        if (variant.ParentProductId != parentId)
        {
            throw new InvalidOperationException($"El producto '{variant.Name}' (ID {variantId}) no pertenece al producto padre ID {parentId}.");
        }

        if (variant.ReservedQuantity > 0)
        {
            var warnMsg = $"Intento de desvincular variante '{variant.Name}' (ID {variant.Id}, SKU {variant.SKU}) con reserva activa de {variant.ReservedQuantity}. Padre ID: {parentId}. Usuario: {_currentUserService?.UserId ?? "Unknown"}";
            Core.Logging.AppLogger.LogWarn(warnMsg, "InventoryService.UnlinkVariant");
            throw new InvalidOperationException($"No se puede desvincular la variante '{variant.Name}' porque tiene una reserva de stock activa de {variant.ReservedQuantity}. Complete o cancele la reserva antes de desvincular.");
        }

        variant.ParentProductId = null;
        variant.ConversionFactor = 1.0000m;
        if (parent.IsStockShared)
        {
            variant.StockQuantity = 0m;
            if (variant.LowStockThreshold <= 0m)
            {
                variant.LowStockThreshold = 5.000m;
            }
        }
        else if (variant.LowStockThreshold <= 0m)
        {
            variant.LowStockThreshold = 5.000m;
        }

        await _context.SaveChangesAsync(token);

        InvalidateProductSkuCache(variant.SKU);
        InvalidateProductSkuCache(parent.SKU);
        _cache?.Remove($"variant_options_{parentId}");

        Core.Logging.AppLogger.LogSecurityAudit($"[Catalog] Desvinculación de variante ID {variant.Id} ({variant.SKU}) del padre ID {parentId} ({parent.SKU}) por usuario: {_currentUserService?.UserId ?? "System"} en {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");

        return new Core.DTOs.ProductDto
        {
            Id = variant.Id,
            Name = variant.Name,
            SKU = variant.SKU,
            Description = variant.Description,
            PriceUSD = variant.PriceUSD,
            PriceRetailUSD = variant.PriceRetailUSD,
            PriceWholesaleUSD = variant.PriceWholesaleUSD,
            CostPriceUSD = variant.CostPriceUSD,
            ProfitMarginRetail = variant.ProfitMarginRetail,
            ProfitMarginWholesale = variant.ProfitMarginWholesale,
            MinWholesaleQuantity = variant.MinWholesaleQuantity,
            HasWholesale = variant.HasWholesale,
            IsFractional = variant.IsFractional,
            PriceBsS = variant.PriceBsS,
            Cost = variant.Cost,
            StockQuantity = variant.StockQuantity,
            ProfitPercentage = variant.ProfitPercentage,
            UnitOfMeasure = variant.UnitOfMeasure,
            LowStockThreshold = variant.LowStockThreshold,
            IsCashAdvance = variant.IsCashAdvance,
            IsActive = variant.IsActive,
            IsDeleted = variant.IsDeleted,
            ReservedQuantity = variant.ReservedQuantity,
            ParentProductId = variant.ParentProductId,
            ParentIsStockShared = false,
            IsGroupHeader = variant.IsGroupHeader,
            IsStockShared = variant.IsStockShared,
            HasIndependentPricing = variant.HasIndependentPricing,
            ConversionFactor = variant.ConversionFactor,
            GroupKey = variant.GroupKey,
            ConsolidatedStock = variant.StockQuantity,
            RowVersion = variant.RowVersion
        };
    }
}
