using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Inventory.Module.Services;

public partial class InventoryService : IInventoryService
{
    private readonly InventoryDbContext _context;
    private readonly ICurrentUserService? _currentUserService;
    private readonly IMemoryCache? _cache;
    private const string ExchangeRateCacheKey = "bcv_rate_today";

    public InventoryService(InventoryDbContext context, ICurrentUserService? currentUserService = null, IMemoryCache? cache = null)
    {
        _context = context;
        _currentUserService = currentUserService;
        _cache = cache;
    }

    private void EnsureCatalogMutationPermission()
    {
        if (_currentUserService != null && !_currentUserService.CanMutateCatalog)
        {
            throw new UnauthorizedAccessException("El usuario actual no tiene permisos para modificar el catálogo ni realizar importaciones.");
        }
    }

    public async Task<List<Product>> GetAllProductsAsync()
    {
        return await _context.Products.AsNoTracking().ToListAsync();
    }

    public async Task<List<Product>> GetProductsByIdsAsync(IEnumerable<int> productIds)
    {
        var idList = productIds.Distinct().ToList();
        if (!idList.Any()) return new List<Product>();
        return await _context.Products.AsNoTracking().Where(p => idList.Contains(p.Id)).ToListAsync();
    }

    public async Task<Product?> GetProductByIdAsync(int id)
    {
        return await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<Product?> GetCashAdvanceProductAsync()
    {
        return await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.IsCashAdvance && !p.IsDeleted && p.IsActive);
    }

    private static MemoryCacheEntryOptions CreateProductCacheOptions() => new MemoryCacheEntryOptions
    {
        SlidingExpiration = TimeSpan.FromSeconds(15),
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
        Size = 1,
        Priority = CacheItemPriority.High
    };

    public void InvalidateProductSkuCache(string? sku)
    {
        if (!string.IsNullOrWhiteSpace(sku))
        {
            var normalized = sku.Trim().ToUpperInvariant();
            _cache?.Remove($"product_sku_{normalized}");
            _cache?.Remove($"product_quick_{normalized}");
        }
    }

    public void InvalidateAllProductCaches()
    {
        _cache?.Remove(ExchangeRateCacheKey);
    }

    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public async Task<Product?> GetProductBySkuAsync(string sku, bool useCache = true)
    {
        if (string.IsNullOrWhiteSpace(sku)) return null;
        var normalized = sku.Trim().ToUpperInvariant();

        if (!useCache || _cache == null)
        {
            return await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.SKU == sku);
        }

        var cacheKey = $"product_sku_{normalized}";
        if (_cache.TryGetValue(cacheKey, out Product? cachedProduct) && cachedProduct != null)
        {
            Core.Metrics.CacheMetrics.RecordHit();
            return cachedProduct;
        }

        Core.Metrics.CacheMetrics.RecordMiss();
        try
        {
            var product = await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.SKU == sku);
            if (product != null)
            {
                _cache.Set(cacheKey, product, CreateProductCacheOptions());
            }
            return product;
        }
        catch
        {
            throw;
        }
    }

    private static void ValidateAndCalculateProductPrices(Product product)
    {
        // Agrupador con Precios Individuales: No posee precios propios; cada variante define su costo/precio
        if (product.IsGroupHeader && product.HasIndependentPricing)
        {
            product.CostPriceUSD = 0m;
            product.Cost = 0m;
            product.ProfitMarginRetail = 0m;
            product.ProfitPercentage = 0m;
            product.PriceRetailUSD = 0m;
            product.PriceUSD = 0m;
            product.PriceBsS = 0m;
            product.HasWholesale = false;
            product.ProfitMarginWholesale = 0m;
            product.PriceWholesaleUSD = 0m;
            return;
        }

        if (product.MinWholesaleQuantity <= 0m)
        {
            product.MinWholesaleQuantity = 6.000m;
        }

        // Precedence Rule for Retail Price:
        // Manual price entry (> 0) takes absolute precedence over margin calculation
        if (product.PriceRetailUSD > 0)
        {
            if (product.ProfitMarginRetail == 0 && product.CostPriceUSD > 0)
            {
                decimal calculatedProfit = ((product.PriceRetailUSD / product.CostPriceUSD) - 1m) * 100m;
                product.ProfitMarginRetail = calculatedProfit < 0 ? 0 : Math.Round(calculatedProfit, 2, MidpointRounding.AwayFromZero);
            }
        }
        else if (product.CostPriceUSD > 0 && product.ProfitMarginRetail > 0)
        {
            decimal rawPrice = product.CostPriceUSD * (1m + (product.ProfitMarginRetail / 100m));
            product.PriceRetailUSD = Math.Ceiling(rawPrice * 100m) / 100m;
        }

        if (!product.HasWholesale)
        {
            product.PriceWholesaleUSD = product.PriceRetailUSD;
            product.ProfitMarginWholesale = product.ProfitMarginRetail;
        }
        else
        {
            // Precedence Rule for Wholesale Price:
            if (product.PriceWholesaleUSD > 0)
            {
                if (product.ProfitMarginWholesale == 0 && product.CostPriceUSD > 0)
                {
                    decimal calculatedProfit = ((product.PriceWholesaleUSD / product.CostPriceUSD) - 1m) * 100m;
                    product.ProfitMarginWholesale = calculatedProfit < 0 ? 0 : Math.Round(calculatedProfit, 2, MidpointRounding.AwayFromZero);
                }
            }
            else if (product.CostPriceUSD > 0 && product.ProfitMarginWholesale > 0)
            {
                decimal rawWholesalePrice = product.CostPriceUSD * (1m + (product.ProfitMarginWholesale / 100m));
                product.PriceWholesaleUSD = Math.Ceiling(rawWholesalePrice * 100m) / 100m;
            }

            if (product.PriceWholesaleUSD > product.PriceRetailUSD && product.PriceRetailUSD > 0)
            {
                throw new InvalidOperationException($"El precio al mayor (${product.PriceWholesaleUSD:F2}) no puede ser mayor al precio al detal (${product.PriceRetailUSD:F2}).");
            }

            if (product.ProfitMarginWholesale > product.ProfitMarginRetail && product.ProfitMarginRetail > 0)
            {
                throw new InvalidOperationException($"El margen al mayor ({product.ProfitMarginWholesale:F2}%) no puede ser mayor al margen al detal ({product.ProfitMarginRetail:F2}%).");
            }
        }

        if (product.PriceRetailUSD > 0)
        {
            product.PriceUSD = product.PriceRetailUSD;
        }
        if (product.CostPriceUSD > 0)
        {
            product.Cost = product.CostPriceUSD;
        }
        if (product.ProfitMarginRetail > 0)
        {
            product.ProfitPercentage = product.ProfitMarginRetail;
        }
    }

    private static void ValidateProductSku(string? sku, bool isGroupHeader = false)
    {
        if (isGroupHeader)
        {
            // Group headers can have alphanumeric or auto-generated GRP- SKUs
            return;
        }

        if (string.IsNullOrWhiteSpace(sku) || !System.Text.RegularExpressions.Regex.IsMatch(sku.Trim(), @"^[A-Za-z0-9\-_]{1,50}$"))
        {
            throw new InvalidOperationException("El SKU/Código del producto debe contener entre 1 y 50 caracteres alfanuméricos (letras, dígitos, guiones o guiones bajos).");
        }
    }

    public async Task<Product> CreateProductAsync(Product product)
    {
        EnsureCatalogMutationPermission();

        if (product.IsCashAdvance)
        {
            if (product.IsGroupHeader)
            {
                throw new InvalidOperationException("Un producto configurado como Servicio de Adelanto de Efectivo no puede ser un grupo de variantes.");
            }
            if (product.ParentProductId.HasValue)
            {
                throw new InvalidOperationException("Un producto configurado como Servicio de Adelanto de Efectivo no puede ser variante de un producto padre.");
            }

            product.IsFractional = false;
            product.UnitOfMeasure = UnitOfMeasureType.Und;
            product.StockQuantity = 0m;
            product.ReservedQuantity = 0m;
            product.LowStockThreshold = 0m;
            product.IsStockShared = false;
            product.HasIndependentPricing = false;
            product.ConversionFactor = 1.0000m;
        }
        else if (product.IsGroupHeader)
        {
            product.ConversionFactor = 1.0000m;
            if (string.IsNullOrWhiteSpace(product.SKU))
            {
                product.SKU = $"GRP-{DateTime.UtcNow.Ticks}";
            }
            product.ParentProductId = null;
            product.ReservedQuantity = 0m;
            if (!product.IsStockShared)
            {
                product.StockQuantity = 0m;
                product.LowStockThreshold = 0m;
            }
            if (string.IsNullOrWhiteSpace(product.GroupKey))
            {
                product.GroupKey = product.Name.Trim();
            }
            if (product.HasIndependentPricing)
            {
                product.CostPriceUSD = 0m;
                product.Cost = 0m;
                product.ProfitMarginRetail = 0m;
                product.ProfitPercentage = 0m;
                product.PriceRetailUSD = 0m;
                product.PriceUSD = 0m;
                product.PriceBsS = 0m;
                product.HasWholesale = false;
                product.ProfitMarginWholesale = 0m;
                product.PriceWholesaleUSD = 0m;
            }
        }
        else if (product.ParentProductId.HasValue)
        {
            var parent = await _context.Products.FindAsync(product.ParentProductId.Value);
            if (parent == null || parent.IsDeleted)
            {
                throw new InvalidOperationException("El producto padre especificado no existe o ha sido eliminado.");
            }

            product.IsGroupHeader = false;
            product.IsStockShared = false;
            product.HasIndependentPricing = false;

            if (parent.IsStockShared)
            {
                product.StockQuantity = 0m;
                product.ReservedQuantity = 0m;
                product.LowStockThreshold = 0m;
                if (product.ConversionFactor < 0.0001m || product.ConversionFactor > 1_000_000m)
                {
                    throw new InvalidOperationException(Core.Constants.InventoryMessages.ConversionFactorOutOfRange);
                }
            }
            else
            {
                product.ConversionFactor = 1.0000m;
            }

            if (!parent.HasIndependentPricing)
            {
                // Inherit pricing and configuration from parent
                product.PriceRetailUSD = parent.PriceRetailUSD;
                product.PriceWholesaleUSD = parent.PriceWholesaleUSD;
                product.CostPriceUSD = parent.CostPriceUSD;
                product.ProfitMarginRetail = parent.ProfitMarginRetail;
                product.ProfitMarginWholesale = parent.ProfitMarginWholesale;
                product.HasWholesale = parent.HasWholesale;
                product.IsFractional = parent.IsFractional;
                product.UnitOfMeasure = parent.UnitOfMeasure;
                product.MinWholesaleQuantity = parent.MinWholesaleQuantity;
                product.PriceUSD = parent.PriceRetailUSD;
                product.Cost = parent.CostPriceUSD;
                product.ProfitPercentage = parent.ProfitMarginRetail;
            }
        }
        else
        {
            product.IsStockShared = false;
            product.HasIndependentPricing = false;
            product.ConversionFactor = 1.0000m;
        }

        ValidateProductSku(product.SKU, product.IsGroupHeader);

        if (await _context.Products.AnyAsync(p => p.SKU == product.SKU && !p.IsDeleted))
        {
            throw new InvalidOperationException($"Product with SKU {product.SKU} already exists.");
        }

        ValidateAndCalculateProductPrices(product);

        _context.Products.Add(product);
        await _context.SaveChangesAsync();
        InvalidateProductSkuCache(product.SKU);
        if (product.ParentProduct != null) InvalidateProductSkuCache(product.ParentProduct.SKU);
        return product;
    }

    public async Task UpdateProductAsync(Product product)
    {
        EnsureCatalogMutationPermission();

        var existing = await _context.Products.FindAsync(product.Id);
        if (existing == null) throw new KeyNotFoundException($"Product {product.Id} not found");

        var entry = _context.Entry(existing);
        bool originalIsGroupHeader = entry.OriginalValues.GetValue<bool>(nameof(Product.IsGroupHeader));
        bool originalIsStockShared = entry.OriginalValues.GetValue<bool>(nameof(Product.IsStockShared));
        bool originalHasIndependentPricing = entry.OriginalValues.GetValue<bool>(nameof(Product.HasIndependentPricing));
        decimal originalConversionFactor = entry.OriginalValues.GetValue<decimal>(nameof(Product.ConversionFactor));

        if (originalIsGroupHeader)
        {
            if (product.IsStockShared != originalIsStockShared || product.HasIndependentPricing != originalHasIndependentPricing)
            {
                throw new InvalidOperationException("No se permite cambiar las banderas de Stock Compartido o Precios Independientes en un grupo existente.");
            }
        }

        if (!product.IsGroupHeader)
        {
            int activeVariants = await _context.Products.CountAsync(p => p.ParentProductId == product.Id && !p.IsDeleted);
            if (activeVariants > 0)
            {
                throw new InvalidOperationException($"No se puede desmarcar el grupo '{product.Name}' porque tiene {activeVariants} variantes asociadas. Desvincule o elimine las variantes primero.");
            }
            product.IsStockShared = false;
            product.HasIndependentPricing = false;
        }

        if (product.IsCashAdvance)
        {
            if (product.IsGroupHeader)
            {
                throw new InvalidOperationException("Un producto configurado como Servicio de Adelanto de Efectivo no puede ser un grupo de variantes.");
            }
            if (product.ParentProductId.HasValue)
            {
                throw new InvalidOperationException("Un producto configurado como Servicio de Adelanto de Efectivo no puede ser variante de un producto padre.");
            }

            product.IsFractional = false;
            product.UnitOfMeasure = UnitOfMeasureType.Und;
            product.StockQuantity = 0m;
            product.ReservedQuantity = 0m;
            product.LowStockThreshold = 0m;
            product.IsStockShared = false;
            product.HasIndependentPricing = false;
            product.ConversionFactor = 1.0000m;
        }
        else if (product.IsGroupHeader)
        {
            product.ConversionFactor = 1.0000m;
            if (string.IsNullOrWhiteSpace(product.SKU))
            {
                product.SKU = existing.SKU;
            }
            product.ParentProductId = null;
            product.ReservedQuantity = 0m;
            if (!product.IsStockShared)
            {
                product.StockQuantity = 0m;
                product.LowStockThreshold = 0m;
            }
            if (string.IsNullOrWhiteSpace(product.GroupKey))
            {
                product.GroupKey = product.Name.Trim();
            }
            if (product.HasIndependentPricing)
            {
                product.CostPriceUSD = 0m;
                product.Cost = 0m;
                product.ProfitMarginRetail = 0m;
                product.ProfitPercentage = 0m;
                product.PriceRetailUSD = 0m;
                product.PriceUSD = 0m;
                product.PriceBsS = 0m;
                product.HasWholesale = false;
                product.ProfitMarginWholesale = 0m;
                product.PriceWholesaleUSD = 0m;
            }
        }
        else if (product.ParentProductId.HasValue)
        {
            var parent = await _context.Products.FindAsync(product.ParentProductId.Value);
            if (parent != null && !parent.IsDeleted)
            {
                product.IsGroupHeader = false;
                product.IsStockShared = false;
                product.HasIndependentPricing = false;

                if (parent.IsStockShared)
                {
                    product.StockQuantity = 0m;
                    product.ReservedQuantity = 0m;
                    product.LowStockThreshold = 0m;
                    decimal factor = product.ConversionFactor > 0 ? product.ConversionFactor : (originalConversionFactor > 0 ? originalConversionFactor : 1.0000m);
                    if (factor < 0.0001m || factor > 1_000_000m)
                    {
                        throw new InvalidOperationException(Core.Constants.InventoryMessages.ConversionFactorOutOfRange);
                    }
                    product.ConversionFactor = factor;
                }
                else
                {
                    product.ConversionFactor = 1.0000m;
                }

                if (!parent.HasIndependentPricing)
                {
                    // Inherit pricing from parent
                    product.PriceRetailUSD = parent.PriceRetailUSD;
                    product.PriceWholesaleUSD = parent.PriceWholesaleUSD;
                    product.CostPriceUSD = parent.CostPriceUSD;
                    product.ProfitMarginRetail = parent.ProfitMarginRetail;
                    product.ProfitMarginWholesale = parent.ProfitMarginWholesale;
                    product.HasWholesale = parent.HasWholesale;
                    product.IsFractional = parent.IsFractional;
                    product.UnitOfMeasure = parent.UnitOfMeasure;
                    product.MinWholesaleQuantity = parent.MinWholesaleQuantity;
                    product.PriceUSD = parent.PriceRetailUSD;
                    product.Cost = parent.CostPriceUSD;
                    product.ProfitPercentage = parent.ProfitMarginRetail;
                }
            }
            else
            {
                product.ConversionFactor = 1.0000m;
            }
        }
        else
        {
            product.ConversionFactor = 1.0000m;
        }

        ValidateProductSku(product.SKU, product.IsGroupHeader);
        ValidateAndCalculateProductPrices(product);

        if (product.RowVersion == null || product.RowVersion.Length == 0)
        {
            product.RowVersion = existing.RowVersion;
        }

        await using var tx = _context.Database.IsRelational() ? await _context.Database.BeginTransactionAsync() : null;

        _context.Entry(existing).CurrentValues.SetValues(product);
        existing.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // If updating a parent product with HasIndependentPricing == false, propagate prices/costs to all active variants in batch
        if (product.IsGroupHeader && !product.HasIndependentPricing)
        {
            if (_context.Database.IsRelational())
            {
                await _context.Products
                    .Where(p => p.ParentProductId == product.Id && !p.IsDeleted)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(p => p.PriceRetailUSD, product.PriceRetailUSD)
                        .SetProperty(p => p.PriceWholesaleUSD, product.PriceWholesaleUSD)
                        .SetProperty(p => p.CostPriceUSD, product.CostPriceUSD)
                        .SetProperty(p => p.ProfitMarginRetail, product.ProfitMarginRetail)
                        .SetProperty(p => p.ProfitMarginWholesale, product.ProfitMarginWholesale)
                        .SetProperty(p => p.HasWholesale, product.HasWholesale)
                        .SetProperty(p => p.MinWholesaleQuantity, product.MinWholesaleQuantity)
                        .SetProperty(p => p.PriceUSD, product.PriceRetailUSD)
                        .SetProperty(p => p.Cost, product.CostPriceUSD)
                        .SetProperty(p => p.ProfitPercentage, product.ProfitMarginRetail)
                        .SetProperty(p => p.UpdatedAt, DateTime.UtcNow));
            }
            else
            {
                // In-memory or non-relational fallback
                var variants = await _context.Products.Where(p => p.ParentProductId == product.Id && !p.IsDeleted).ToListAsync();
                foreach (var v in variants)
                {
                    v.PriceRetailUSD = product.PriceRetailUSD;
                    v.PriceWholesaleUSD = product.PriceWholesaleUSD;
                    v.CostPriceUSD = product.CostPriceUSD;
                    v.ProfitMarginRetail = product.ProfitMarginRetail;
                    v.ProfitMarginWholesale = product.ProfitMarginWholesale;
                    v.HasWholesale = product.HasWholesale;
                    v.MinWholesaleQuantity = product.MinWholesaleQuantity;
                    v.PriceUSD = product.PriceRetailUSD;
                    v.Cost = product.CostPriceUSD;
                    v.ProfitPercentage = product.ProfitMarginRetail;
                    v.UpdatedAt = DateTime.UtcNow;
                }
                await _context.SaveChangesAsync();
            }
        }

        if (tx != null)
        {
            await tx.CommitAsync();
        }

        InvalidateProductSkuCache(product.SKU);
        if (product.IsGroupHeader || product.ParentProductId != null)
        {
            InvalidateAllProductCaches();
        }
    }

    public async Task SetProductStatusAsync(int id, bool isActive, bool isDeleted)
    {
        EnsureCatalogMutationPermission();
        var product = await _context.Products.FindAsync(id);
        if (product != null)
        {
            product.IsActive = isActive;
            product.IsDeleted = isDeleted;
            product.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            InvalidateProductSkuCache(product.SKU);
            if (product.IsGroupHeader) InvalidateAllProductCaches();
        }
    }

    public async Task RestoreProductAsync(int id)
    {
        EnsureCatalogMutationPermission();
        await SetProductStatusAsync(id, isActive: true, isDeleted: false);
    }

    public async Task<string> DeleteProductAsync(int id, bool forceHardDelete = false)
    {
        EnsureCatalogMutationPermission();
        var product = await _context.Products.FindAsync(id);
        if (product == null) return "not_found";

        // Safe delete rule: Block deleting a parent product if it has active variants
        if (product.IsGroupHeader)
        {
            int activeVariants = await _context.Products.CountAsync(p => p.ParentProductId == id && !p.IsDeleted);
            if (activeVariants > 0)
            {
                throw new InvalidOperationException($"No se puede eliminar el producto padre '{product.Name}' porque contiene {activeVariants} variantes asociadas. Desvincule o elimine primero las variantes.");
            }
        }

        string result = "archived";
        if (forceHardDelete)
        {
            try
            {
                _context.Products.Remove(product);
                await _context.SaveChangesAsync();
                result = "hard_deleted";
            }
            catch
            {
                // Has FK relationships (e.g. accounting history), fallback to archived
                _context.Entry(product).State = Microsoft.EntityFrameworkCore.EntityState.Unchanged;
                product.IsActive = false;
                product.IsDeleted = true;
                product.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                result = "archived";
            }
        }
        else
        {
            // Soft delete -> mark as deleted (archived for accounting)
            product.IsActive = false;
            product.IsDeleted = true;
            product.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            result = "archived";
        }

        InvalidateProductSkuCache(product.SKU);
        if (product.IsGroupHeader || product.ParentProductId != null) InvalidateAllProductCaches();
        return result;
    }

    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public async Task<Core.DTOs.ProductQuickInfoDto?> GetProductQuickInfoAsync(string sku, bool useCache = true)
    {
        if (string.IsNullOrWhiteSpace(sku)) return null;
        var normalized = sku.Trim().ToUpperInvariant();

        if (!useCache || _cache == null)
        {
            return await FetchProductQuickInfoFromDbAsync(sku);
        }

        var cacheKey = $"product_quick_{normalized}";
        if (_cache.TryGetValue(cacheKey, out Core.DTOs.ProductQuickInfoDto? cachedDto) && cachedDto != null)
        {
            Core.Metrics.CacheMetrics.RecordHit();
            return cachedDto;
        }

        Core.Metrics.CacheMetrics.RecordMiss();
        try
        {
            var dto = await FetchProductQuickInfoFromDbAsync(sku);
            if (dto != null)
            {
                _cache.Set(cacheKey, dto, CreateProductCacheOptions());
            }
            return dto;
        }
        catch
        {
            throw;
        }
    }

    private async Task<Core.DTOs.ProductQuickInfoDto?> FetchProductQuickInfoFromDbAsync(string sku)
    {
        return await _context.Products
            .AsNoTracking()
            .Where(p => p.SKU == sku)
            .Select(p => new Core.DTOs.ProductQuickInfoDto
            {
                Id = p.Id,
                SKU = p.SKU,
                Name = p.Name,
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
                IsGroupHeader = p.IsGroupHeader,
                IsStockShared = p.IsStockShared,
                HasIndependentPricing = p.HasIndependentPricing,
                ConversionFactor = p.ConversionFactor,
                VariantCount = p.IsGroupHeader ? p.Variants.Count(v => !v.IsDeleted) : 0,
                ConsolidatedStock = p.IsGroupHeader
                    ? (p.IsStockShared ? p.StockQuantity : (p.Variants.Where(v => !v.IsDeleted).Sum(v => (decimal?)v.StockQuantity) ?? 0m))
                    : p.StockQuantity
            })
            .FirstOrDefaultAsync();
    }

    public async Task EnrollInTransactionAsync(System.Data.Common.DbTransaction transaction, System.Threading.CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsRelational() && transaction != null)
        {
            var txConn = transaction.Connection;
            if (txConn != null && _context.Database.GetDbConnection() != txConn)
            {
                await _context.Database.CloseConnectionAsync();
                _context.Database.SetDbConnection(txConn);
            }
            await _context.Database.UseTransactionAsync(transaction, cancellationToken);
        }
    }
}
