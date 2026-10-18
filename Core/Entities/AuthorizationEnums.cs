namespace Core.Entities;

public enum AuthorizationActionType
{
    ManualPriceOverride = 1,
    SaleCancellation = 2,

    // Paso 11/12 (S4): excepcion de sobregiro de cupo en venta a credito.
    CreditOverLimit = 3,

    // Paso 13 / 8.188-T1 (S4/S6): egreso de caja chica que exige autorizacion jerarquica.
    PettyCashExpense = 4
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
