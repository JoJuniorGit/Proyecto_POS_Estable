using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inventory.Module.Services;

public partial class SupplierInvoiceService : ISupplierInvoiceService
{
    private const decimal MaxInvoiceNumericValue = 1_000_000_000m;
    private const decimal MaxMarginOverrideValue = 100_000m;

    // 8.203: el confirm no puede aceptar una tasa arbitraria del cliente. Se tolera una
    // corrección menor (redondeos/diferencia del día) y se rechaza cualquier desvío material.
    private const decimal MaxConfirmRateDeviation = 0.10m;

    private readonly InventoryDbContext _context;
    private readonly ISystemSettingsService _systemSettingsService;
    private readonly ISupplierProductSimilaritySearch _similaritySearch;
    private readonly ICurrentUserService? _currentUserService;
    private readonly IMemoryCache? _memoryCache;
    private readonly ILogger<SupplierInvoiceService> _logger;
    private readonly ITodayExchangeRateProvider? _rateProvider;
    // 8.211-T3d (F3d): active monetary profile and manual pair quotes. Null keeps the historical
    // USD/Bs.S behavior for direct legacy compositions and unit tests.
    private readonly IMonetaryProfileService? _monetaryProfileService;
    private readonly ICurrencyRateService? _currencyRateService;

    public SupplierInvoiceService(
        InventoryDbContext context,
        ISystemSettingsService systemSettingsService,
        ISupplierProductSimilaritySearch similaritySearch,
        ICurrentUserService currentUserService,
        IProductManagementService productManagementService,
        IMemoryCache? memoryCache = null,
        ILogger<SupplierInvoiceService>? logger = null,
        ITodayExchangeRateProvider? rateProvider = null,
        IMonetaryProfileService? monetaryProfileService = null,
        ICurrencyRateService? currencyRateService = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(systemSettingsService);
        ArgumentNullException.ThrowIfNull(similaritySearch);
        ArgumentNullException.ThrowIfNull(currentUserService);
        ArgumentNullException.ThrowIfNull(productManagementService);

        _context = context;
        _systemSettingsService = systemSettingsService;
        _similaritySearch = similaritySearch;
        _currentUserService = currentUserService;
        _productManagementService = productManagementService;
        _memoryCache = memoryCache;
        _logger = logger ?? NullLogger<SupplierInvoiceService>.Instance;
        _rateProvider = rateProvider;
        _monetaryProfileService = monetaryProfileService;
        _currencyRateService = currencyRateService;
    }

    private void InvalidateProductCache(IEnumerable<Product> products)
    {
        if (_memoryCache is null)
        {
            return;
        }

        foreach (var product in products)
        {
            if (string.IsNullOrWhiteSpace(product.SKU))
            {
                continue;
            }

            var sku = product.SKU.Trim();
            var upperSku = sku.ToUpperInvariant();
            _memoryCache.Remove($"product_quick_{sku}");
            _memoryCache.Remove($"product_quick_{upperSku}");
            _memoryCache.Remove($"product_sku_{upperSku}");
        }
    }

    private static SupplierInvoiceDetailDto ToDetailDto(SupplierInvoice invoice) => new(
        invoice.Id,
        invoice.SupplierId,
        invoice.Status.ToString(),
        invoice.Currency,
        invoice.AppliedRate,
        invoice.Lines.Select(ToLineDto).ToArray());

    private static SupplierInvoiceLineDto ToLineDto(SupplierInvoiceLine line) => new(
        line.Id,
        line.SupplierCode,
        line.Barcode,
        line.Name,
        line.Quantity,
        line.UnitCostDocument,
        line.UnitCostUSD,
        line.Status.ToString(),
        line.ResolvedProductId,
        line.OldCostPriceUSD,
        line.OldProfitMarginRetail,
        line.OldProfitMarginWholesale,
        line.OldStockQuantity,
        line.MarginRetailOverride,
        line.MarginWholesaleOverride,
        line.SuggestedRetailPriceUSD,
        line.SuggestedWholesalePriceUSD,
        line.IsApproved,
        line.MatchMethod.ToString(),
        line.OcrNameConfidence,
        line.OcrQuantityConfidence,
        line.OcrUnitCostConfidence,
        line.ConversionFactor,
        // 8.184-T2 (S5): regla per-línea persistida (null = hereda la regla de la factura); la UI
        // repobla el selector con este valor al recargar el borrador.
        line.RoundingRule);
}
