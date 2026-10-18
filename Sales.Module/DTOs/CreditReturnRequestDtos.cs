using System;
using System.Collections.Generic;
using Sales.Module.Entities;

namespace Sales.Module.DTOs;

/// <summary>
/// T3b2 (F2b, Paso 11/12, S5/S7): contrato de devolución total o parcial de una venta a crédito.
/// Las líneas referencian ítems de la venta con la cantidad devuelta (acumulada por ítem nunca
/// excede la cantidad vendida) y la clave de idempotencia replica la devolución sin nuevos
/// movimientos. El valor se calcula server-side sobre la base EFECTIVA; el contrato no expone
/// montos ni tasas.
/// </summary>
public class CreditReturnRequest
{
    public List<CreditReturnItemRequest> Items { get; set; } = new();

    /// <summary>Motivo de la devolución (máximo 500 caracteres).</summary>
    public string? Reason { get; set; }

    /// <summary>Clave de idempotencia de la devolución: el reintento replica sin nuevas salidas.</summary>
    public string? IdempotencyKey { get; set; }
}

/// <summary>Línea solicitada de la devolución: ítem de la venta y cantidad devuelta (mayor a cero).</summary>
public class CreditReturnItemRequest
{
    public int SaleItemId { get; set; }
    public decimal Quantity { get; set; }
}

/// <summary>Resultado de la devolución: totales, reembolso del overpago y saldo resultante.</summary>
public class CreditReturnResultDto
{
    public int ReturnId { get; set; }
    public int SaleId { get; set; }
    public int ReceivableId { get; set; }
    public int CustomerId { get; set; }

    /// <summary>Valor USD 4dp de las líneas devueltas (base efectiva / tasa de emisión).</summary>
    public decimal ReturnedUSD { get; set; }

    /// <summary>Excedente efectivamente pagado reembolsado por caja (USD 4dp); 0 sin overpago.</summary>
    public decimal RefundedUSD { get; set; }

    /// <summary>Contravalor en Bs.S del reembolso a <see cref="RefundRate"/> (2dp); 0 sin overpago.</summary>
    public decimal RefundedBsS { get; set; }

    /// <summary>Tasa congelada del último abono usada para el reembolso; null sin overpago.</summary>
    public decimal? RefundRate { get; set; }

    public int? CashTransactionId { get; set; }

    /// <summary>Saldo USD 4dp de la obligación después de la devolución.</summary>
    public decimal BalanceAfterUSD { get; set; }

    public ReceivableStatus Status { get; set; }

    /// <summary>Estado de la venta tras la devolución (Cancelled solo en devolución total sin abonos).</summary>
    public SaleStatus SaleStatus { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public string? Reason { get; set; }

    /// <summary>true cuando la devolución se sirvió desde la clave de idempotencia (replay sin efectos nuevos).</summary>
    public bool IsReplay { get; set; }

    public List<CreditReturnItemResultDto> Items { get; set; } = new();
}

/// <summary>Línea persistida de la devolución con su valor USD prorrateado.</summary>
public class CreditReturnItemResultDto
{
    public int SaleItemId { get; set; }
    public decimal Quantity { get; set; }
    public decimal AmountUSD { get; set; }
}

/// <summary>
/// T3b2 (F2b, S7): solicitud de anulación de una venta a crédito SIN abonos. La anulación simple
/// solo procede cuando el saldo vigente es igual al original (cero movimientos de cartera).
/// </summary>
public class CreditCancelRequest
{
    /// <summary>Motivo de la anulación (máximo 500 caracteres).</summary>
    public string? Reason { get; set; }
}

/// <summary>Resultado de la anulación sin abonos: obligación anulada, cupo restituido y venta cancelada.</summary>
public class CreditCancelResultDto
{
    public int SaleId { get; set; }
    public int ReceivableId { get; set; }
    public int CustomerId { get; set; }

    /// <summary>Monto original de la obligación anulada (USD 4dp).</summary>
    public decimal CancelledOriginalUSD { get; set; }

    public ReceivableStatus Status { get; set; }
    public SaleStatus SaleStatus { get; set; }
    public DateTime CancelledAtUtc { get; set; }
    public string? Reason { get; set; }
}
