namespace Core.Constants;

/// <summary>
/// Claves centralizadas para el almacenamiento en caché de memoria (IMemoryCache).
/// </summary>
public static class CacheKeys
{
    /// <summary>
    /// Lista de métodos de pago activos configurados en el sistema.
    /// </summary>
    public const string ActivePaymentMethods = "active_payment_methods";

    /// <summary>
    /// Lista completa de todos los métodos de pago (activos e inactivos).
    /// </summary>
    public const string AllPaymentMethods = "all_payment_methods";

    /// <summary>
    /// Sesión de caja activa actualmente abierta.
    /// </summary>
    public const string ActiveCashDrawerSession = "active_cash_drawer_session";
}
