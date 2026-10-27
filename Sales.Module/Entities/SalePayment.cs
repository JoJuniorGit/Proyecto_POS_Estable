using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Sales.Module.Entities;

public class SalePayment
{
    public int Id { get; set; }

    [Required]
    public int SaleId { get; set; }
    public Sale? Sale { get; set; }

    [Required]
    public int PaymentMethodId { get; set; }
    public PaymentMethod? PaymentMethod { get; set; }

    [Required]
    public decimal Amount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountBsS { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal ExchangeRate { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? ReferenceNumber { get; set; }

    // 8.211-T3b1 (S6/S8): snapshot aditivo de la moneda del método al momento del cobro. Se
    // congela desde la moneda persistida (el nombre es solo fallback legacy), de modo que un
    // rename o reconfiguración posterior no reclasifique pagos históricos. No participa de
    // ningún cálculo de montos, tasas ni totales.
    [MaxLength(10)]
    public string? CurrencyCode { get; set; }
}
