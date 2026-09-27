namespace Sales.Module.Receipts;

public sealed class SaleReceiptRenderer : IReceiptDocumentRenderer
{
    public ReceiptDocument Render(SaleReceiptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var kind = ReceiptDocumentKind.NonFiscalSaleReceipt;
        var invoice = context.InvoiceNumber.HasValue ? context.InvoiceNumber.Value.ToString("D5") : "PENDIENTE";
        var fileName = $"Recibo_{invoice}_{context.SaleId}_{context.Date:yyyyMMdd_HHmmss}.pdf";
        var bytes = SaleReceiptPdfGenerator.BuildPdf(context, kind);

        return new ReceiptDocument(kind, string.Empty, fileName)
        {
            Bytes = bytes,
            ContentType = "application/pdf"
        };
    }
}