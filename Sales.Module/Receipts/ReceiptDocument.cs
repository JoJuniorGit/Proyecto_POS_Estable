namespace Sales.Module.Receipts;

public enum ReceiptDocumentKind
{
    NonFiscalSaleReceipt,
    DeliveryNote,

    // Paso 11/12 (S5): comprobantes de venta a credito y de abono CxC.
    CreditSale,
    CreditPayment
}

public sealed record ReceiptDocument(ReceiptDocumentKind Kind, string Content, string FileName)
{
    public byte[]? Bytes { get; init; }
    public string ContentType { get; init; } = "text/plain";
}
