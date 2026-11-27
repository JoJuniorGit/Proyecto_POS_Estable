using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Core.Entities;

namespace Sales.Module.Entities;

/// <summary>
/// Cabecera del cobro CxC (Paso 11/12, S5/S6): un abono puede imputarse a una o varias
/// obligaciones via <see cref="CreditPaymentAllocation"/>. La tasa del dia se congela en
/// <see cref="PaymentRate"/> (snapshot inmutable: los reintentos no re-leen la tasa) y el
/// diferencial cambiario realizado con signo se desglosa por asignacion.
/// </summary>
public class ReceivablePayment
{
    public int Id { get; set; }

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal AmountUSD { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountBsS { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal PaymentRate { get; set; }

    // Signed: ganancia (+) o perdida (-) por variacion de tasa entre emision y cobro.
    [Column(TypeName = "decimal(18,2)")]
    public decimal FxDifferenceBsS { get; set; }

    public int? CashTransactionId { get; set; }
    public CashTransaction? CashTransaction { get; set; }

    public int? PaymentMethodId { get; set; }
    public PaymentMethod? PaymentMethod { get; set; }

    [MaxLength(100)]
    public string? ReferenceNumber { get; set; }

    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;

    // Clave de idempotencia del cobro: unica cuando existe (NULL multiple permitido).
    [MaxLength(100)]
    public string? IdempotencyKey { get; set; }

    public int? ReceivedByUserId { get; set; }
    public User? ReceivedByUser { get; set; }

    // Paso 14 (S7): snapshot inmutable de la regla de comision resuelta al recibir el abono.
    // La comision es un costo del negocio (nunca se carga al cliente). FeeConfigured distingue
    // "sin regla activa" (montos 0) de una comision calculada en cero.
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

    public List<CreditPaymentAllocation> Allocations { get; set; } = new();
}
