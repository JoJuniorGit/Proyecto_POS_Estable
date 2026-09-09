using Sales.Module.Entities;

namespace Sales.Module.Receipts;

public sealed record ReceiptLine(
    string ProductName,
    decimal Quantity,
    decimal UnitPrice,
    decimal Subtotal,
    decimal UnitPriceBsS,
    decimal SubtotalBsS);

public sealed record ReceiptPayment(
    string? MethodName,
    decimal Amount,
    decimal AmountBsS,
    decimal ExchangeRate,
    string? ReferenceNumber);

public sealed class SaleReceiptContext
{
    public int SaleId { get; init; }
    public int? InvoiceNumber { get; init; }
    public DateTime Date { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerCedula { get; init; }
    public decimal Subtotal { get; init; }
    public decimal TotalUSD { get; init; }
    public decimal TotalBsS { get; init; }
    public decimal AppliedRate { get; init; }
    public decimal RoundingAdjustment { get; init; }
    public decimal FinalPaidAmountBsS { get; init; }
    public IReadOnlyList<ReceiptLine> Lines { get; init; } = Array.Empty<ReceiptLine>();
    public IReadOnlyList<ReceiptPayment> Payments { get; init; } = Array.Empty<ReceiptPayment>();

    public static SaleReceiptContext CreateFrom(Sale sale)
    {
        ArgumentNullException.ThrowIfNull(sale);
        return new SaleReceiptContext
        {
            SaleId = sale.Id,
            InvoiceNumber = sale.InvoiceNumber,
            Date = sale.Date,
            CustomerName = sale.CustomerName,
            CustomerCedula = sale.CustomerCedula,
            Subtotal = sale.Subtotal,
            TotalUSD = sale.TotalUSD,
            TotalBsS = sale.TotalBsS,
            AppliedRate = sale.AppliedRate,
            RoundingAdjustment = sale.RoundingAdjustment,
            FinalPaidAmountBsS = sale.FinalPaidAmountBsS,
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
                p.ReferenceNumber)).ToList()
        };
    }
}