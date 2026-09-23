namespace Sales.Module.Receipts;

public enum ReceiptDocumentKind
{
    NonFiscalSaleReceipt,
    DeliveryNote
}

public sealed record ReceiptDocument(ReceiptDocumentKind Kind, string Content, string FileName)
{
    public byte[]? Bytes { get; init; }
    public string ContentType { get; init; } = "text/plain";
}
