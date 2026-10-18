using System.ComponentModel.DataAnnotations.Schema;

namespace Sales.Module.Entities;

/// <summary>
/// Imputacion de un abono contra una obligacion concreta (FIFO o factura explicita, S3).
/// El diferencial cambiario realizado se registra por asignacion (S6) porque cada
/// obligacion tiene su propia tasa historica de emision.
/// </summary>
public class CreditPaymentAllocation
{
    public int Id { get; set; }

    public int ReceivablePaymentId { get; set; }
    public ReceivablePayment ReceivablePayment { get; set; } = null!;

    public int ReceivableId { get; set; }
    public Receivable Receivable { get; set; } = null!;

    [Column(TypeName = "decimal(18,4)")]
    public decimal AmountUSD { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountBsS { get; set; }

    // Signed: espejo del diferencial realizado de la cabecera aplicado a esta obligacion.
    [Column(TypeName = "decimal(18,2)")]
    public decimal FxDifferenceBsS { get; set; }
}
