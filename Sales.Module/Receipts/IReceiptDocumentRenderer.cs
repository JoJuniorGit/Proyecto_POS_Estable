namespace Sales.Module.Receipts;

public interface IReceiptDocumentRenderer
{
    ReceiptDocument Render(SaleReceiptContext context);
}