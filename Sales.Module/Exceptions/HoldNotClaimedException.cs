using System;

namespace Sales.Module.Exceptions;

public class HoldNotClaimedException : InvalidOperationException
{
    public int SaleId { get; }

    public HoldNotClaimedException(int saleId)
        : base($"El pedido #{saleId} no está reclamado; reclame el pedido antes de modificarlo.")
        => SaleId = saleId;
}
