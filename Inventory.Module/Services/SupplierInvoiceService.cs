using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.Services;

public partial class SupplierInvoiceService : ISupplierInvoiceService
{
    private readonly InventoryDbContext _context;
    private readonly ISystemSettingsService _systemSettingsService;
    private readonly ISupplierProductSimilaritySearch _similaritySearch;

    public SupplierInvoiceService(
        InventoryDbContext context,
        ISystemSettingsService systemSettingsService,
        ISupplierProductSimilaritySearch similaritySearch)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(systemSettingsService);
        ArgumentNullException.ThrowIfNull(similaritySearch);

        _context = context;
        _systemSettingsService = systemSettingsService;
        _similaritySearch = similaritySearch;
    }

    private static SupplierInvoiceDetailDto ToDetailDto(SupplierInvoice invoice) => new(
        invoice.Id,
        invoice.SupplierId,
        invoice.Status.ToString(),
        invoice.Lines.Select(ToLineDto).ToArray());

    private static SupplierInvoiceLineDto ToLineDto(SupplierInvoiceLine line) => new(
        line.Id,
        line.SupplierCode,
        line.Barcode,
        line.Name,
        line.Quantity,
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
        line.MatchMethod.ToString());
}
