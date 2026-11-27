using System;
using System.Collections.Generic;
using System.Globalization;

namespace Core.Common;

/// <summary>
/// 8.211-T1 (S1): normalización y catálogo de códigos ISO 4217 alfa-3. El conjunto se construye
/// una vez a partir de las culturas específicas del runtime y se une con las monedas de curso legal
/// que el POS necesita garantizar. El conjunto mutable nunca se expone.
/// </summary>
public static class Iso4217
{
    private static readonly string[] FallbackCodes =
    {
        "USD", "EUR", "VES", "COP", "MXN", "PEN", "CLP", "ARS", "BRL", "GBP", "CAD", "JPY", "CNY",
        "CLF", "UYW"
    };

    private static readonly HashSet<string> SupportedCodes = BuildSupportedCodes();

    /// <summary>Recorta y pasa a mayúsculas; null o whitespace devuelve null.</summary>
    public static string? Normalize(string? code)
        => string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

    /// <summary>
    /// Indica si un código YA normalizado es un ISO 4217 alfa-3 soportado. Un valor malformado
    /// (longitud distinta de 3 o con caracteres no alfabéticos) siempre devuelve false.
    /// </summary>
    public static bool IsSupported(string? normalizedCode)
    {
        if (normalizedCode is not { Length: 3 })
        {
            return false;
        }

        foreach (var character in normalizedCode)
        {
            if (character is < 'A' or > 'Z')
            {
                return false;
            }
        }

        return SupportedCodes.Contains(normalizedCode);
    }

    private static HashSet<string> BuildSupportedCodes()
    {
        var codes = new HashSet<string>(FallbackCodes, StringComparer.Ordinal);

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                if (!string.IsNullOrWhiteSpace(region.ISOCurrencySymbol))
                {
                    codes.Add(region.ISOCurrencySymbol.Trim().ToUpperInvariant());
                }
            }
            catch (ArgumentException)
            {
                // Culturas sin región válida: el fallback ya cubre las monedas del POS.
            }
        }

        return codes;
    }
}
