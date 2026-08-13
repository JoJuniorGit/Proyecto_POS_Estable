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

    [Column(TypeName = "decimal(18,2)")]
    public decimal ExchangeRate { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? ReferenceNumber { get; set; }
}
