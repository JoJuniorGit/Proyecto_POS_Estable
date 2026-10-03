using Core.Helpers;

namespace Sales.Module.Receipts;

public sealed class StubReceiptRenderer : IReceiptDocumentRenderer
{
    public ReceiptDocument Render(SaleReceiptContext context, MoneyDisplayFormat format)
    {
        throw new NotImplementedException("La generación del comprobante digital (nota de entrega / recibo no fiscal) se implementa en la siguiente fase.");
    }
}