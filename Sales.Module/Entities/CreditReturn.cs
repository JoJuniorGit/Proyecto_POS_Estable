using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Core.Entities;

namespace Sales.Module.Entities;

/// <summary>
/// Devolución de una venta a crédito (Paso 11/12, S5/S7). Registra el valor devuelto sobre la
/// base EFECTIVA por línea y, cuando la devolución supera el saldo pendiente (overpago), el
/// excedente efectivamente pagado se reembolsa por caja: <see cref="RefundedUSD"/> en USD y
/// <see cref="RefundedBsS"/> al contravalor de <see cref="RefundRate"/> (tasa congelada del
/// último abono, snapshot inmutable). La clave de idempotencia es única cuando existe para que
/// el reintento replique la devolución sin nuevos movimientos.
/// </summary>
public class CreditReturn
{
    public int Id { get; set; }

    public int SaleId { get; set; }
    public Sale Sale { get; set; } = null!;

    public int ReceivableId { get; set; }
    public Receivable Receivable { get; set; } = null!;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>Valor USD 4dp de las líneas devueltas (base efectiva Bs.S / tasa de emisión).</summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal ReturnedUSD { get; set; }

    /// <summary>Excedente efectivamente pagado que se reembolsó por caja (USD 4dp); 0 sin overpago.</summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal RefundedUSD { get; set; }

    /// <summary>Contravalor del reembolso en Bs.S a <see cref="RefundRate"/> (2dp); 0 sin overpago.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal RefundedBsS { get; set; }

    /// <summary>Tasa congelada del último abono usada para el reembolso; null sin overpago.</summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal? RefundRate { get; set; }

    public int? CashTransactionId { get; set; }
    public CashTransaction? CashTransaction { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(500)]
    public string? Reason { get; set; }

    public int? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    // Clave de idempotencia de la devolución: unica cuando existe (NULL multiple permitido).
    [MaxLength(100)]
    public string? IdempotencyKey { get; set; }

    public List<CreditReturnItem> Items { get; set; } = new();
}
