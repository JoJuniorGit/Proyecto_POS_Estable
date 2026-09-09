namespace Sales.Module.Receipts;

public enum ReceiptDocumentKind
{
    NonFiscalSaleReceipt,
    DeliveryNote
}

public sealed record ReceiptDocument(ReceiptDocumentKind Kind, string Content, string FileName);