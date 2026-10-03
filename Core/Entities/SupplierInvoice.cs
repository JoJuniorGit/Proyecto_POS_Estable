using System;
using System.Collections.Generic;

namespace Core.Entities;

public class SupplierInvoice : BaseEntity
{
    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public SupplierInvoiceStatus Status { get; set; } = SupplierInvoiceStatus.Draft;
    public DateTime? AppliedAt { get; set; }
    public ICollection<SupplierInvoiceLine> Lines { get; set; } = new List<SupplierInvoiceLine>();
}
