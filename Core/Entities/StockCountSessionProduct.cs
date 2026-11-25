namespace Core.Entities;

/// <summary>
/// WU1: producto seleccionado como alcance estricto de una sesión de conteo. La pareja
/// (sesión, producto) es la clave primaria y ambos FKs son Restrict: una sesión o un producto
/// referenciado no se borran en cascada.
/// </summary>
public class StockCountSessionProduct
{
    public int StockCountSessionId { get; set; }
    public StockCountSession StockCountSession { get; set; } = null!;

    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
}
