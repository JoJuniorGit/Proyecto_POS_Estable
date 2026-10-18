namespace Core.Entities;

public enum AuthorizationActionType
{
    ManualPriceOverride = 1,
    SaleCancellation = 2,

    // Paso 11/12 (S4): excepcion de sobregiro de cupo en venta a credito.
    CreditOverLimit = 3
}

public enum AuthorizationStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Expired = 3,
    Cancelled = 4
}

public enum AuthorizationResolutionMode
{
    Remote = 1,
    Local = 2
}
