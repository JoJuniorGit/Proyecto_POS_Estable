using System.Collections.Generic;

namespace Core.Entities;

public class Supplier : BaseEntity
{
    public string? RifOrNit { get; set; }
    public string? NormalizedRifOrNit { get; set; }
    public string CommercialName { get; set; } = string.Empty;
    public string NormalizedCommercialName { get; set; } = string.Empty;

    /// <summary>
    /// 8.167: moneda de facturación recordada del proveedor ("USD"/"Bs.S"); null = sin preferencia.
    /// Se actualiza al confirmar una factura y se prefija al seleccionar el proveedor (editable).
    /// </summary>
    public string? InvoiceCurrency { get; set; }

    public SupplierColumnMapping? ColumnMapping { get; set; }
    public ICollection<SupplierProductCode> ProductCodes { get; set; } = new List<SupplierProductCode>();
    public ICollection<SupplierInvoice> Invoices { get; set; } = new List<SupplierInvoice>();
}
