using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Inventory.Module.Services;

public partial class InventoryService : IInventoryService
{
    private readonly InventoryDbContext _context;
    private readonly ICurrentUserService? _currentUserService;
    private readonly IMemoryCache? _cache;
    private const string ExchangeRateCacheKey = "bcv_rate_today";
    // 8.7-L5: registro de claves de caché de producto emitidas para invalidación efectiva.
    private static readonly ConcurrentDictionary<string, byte> _productCacheKeys = new(StringComparer.OrdinalIgnoreCase);

    private static void RegisterProductCacheKey(string cacheKey)
    {
        if (!string.IsNullOrWhiteSpace(cacheKey))
        {
            _productCacheKeys.TryAdd(cacheKey, 0);
        }
    }

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
        // 8.7-L5: ahora SÍ invalida las claves de producto (SKU/quick) registradas, además de la
        // tasa BCV que históricamente era lo único que se removía aquí.
        // 8.16-H12: tras invalidar todas las claves registradas se limpia el diccionario de
        // seguimiento, evitando el crecimiento no acotado en procesos long-running. Las claves
        // ya no existen en el caché (Remove sobre claves ausentes es no-op), por lo que las
        // entradas huérfanas solo acarreaban memoria. El diccionario se repuebla con el próximo
        // uso de la caché.
        foreach (var cacheKey in _productCacheKeys.Keys)
        {
            _cache?.Remove(cacheKey);
        }
        _cache?.Remove(ExchangeRateCacheKey);
        _productCacheKeys.Clear();
    }

    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public async Task<Product?> GetProductBySkuAsync(string sku, bool useCache = true)
    {
        if (string.IsNullOrWhiteSpace(sku)) return null;
        var normalized = sku.Trim().ToUpperInvariant();

        if (!useCache || _cache == null)
        {
            return await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.SKU == normalized);
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
            var product = await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.SKU == normalized);
            if (product != null)
            {
                RegisterProductCacheKey(cacheKey);
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
            product.PriceRetailUSD = Core.Helpers.PricingCalculator.RoundPriceUp(rawPrice);
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
                product.PriceWholesaleUSD = Core.Helpers.PricingCalculator.RoundPriceUp(rawWholesalePrice);
            }

            if (product.PriceWholesaleUSD > product.PriceRetailUSD && product.PriceRetailUSD > 0)
            {
                throw new ArgumentException($"El precio al mayor (${product.PriceWholesaleUSD:F2}) no puede ser mayor al precio al detal (${product.PriceRetailUSD:F2}).");
            }

            if (product.ProfitMarginWholesale > product.ProfitMarginRetail && product.ProfitMarginRetail > 0)
            {
                throw new ArgumentException($"El margen al mayor ({product.ProfitMarginWholesale:F2}%) no puede ser mayor al margen al detal ({product.ProfitMarginRetail:F2}%).");
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
            throw new ArgumentException("El SKU/Código del producto debe contener entre 1 y 50 caracteres alfanuméricos (letras, dígitos, guiones o guiones bajos).");
        }
    }

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
        var dto = await FetchProductQuickInfoFromDbAsync(sku);
        if (dto != null)
        {
            RegisterProductCacheKey(cacheKey);
            _cache.Set(cacheKey, dto, CreateProductCacheOptions());
        }
        return dto;
    }

    private async Task<Core.DTOs.ProductQuickInfoDto?> FetchProductQuickInfoFromDbAsync(string sku)
    {
        var normalized = sku.Trim().ToUpperInvariant();
        return await _context.Products
            .AsNoTracking()
            .Where(p => p.SKU == normalized && !p.IsDeleted)
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

    public async Task DetachFromTransactionAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        if (!_context.Database.IsRelational())
        {
            return;
        }

        await _context.Database.UseTransactionAsync(null, cancellationToken);
    }
}
