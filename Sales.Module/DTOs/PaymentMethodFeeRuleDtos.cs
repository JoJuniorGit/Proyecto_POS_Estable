using Sales.Module.Entities;

namespace Sales.Module.DTOs;

/// <summary>
/// Paso 14 (F3-S1): regla de comisión de un medio de pago tal como la expone la API de
/// configuración. Los cobros históricos conservan su propio snapshot; esta vista es la
/// configuración vigente/administrable, no el detalle de un pago.
/// </summary>
public sealed record PaymentMethodFeeRuleDto
{
    public int Id { get; init; }

    public int PaymentMethodId { get; init; }

    public string PaymentMethodName { get; init; } = string.Empty;

    public PaymentMethodFeeType FeeType { get; init; }

    public decimal? Percentage { get; init; }

    public decimal? FixedAmount { get; init; }

    public string CurrencyCode { get; init; } = string.Empty;

    public DateTime EffectiveFromUtc { get; init; }

    public DateTime? EffectiveToUtc { get; init; }

    public bool IsActive { get; init; }

    public DateTime CreatedAtUtc { get; init; }

    public DateTime UpdatedAtUtc { get; init; }
}

/// <summary>
/// Paso 14 (F3-S1): alta de una regla de comisión. El método de pago solo se define al crear;
/// luego es inmutable para no reescribir la trazabilidad de los cobros ya configurados.
/// </summary>
public sealed class CreatePaymentMethodFeeRuleRequest
{
    public int PaymentMethodId { get; set; }

    public PaymentMethodFeeType FeeType { get; set; }

    public decimal? Percentage { get; set; }

    public decimal? FixedAmount { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;

    public DateTime EffectiveFromUtc { get; set; }

    public DateTime? EffectiveToUtc { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Paso 14 (F3-S1): actualización de una regla existente. <see cref="CreatePaymentMethodFeeRuleRequest.PaymentMethodId"/>
/// es intencionalmente inmutable: la regla pertenece al método con el que fue creada.
/// </summary>
public sealed class UpdatePaymentMethodFeeRuleRequest
{
    public PaymentMethodFeeType FeeType { get; set; }

    public decimal? Percentage { get; set; }

    public decimal? FixedAmount { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;

    public DateTime EffectiveFromUtc { get; set; }

    public DateTime? EffectiveToUtc { get; set; }

    public bool IsActive { get; set; } = true;
}
