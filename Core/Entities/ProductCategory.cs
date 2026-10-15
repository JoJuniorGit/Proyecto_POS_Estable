namespace Core.Entities;

/// <summary>
/// 8.179-T1 (S1): junction N:M producto-categoría (molde <see cref="SupplierProductCode"/>).
/// El FK a Product es Cascade; el FK a Category es Restrict para no borrar categorías en uso.
/// </summary>
public class ProductCategory : BaseEntity
{
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
}
