using System;
using System.Collections.Generic;
using Core.Common;

namespace Core.Entities;

public class SupplierInvoice : BaseEntity
{
    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public SupplierInvoiceStatus Status { get; set; } = SupplierInvoiceStatus.Draft;

    /// <summary>
    /// Moneda de emisión de la factura ("USD" o "Bs.S"); snapshot inmutable por documento (8.146-S1).
    /// </summary>
    public string Currency { get; set; } = CurrencyCodes.Usd;

    /// <summary>
    /// Tasa de cambio aplicada al documento; snapshot inmutable para normalizar costos a USD (8.146-S2).
    /// </summary>
    public decimal AppliedRate { get; set; } = 1m;

    public DateTime? AppliedAt { get; set; }
    public ICollection<SupplierInvoiceLine> Lines { get; set; } = new List<SupplierInvoiceLine>();
}
