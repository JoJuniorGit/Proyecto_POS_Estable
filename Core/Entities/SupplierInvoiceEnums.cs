namespace Core.Entities;

public enum SupplierInvoiceLineStatus
{
    New,
    Update,
    Unchanged,
    Conflict
}

public enum MatchMethod
{
    Barcode = 1,
    SupplierCode = 2,
    Fuzzy = 3,
    None = 0
}

public enum SupplierInvoiceStatus
{
    Draft,
    Applied,
    Failed
}
