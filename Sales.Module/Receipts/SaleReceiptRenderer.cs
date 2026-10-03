using System.Globalization;
using Core.Helpers;

namespace Sales.Module.Receipts;

public sealed class SaleReceiptRenderer : IReceiptDocumentRenderer
{
    public ReceiptDocument Render(SaleReceiptContext context, MoneyDisplayFormat format)
    {
        ArgumentNullException.ThrowIfNull(context);
        var kind = ReceiptDocumentKind.NonFiscalSaleReceipt;
        var invoice = context.InvoiceNumber.HasValue ? context.InvoiceNumber.Value.ToString("D5") : "PENDIENTE";
        var fileName = $"Recibo_{invoice}_{context.SaleId}_{context.Date.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.pdf";
        var bytes = SaleReceiptPdfGenerator.BuildPdf(context, kind, format);

        return new ReceiptDocument(kind, string.Empty, fileName)
        {
            Bytes = bytes,
            ContentType = "application/pdf"
        };
    }
}