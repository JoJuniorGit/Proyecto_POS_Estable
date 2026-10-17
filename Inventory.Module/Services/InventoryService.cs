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
    // 8.185-T2 (S1/S2): lector de la política global de efectivo para resolver las quick-info.
    // Opcional para no romper composiciones de tests que construyen el servicio sin settings.
    private readonly ISystemSettingsService? _settingsService;
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

    public InventoryService(InventoryDbContext context, ICurrentUserService? currentUserService = null, IMemoryCache? cache = null, ISystemSettingsService? settingsService = null)
    {
        _context = context;
        _currentUserService = currentUserService;
        _cache = cache;
        _settingsService = settingsService;
    }

    private void EnsureCatalogMutationPermission()
    {
        if (_currentUserService != null && !_currentUserService.CanMutateCatalog)
        {
            throw new UnauthorizedAccessException("El usuario actual no tiene permisos para modificar el catálogo ni realizar importaciones.");
        }
    }

    private static IQueryable<Core.DTOs.SaleProductInfoDto> ProjectSaleProduct(IQueryable<Product> query) =>
        query.Select(p => new Core.DTOs.SaleProductInfoDto
        {
            Id = p.Id,
            Name = p.Name,
            IsDeleted = p.IsDeleted,
            IsActive = p.IsActive,
            IsCashAdvance = p.IsCashAdvance,
            PriceUSD = p.PriceUSD,
            PriceBsS = p.PriceBsS,
            PriceRetailUSD = p.PriceRetailUSD,
            PriceWholesaleUSD = p.PriceWholesaleUSD,
            MinWholesaleQuantity = p.MinWholesaleQuantity,
            HasWholesale = p.HasWholesale,
            IsGroupHeader = p.IsGroupHeader,
            IsFractional = p.IsFractional,
            UnitOfMeasure = p.UnitOfMeasure,
            CostPriceUSD = (decimal?)p.CostPriceUSD,
            // 8.185-T2 (S5): override crudo; SalesService resuelve la regla efectiva al agregar.
            RoundingRuleCashBs = p.RoundingRuleCashBs
        });

    public async Task<IReadOnlyList<Core.DTOs.SaleProductInfoDto>> GetSaleProductsByIdsAsync(IEnumerable<int> productIds, System.Threading.CancellationToken cancellationToken = default)
    {
        var idList = productIds.Distinct().ToList();
        if (idList.Count == 0) return Array.Empty<Core.DTOs.SaleProductInfoDto>();
        return await ProjectSaleProduct(_context.Products.AsNoTracking().Where(p => idList.Contains(p.Id))).ToListAsync(cancellationToken);
    }

    public async Task<Core.DTOs.SaleProductInfoDto?> GetSaleProductByIdAsync(int id, System.Threading.CancellationToken cancellationToken = default)
    {
        return await ProjectSaleProduct(_context.Products.AsNoTracking().Where(p => p.Id == id)).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Core.DTOs.SaleProductInfoDto?> GetCashAdvanceProductAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        return await ProjectSaleProduct(_context.Products.AsNoTracking().Where(p => p.IsCashAdvance && !p.IsDeleted && p.IsActive)).FirstOrDefaultAsync(cancellationToken);
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

    public async Task<Core.DTOs.ProductQuickInfoDto?> GetProductQuickInfoAsync(string sku, bool useCache = true, System.Threading.CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sku)) return null;
        var normalized = sku.Trim().ToUpperInvariant();

        if (!useCache || _cache == null)
        {
            return await FetchProductQuickInfoFromDbAsync(sku, cancellationToken);
        }

        var cacheKey = $"product_quick_{normalized}";
        if (_cache.TryGetValue(cacheKey, out Core.DTOs.ProductQuickInfoDto? cachedDto) && cachedDto != null)
        {
            Core.Metrics.CacheMetrics.RecordHit();
            return cachedDto;
        }

        Core.Metrics.CacheMetrics.RecordMiss();
        var dto = await FetchProductQuickInfoFromDbAsync(sku, cancellationToken);
        if (dto != null)
        {
            RegisterProductCacheKey(cacheKey);
            _cache.Set(cacheKey, dto, CreateProductCacheOptions());
        }
        return dto;
    }

    /// <summary>
    /// 8.185-T2 (S1/S2): regla de efectivo por defecto (Pricing.DefaultCashBsRoundingRule) leída
    /// con el contrato del lector 8.184 (Enum.TryParse + IsDefined; clave ausente/ilegible ⇒
    /// ExactMath). Sin accessor inyectado ⇒ ExactMath, sin romper composiciones de tests.
    /// </summary>
    private async Task<Core.DTOs.RoundingRuleType> ResolveDefaultCashRoundingRuleAsync()
    {
        if (_settingsService == null)
        {
            return Core.DTOs.RoundingRuleType.ExactMath;
        }

        var value = await _settingsService.GetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultCashBsRoundingRule);
        return Enum.TryParse<Core.DTOs.RoundingRuleType>(value, ignoreCase: true, out var rule) && Enum.IsDefined(rule)
            ? rule
            : Core.DTOs.RoundingRuleType.ExactMath;
    }

    /// <summary>
    /// 8.186-T1 (S1/S6): toggle global de piso (Pricing.DefaultEnforceMinimumMarginFloor) con el
    /// contrato 8.185-T3 (default true ante ausencia o valor ilegible); sin accessor ⇒ true.
    /// </summary>
    private async Task<bool> ResolveNeverRoundDownAsync()
    {
        if (_settingsService == null)
        {
            return true;
        }

        var value = await _settingsService.GetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor);
        return !bool.TryParse(value, out var enforceFloor) || enforceFloor;
    }

    private static bool IsCashFriendlyRule(Core.DTOs.RoundingRuleType? rule) =>
        rule is Core.DTOs.RoundingRuleType.VesCashFriendlyInteger
            or Core.DTOs.RoundingRuleType.VesCashFriendlyStep5
            or Core.DTOs.RoundingRuleType.VesCashFriendlyStep10;

    // Base USD del canal Detal: espejo del precio canónico persistido (Product.PriceBsS) y de la
    // venta (retailUsd en SalesService): PriceRetailUSD con fallback a PriceUSD.
    private static decimal ResolveRetailUsd(Core.DTOs.ProductDto item) =>
        item.PriceRetailUSD > 0m ? item.PriceRetailUSD : item.PriceUSD;

    private static decimal ResolveRetailUsd(Core.DTOs.ProductQuickInfoDto item) =>
        item.PriceRetailUSD > 0m ? item.PriceRetailUSD : item.PriceUSD;

    /// <summary>
    /// 8.186-T1 (S1/S3): proyecta EffectivePriceBsS/EffectiveWholesalePriceBsS sobre una página de
    /// catálogo ya materializada. La tasa se lee UNA vez por request y el toggle solo si algún ítem
    /// resuelve a regla cash (si no, la proyección cae al canónico y el toggle no interviene).
    /// El Mayor queda en 0 cuando !HasWholesale o PriceWholesaleUSD ≤ 0.
    /// </summary>
    private async Task ProjectEffectiveBsSBatchAsync(
        System.Collections.Generic.IReadOnlyList<Core.DTOs.ProductDto> items,
        Core.DTOs.RoundingRuleType defaultCashRule,
        System.Threading.CancellationToken token)
    {
        if (items.Count == 0)
        {
            return;
        }

        decimal rate = await GetTodayExchangeRateAsync(token);
        bool neverRoundDown = true;
        foreach (var item in items)
        {
            if (IsCashFriendlyRule(item.RoundingRuleCashBs ?? defaultCashRule))
            {
                neverRoundDown = await ResolveNeverRoundDownAsync();
                break;
            }
        }

        foreach (var item in items)
        {
            var rule = item.RoundingRuleCashBs ?? defaultCashRule;
            item.EffectivePriceBsS = PricingRoundingPolicyService.ProjectEffectiveBsS(ResolveRetailUsd(item), rate, rule, neverRoundDown);
            item.EffectiveWholesalePriceBsS = item.HasWholesale && item.PriceWholesaleUSD > 0m
                ? PricingRoundingPolicyService.ProjectEffectiveBsS(item.PriceWholesaleUSD, rate, rule, neverRoundDown)
                : 0m;
        }
    }

    /// <summary>
    /// 8.186-T1 (S1/S3): misma proyección para las superficies de quick-info/sugerencias (la regla
    /// ya viaja RESUELTA en el DTO). Tasa y toggle se leen UNA vez por request, no por ítem.
    /// </summary>
    private async Task ProjectEffectiveBsSBatchAsync(
        System.Collections.Generic.IReadOnlyList<Core.DTOs.ProductQuickInfoDto> items,
        Core.DTOs.RoundingRuleType defaultCashRule,
        System.Threading.CancellationToken token)
    {
        if (items.Count == 0)
        {
            return;
        }

        decimal rate = await GetTodayExchangeRateAsync(token);
        bool neverRoundDown = true;
        foreach (var item in items)
        {
            if (IsCashFriendlyRule(item.RoundingRuleCashBs ?? defaultCashRule))
            {
                neverRoundDown = await ResolveNeverRoundDownAsync();
                break;
            }
        }

        foreach (var item in items)
        {
            var rule = item.RoundingRuleCashBs ?? defaultCashRule;
            item.EffectivePriceBsS = PricingRoundingPolicyService.ProjectEffectiveBsS(ResolveRetailUsd(item), rate, rule, neverRoundDown);
            item.EffectiveWholesalePriceBsS = item.HasWholesale && item.PriceWholesaleUSD > 0m
                ? PricingRoundingPolicyService.ProjectEffectiveBsS(item.PriceWholesaleUSD, rate, rule, neverRoundDown)
                : 0m;
        }
    }

    private async Task<Core.DTOs.ProductQuickInfoDto?> FetchProductQuickInfoFromDbAsync(string sku, System.Threading.CancellationToken cancellationToken = default)
    {
        var normalized = sku.Trim().ToUpperInvariant();
        var defaultCashRule = await ResolveDefaultCashRoundingRuleAsync();
        var dto = await _context.Products
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
                    : p.StockQuantity,
                // 8.186-T1 (S1): el canal Mayor necesita la bandera real para gatear el efectivo.
                HasWholesale = p.HasWholesale,
                // 8.185-T2 (S1/S2): regla resuelta en memoria (override ?? default global).
                RoundingRuleCashBs = p.RoundingRuleCashBs
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (dto != null)
        {
            dto.RoundingRuleCashBs ??= defaultCashRule;
            // 8.186-T1 (S1/S3): efectivo Bs.S proyectado (tasa/toggle una vez; cache 15s/5min del DTO
            // mantiene la proyección vigente según las mismas reglas de refresco que el resto).
            await ProjectEffectiveBsSBatchAsync(new[] { dto }, defaultCashRule, cancellationToken);
        }

        return dto;
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
