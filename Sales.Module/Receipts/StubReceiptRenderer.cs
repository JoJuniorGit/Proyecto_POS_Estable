namespace Sales.Module.Receipts;

public sealed class StubReceiptRenderer : IReceiptDocumentRenderer
{
    public ReceiptDocument Render(SaleReceiptContext context)
    {
        throw new NotImplementedException("La generación del comprobante digital (nota de entrega / recibo no fiscal) se implementa en la siguiente fase.");
    }
}