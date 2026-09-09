namespace Sales.Module.Receipts;

public sealed class NullReceiptPrintQueue : IReceiptPrintQueue
{
    public void Enqueue(ReceiptDocument document)
    {
    }
}