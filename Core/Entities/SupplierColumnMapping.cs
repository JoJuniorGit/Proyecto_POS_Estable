namespace Core.Entities;

public class SupplierColumnMapping : BaseEntity
{
    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public string? BarcodeColumnName { get; set; }
    public string? SupplierCodeColumnName { get; set; }
    public string NameColumnName { get; set; } = string.Empty;
    public string QuantityColumnName { get; set; } = string.Empty;
    public string UnitCostColumnName { get; set; } = string.Empty;
}
