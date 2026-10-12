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

    private readonly InventoryDbContext _context;
    private readonly ISystemSettingsService _systemSettingsService;
    private readonly ISupplierProductSimilaritySearch _similaritySearch;
    private readonly ICurrentUserService? _currentUserService;
    private readonly IMemoryCache? _memoryCache;
    private readonly ILogger<SupplierInvoiceService> _logger;

    public SupplierInvoiceService(
        InventoryDbContext context,
        ISystemSettingsService systemSettingsService,
        ISupplierProductSimilaritySearch similaritySearch,
        ICurrentUserService currentUserService,
        IProductManagementService productManagementService,
        IMemoryCache? memoryCache = null,
        ILogger<SupplierInvoiceService>? logger = null)
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
        line.ConversionFactor);
}
