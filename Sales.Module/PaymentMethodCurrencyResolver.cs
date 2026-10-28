using System;
using Core.Common;

namespace Sales.Module;

/// <summary>
/// 8.9-M16: Única fuente de verdad para la clasificación de moneda de un método de pago
/// (USD vs Bs.S). Todas las capas (backend, arqueo, PDF y clientes) deben usar este
/// clasificador en lugar de heurísticas locales por nombre que divergían entre sí.
/// 8.211-T3c3b2 (S5/S7): el vocabulario se amplía a cualquier código ISO 4217 soportado;
/// Bs.S/BsS/VES siguen colapsando a la moneda local de presentación.
/// </summary>
public static class PaymentMethodCurrencyResolver
{
    public const string Usd = "USD";
    public const string LocalCurrency = "Bs.S";

    /// <summary>
    /// Normaliza una moneda persistida al vocabulario canónico. Los alias locales resuelven a
    /// <see cref="LocalCurrency"/>; el resto conserva su código ISO 4217. Devuelve null cuando el
    /// valor no es reconocible (fila legada o payload vacío).
    /// </summary>
    public static string? Normalize(string? currency)
    {
        var isoCode = CurrencyCodeNormalizer.NormalizeToIso(currency);
        if (isoCode is null)
        {
            return null;
        }

        return string.Equals(isoCode, "VES", StringComparison.Ordinal) ? LocalCurrency : isoCode;
    }

    /// <summary>
    /// Moneda efectiva de un método: usa la persistida cuando es válida y cae a la regla
    /// histórica por nombre solo para filas legadas (Currency null/desconocida).
    /// </summary>
    public static string ResolveStoredOrName(string? storedCurrency, string? methodName)
        => Normalize(storedCurrency) ?? Resolve(methodName);

    /// <summary>
    /// Regla canónica: solo los métodos cuyo nombre contenga 'USD' se consideran dolarizados.
    /// Es la misma regla que históricamente usó el backend para el arqueo; se centraliza para
    /// que el frontend no pueda desalinearse con el cálculo de caja. Solo se usa para backfill
    /// y para filas legadas sin <c>Currency</c> persistida.
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