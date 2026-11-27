using System;
using System.Collections.Generic;

namespace Core.Common;

/// <summary>
/// 8.211-T2b (S5): precision for a currency. <see cref="DigitalDecimals"/> is the ISO 4217 minor
/// unit used for digital amounts; <see cref="CashDecimals"/> governs physical cash.
/// </summary>
public sealed record CurrencyPrecision(int DigitalDecimals, int CashDecimals);

/// <summary>
/// 8.211-T2b (S5): per-currency precision resolved from the ISO 4217 minor unit. The historical
/// Bs.S cash policy (integer units) is preserved without changing any existing rounding path.
/// </summary>
public static class CurrencyPrecisionCatalog
{
    private const string VesCode = "VES";
    private const int DefaultDecimals = 2;

    private static readonly HashSet<string> ZeroDecimalCurrencies = new(StringComparer.Ordinal)
    {
        "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG", "RWF", "UGX", "VND", "VUV",
        "XAF", "XOF", "XPF"
    };

    private static readonly HashSet<string> ThreeDecimalCurrencies = new(StringComparer.Ordinal)
    {
        "BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND"
    };

    private static readonly HashSet<string> FourDecimalCurrencies = new(StringComparer.Ordinal)
    {
        "CLF", "UYW"
    };

    /// <summary>
    /// Returns the precision for <paramref name="currencyCode"/> after ISO 4217 normalization.
    /// </summary>
    /// <exception cref="ArgumentException">The code is missing, malformed, or unsupported.</exception>
    public static CurrencyPrecision For(string currencyCode)
    {
        var normalized = Iso4217.Normalize(currencyCode);
        if (normalized is null)
        {
            throw new ArgumentException("El código de moneda es obligatorio.");
        }

        var isoCode = CurrencyCodeNormalizer.NormalizeToIso(normalized);
        if (isoCode is null)
        {
            throw new ArgumentException($"El código '{normalized}' no es un código ISO 4217 reconocido.");
        }

        int digitalDecimals;
        if (ZeroDecimalCurrencies.Contains(isoCode))
        {
            digitalDecimals = 0;
        }
        else if (ThreeDecimalCurrencies.Contains(isoCode))
        {
            digitalDecimals = 3;
        }
        else if (FourDecimalCurrencies.Contains(isoCode))
        {
            digitalDecimals = 4;
        }
        else
        {
            digitalDecimals = DefaultDecimals;
        }

        // Bs.S cash is rounded to whole units by historical policy; digital Bs.S keeps its minor unit.
        var cashDecimals = isoCode == VesCode ? 0 : digitalDecimals;

        return new CurrencyPrecision(digitalDecimals, cashDecimals);
    }
}
