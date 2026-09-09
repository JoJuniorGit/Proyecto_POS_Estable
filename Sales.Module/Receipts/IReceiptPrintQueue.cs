namespace Sales.Module.Receipts;

public interface IReceiptPrintQueue
{
    void Enqueue(ReceiptDocument document);
}