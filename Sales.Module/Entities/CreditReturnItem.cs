using System.ComponentModel.DataAnnotations.Schema;

namespace Sales.Module.Entities;

/// <summary>
/// Línea de una devolución CxC (Paso 11/12, S5/S7): cantidad devuelta (18,3) y valor USD 4dp
/// prorrateado desde la base EFECTIVA de la línea de venta. La suma acumulada por
/// <see cref="SaleItemId"/> nunca excede la cantidad vendida.
/// </summary>
public class CreditReturnItem
{
    public int Id { get; set; }

    public int CreditReturnId { get; set; }
    public CreditReturn CreditReturn { get; set; } = null!;

    public int SaleItemId { get; set; }
    public SaleItem SaleItem { get; set; } = null!;

    [Column(TypeName = "decimal(18,3)")]
    public decimal Quantity { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal AmountUSD { get; set; }
}
