namespace Core.Entities;

public class SupplierInvoiceLine : BaseEntity
{
    public int SupplierInvoiceId { get; set; }
    public SupplierInvoice SupplierInvoice { get; set; } = null!;
    public string? SupplierCode { get; set; }
    public string? Barcode { get; set; }
    public string? Name { get; set; }
    public decimal Quantity { get; set; }

    /// <summary>
    /// Costo unitario tal como lo emitió el proveedor en la moneda del documento (8.146-S2, auditoría).
    /// </summary>
    public decimal UnitCostDocument { get; set; }

    public decimal UnitCostUSD { get; set; }
    public SupplierInvoiceLineStatus Status { get; set; } = SupplierInvoiceLineStatus.New;
    public int? ResolvedProductId { get; set; }
    public Product? ResolvedProduct { get; set; }
    public decimal? OldCostPriceUSD { get; set; }
    public decimal? OldProfitMarginRetail { get; set; }
    public decimal? OldProfitMarginWholesale { get; set; }
    public decimal? OldStockQuantity { get; set; }
    public decimal? MarginRetailOverride { get; set; }
    public decimal? MarginWholesaleOverride { get; set; }
    public decimal? SuggestedRetailPriceUSD { get; set; }
    public decimal? SuggestedWholesalePriceUSD { get; set; }
    public bool IsApproved { get; set; } = true;
    public MatchMethod MatchMethod { get; set; } = MatchMethod.None;
}
