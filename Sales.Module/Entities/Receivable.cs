using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using Core.Entities;

namespace Sales.Module.Entities;

/// <summary>
/// Obligacion de deuda CxC (Paso 11/12, S5/S6): una por venta a credito. La deuda se
/// denomina en USD 4dp derivado del Bs.S efectivamente financiado (USD efectivo de
/// 8.185/8.186, no canonico) y el Bs.S es proyeccion a la tasa vigente.
/// Snapshot inmutable de emision: <see cref="HistoricalRate"/> congela la tasa de la venta
/// y <see cref="OriginalBsS"/> el contravalor financiado; el principal nunca se revalua.
/// </summary>
public class Receivable
{
    public int Id { get; set; }

    // 1:1 con la venta: una factura a credito genera a lo sumo una obligacion.
    public int SaleId { get; set; }
    public Sale Sale { get; set; } = null!;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    [Column(TypeName = "decimal(18,4)")]
    public decimal OriginalUSD { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal BalanceUSD { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal OriginalBsS { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal HistoricalRate { get; set; }

    public DateTime IssuedAtUtc { get; set; }

    public DateTime DueDateUtc { get; set; }

    public ReceivableStatus Status { get; set; } = ReceivableStatus.Vigente;

    public List<CreditPaymentAllocation> PaymentAllocations { get; set; } = new();
}
