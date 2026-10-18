using System;
using System.Collections.Generic;
using System.Linq;
using Sales.Module.Entities;

namespace Sales.Module.Receipts;

/// <summary>
/// T3b1 (F2b, S5): contexto del comprobante de venta a crédito (documento de entrega). Reúne el
/// snapshot de la factura emitida (ítems, totales y abono inicial si existió) con la obligación
/// CxC constituida (monto financiado en USD y su contravalor Bs.S a la tasa de emisión, plazo y
/// vencimiento). Sin persistencia: se arma on-demand desde la venta y su Receivable.
/// </summary>
public sealed class CreditSaleReceiptContext
{
    public int SaleId { get; init; }
    public int? InvoiceNumber { get; init; }
    public DateTime Date { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerCedula { get; init; }

    /// <summary>Total canónico de la factura (USD).</summary>
    public decimal TotalUSD { get; init; }

    /// <summary>Sincronización USD efectivo (ANEXO XIII): inverso del Bs.S EFECTIVO a la tasa anclada.</summary>
    public decimal EffectiveTotalUSD { get; init; }

    public decimal TotalBsS { get; init; }
    public decimal AppliedRate { get; init; }
    public IReadOnlyList<ReceiptLine> Lines { get; init; } = Array.Empty<ReceiptLine>();

    /// <summary>Abono inicial efectivamente cobrado (vacío en crédito total).</summary>
    public IReadOnlyList<ReceiptPayment> Payments { get; init; } = Array.Empty<ReceiptPayment>();

    // Obligación CxC (S6): USD efectivo financiado + Bs.S del contravalor a la tasa de emisión.
    public int ReceivableId { get; init; }
    public decimal FinancedUSD { get; init; }
    public decimal FinancedBsS { get; init; }
    public decimal HistoricalRate { get; init; }
    public DateTime IssuedAtUtc { get; init; }
    public DateTime DueDateUtc { get; init; }

    public static CreditSaleReceiptContext CreateFrom(Sale sale, Receivable receivable)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(receivable);

        return new CreditSaleReceiptContext
        {
            SaleId = sale.Id,
            InvoiceNumber = sale.InvoiceNumber,
            Date = sale.Date,
            CustomerName = sale.CustomerName,
            CustomerCedula = sale.CustomerCedula,
            TotalUSD = sale.TotalUSD,
            EffectiveTotalUSD = sale.EffectiveTotalUSD,
            TotalBsS = sale.TotalBsS,
            AppliedRate = sale.AppliedRate,
            Lines = sale.Items.Select(i => new ReceiptLine(
                i.ProductName,
                i.Quantity,
                i.UnitPrice,
                i.Subtotal,
                i.UnitPriceBsS,
                i.SubtotalBsS)).ToList(),
            Payments = sale.Payments.Select(p => new ReceiptPayment(
                p.PaymentMethod?.Name,
                p.Amount,
                p.AmountBsS,
                p.ExchangeRate,
                p.ReferenceNumber)).ToList(),
            ReceivableId = receivable.Id,
            FinancedUSD = receivable.OriginalUSD,
            FinancedBsS = receivable.OriginalBsS,
            HistoricalRate = receivable.HistoricalRate,
            IssuedAtUtc = receivable.IssuedAtUtc,
            DueDateUtc = receivable.DueDateUtc
        };
    }
}
