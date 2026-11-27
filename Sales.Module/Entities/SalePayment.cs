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

    // 8.211-T3c2 (S6): importe original en la moneda del medio de pago identificada por
    // CurrencyCode. Es aditivo: Amount (USD) y AmountBsS siguen siendo la fuente de cálculo.
    [Column(TypeName = "decimal(18,4)")]
    public decimal? OriginalAmount { get; set; }

    // Paso 14 (S7): snapshot inmutable de la regla de comision resuelta al cobrar. La comision
    // es un costo del negocio (nunca se carga al cliente). FeeConfigured distingue "sin regla
    // activa" (montos 0) de una comision calculada en cero; jamas se re-resuelve la regla.
    public int? PaymentMethodFeeRuleId { get; set; }
    public PaymentMethodFeeRule? PaymentMethodFeeRule { get; set; }

    public PaymentMethodFeeType? FeeType { get; set; }

    [Column(TypeName = "decimal(9,4)")]
    public decimal? FeePercentage { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? FeeFixedAmount { get; set; }

    [MaxLength(10)]
    public string? FeeCurrencyCode { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal FeeAmountUsd { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal FeeAmountLocal { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal FeeRate { get; set; }

    public bool FeeConfigured { get; set; }

    public DateTime? FeeRuleEffectiveFromUtc { get; set; }
}
