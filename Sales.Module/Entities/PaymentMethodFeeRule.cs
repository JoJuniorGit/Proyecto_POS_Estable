using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Sales.Module.Entities;

/// <summary>
/// Paso 14 (S7): tipo de comision configurada para un metodo de pago.
/// </summary>
public enum PaymentMethodFeeType
{
    Percentage = 0,
    Fixed = 1,
    PercentagePlusFixed = 2
}

/// <summary>
/// Paso 14 (S7): regla de comision con vigencia para un metodo de pago. La comision es un
/// costo del negocio y nunca se carga al cliente. Es configuracion historica: cada cobro
/// congela el snapshot de la regla resuelta (SalePayment/ReceivablePayment) y no la re-lee.
/// </summary>
public class PaymentMethodFeeRule
{
    public int Id { get; set; }

    public int PaymentMethodId { get; set; }
    public PaymentMethod PaymentMethod { get; set; } = null!;

    public PaymentMethodFeeType FeeType { get; set; }

    [Column(TypeName = "decimal(9,4)")]
    public decimal? Percentage { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? FixedAmount { get; set; }

    [MaxLength(10)]
    public string CurrencyCode { get; set; } = "USD";

    public DateTime EffectiveFromUtc { get; set; }

    public DateTime? EffectiveToUtc { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public int? CreatedByUserId { get; set; }
    public int? UpdatedByUserId { get; set; }
}
