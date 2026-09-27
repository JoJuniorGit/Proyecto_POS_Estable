using System;

namespace Sales.Module;

/// <summary>
/// 8.9-M16: Única fuente de verdad para la clasificación de moneda de un método de pago
/// (USD vs Bs.S). Todas las capas (backend, arqueo, PDF y clientes) deben usar este
/// clasificador en lugar de heurísticas locales por nombre que divergían entre sí.
/// </summary>
public static class PaymentMethodCurrencyResolver
{
    public const string Usd = "USD";
    public const string LocalCurrency = "Bs.S";

    /// <summary>
    /// Regla canónica: solo los métodos cuyo nombre contenga 'USD' se consideran dolarizados.
    /// Es la misma regla que históricamente usó el backend para el arqueo; se centraliza para
    /// que el frontend no pueda desalinearse con el cálculo de caja.
    /// </summary>
    public static string Resolve(string? methodName)
    {
        if (!string.IsNullOrWhiteSpace(methodName) &&
            methodName.Contains(Usd, StringComparison.OrdinalIgnoreCase))
        {
            return Usd;
        }

        return LocalCurrency;
    }
}