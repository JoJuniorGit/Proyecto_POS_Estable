namespace Core.Entities;

/// <summary>
/// 8.166: factor de conversión caja→unidad recordado por proveedor+producto. Se aprende al
/// confirmar una factura y se prefija en las líneas nuevas que resuelven al mismo producto; el
/// revisor puede editarlo en cualquier momento (cambio de facturación del proveedor).
/// </summary>
public class SupplierProductFactor : BaseEntity
{
    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public decimal ConversionFactor { get; set; } = 1m;
}
