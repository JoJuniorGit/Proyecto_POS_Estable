using System;
using System.Collections.Generic;
using Sales.Module.Entities;

namespace Sales.Module.DTOs;

/// <summary>
/// T3a (F2b, Paso 11/12, S3/S6): contrato del abono CxC. El monto se expresa en USD (la deuda
/// está denominada en USD 4dp) y la tasa la resuelve el servidor desde la fuente BCV cacheada
/// (nunca llega del cliente); la imputación es explícita por el orden de
/// <see cref="ReceivableIds"/> o FIFO de cartera por defecto.
/// </summary>
public class CreditPaymentRequest
{
    public int CustomerId { get; set; }

    /// <summary>Monto del abono en USD (mayor a cero); el polvo ≤ 0.005 USD se absorbe al saldar.</summary>
    public decimal AmountUSD { get; set; }

    public int PaymentMethodId { get; set; }

    public string? ReferenceNumber { get; set; }

    /// <summary>Imputación explícita por factura en el orden informado; nulo/vacío ⇒ FIFO de cartera.</summary>
    public List<int>? ReceivableIds { get; set; }

    /// <summary>Clave de idempotencia del cobro: el reintento con la misma clave replica el abono sin nuevos movimientos.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Receptor del cobro; el usuario autenticado (acting user) prevalece cuando está presente.</summary>
    public int? ReceivedByUserId { get; set; }
}

/// <summary>Imputación individual del abono contra una obligación (FIFO o factura explícita).</summary>
public class CreditPaymentAllocationResultDto
{
    public int ReceivableId { get; set; }
    public int SaleId { get; set; }
    public int? InvoiceNumber { get; set; }

    /// <summary>USD efectivo aplicado a la obligación (4dp).</summary>
    public decimal AmountUSD { get; set; }

    /// <summary>Contravalor en Bs.S a la tasa del abono (2dp).</summary>
    public decimal AmountBsS { get; set; }

    /// <summary>Diferencial cambiario realizado con signo (ganancia + / pérdida −) respecto de la tasa histórica.</summary>
    public decimal FxDifferenceBsS { get; set; }

    public decimal BalanceAfterUSD { get; set; }
    public ReceivableStatus Status { get; set; }
}

/// <summary>Resultado del cobro CxC: cabecera del abono con sus totales y las imputaciones realizadas.</summary>
public class CreditPaymentResultDto
{
    public int PaymentId { get; set; }
    public int CustomerId { get; set; }

    /// <summary>Suma de las imputaciones (USD 4dp). Es también el monto que ingresa a caja si el método es efectivo.</summary>
    public decimal AmountUSD { get; set; }

    /// <summary>Suma de los contravalores Bs.S a <see cref="PaymentRate"/> (2dp).</summary>
    public decimal AmountBsS { get; set; }

    /// <summary>Tasa BCV server-side congelada en el abono (snapshot inmutable).</summary>
    public decimal PaymentRate { get; set; }

    /// <summary>Diferencial cambiario realizado total con signo (Bs.S 2dp).</summary>
    public decimal FxDifferenceBsS { get; set; }

    public DateTime ReceivedAtUtc { get; set; }
    public int? ReceivedByUserId { get; set; }
    public int? CashTransactionId { get; set; }
    public int? PaymentMethodId { get; set; }
    public string? ReferenceNumber { get; set; }

    /// <summary>true cuando el cobro se sirvió desde la clave de idempotencia (replay sin nuevos movimientos).</summary>
    public bool IsReplay { get; set; }

    public List<CreditPaymentAllocationResultDto> Allocations { get; set; } = new();
}
