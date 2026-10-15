using Core.DTOs;

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

    /// <summary>
    /// 8.147-S6/D10: confianza (0–100, 1 decimal) del nombre extraído por OCR; null en filas tabulares.
    /// </summary>
    public decimal? OcrNameConfidence { get; set; }

    /// <summary>
    /// 8.147-S6/D10: confianza (0–100, 1 decimal) de la cantidad extraída por OCR; null en filas tabulares.
    /// </summary>
    public decimal? OcrQuantityConfidence { get; set; }

    /// <summary>
    /// 8.147-S6/D10: confianza (0–100, 1 decimal) del costo unitario extraído por OCR; null en filas tabulares.
    /// </summary>
    public decimal? OcrUnitCostConfidence { get; set; }

    /// <summary>
    /// 8.165: factor de conversión caja→unidad (divisor). La factura puede venir por caja/bulto y
    /// el local vender por unidad: al confirmar, el costo unitario aplicado se divide por el
    /// factor y la cantidad que entra a stock se multiplica por él. Default 1 (sin conversión).
    /// </summary>
    public decimal ConversionFactor { get; set; } = 1m;

    /// <summary>
    /// 8.184-T2 (S5): regla de redondeo elegida al resolver la línea contra un producto (misma
    /// semántica que los overrides de margen). null = hereda la regla de la factura al confirmar
    /// (precedencia línea ?? factura, 8.184-S7); el re-vínculo reemplaza el valor previo.
    /// </summary>
    public RoundingRuleType? RoundingRule { get; set; }
}
