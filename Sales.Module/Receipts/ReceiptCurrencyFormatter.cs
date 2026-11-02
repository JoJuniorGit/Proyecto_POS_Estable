using System;
using System.Linq;
using Core.Common;
using Core.DTOs;

namespace Sales.Module.Receipts;

/// <summary>
/// 8.211-T4a (S10/F4a): presentación de moneda de los comprobantes backend. El código sale de
/// los snapshots persistidos (venta/abono/método); el símbolo configurado del perfil solo se usa
/// cuando su código coincide y es representable en la fuente PDF (ASCII imprimible). Un símbolo
/// Unicode (p. ej. "€") cae al código de presentación (EUR) y nunca a "?".
/// </summary>
public static class ReceiptCurrencyFormatter
{
    public const string UsdCode = "USD";
    public const string VesCode = "VES";
    public const string VesPresentationCode = "Bs.S";
    public const string LegacyLocalCode = "Bs.S";
    public const string DualMode = "Dual";
    public const string SingleMode = "Single";

    /// <summary>Par código ISO persistido + etiqueta segura para el documento.</summary>
    public sealed record ReceiptCurrencyLabel(string Code, string Symbol);

    /// <summary>
    /// Código ISO canónico del valor persistido. Los alias locales (<c>Bs.S</c>/<c>BsS</c>) se
    /// resuelven a <c>VES</c>. Un código no catalogado se conserva en mayúsculas para no perder la
    /// moneda persistida de una fila histórica.
    /// </summary>
    public static string? NormalizeCode(string? code)
    {
        var iso = CurrencyCodeNormalizer.NormalizeToIso(code);
        if (iso is not null)
        {
            return iso;
        }

        var trimmed = code?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed.ToUpperInvariant();
    }

    /// <summary>Código de presentación: <c>VES</c> se muestra como <c>Bs.S</c>; el resto ISO tal cual.</summary>
    public static string PresentationCode(string? code)
    {
        var normalized = NormalizeCode(code);
        if (normalized is null)
        {
            return string.Empty;
        }

        return string.Equals(normalized, VesCode, StringComparison.Ordinal)
            ? VesPresentationCode
            : normalized;
    }

    /// <summary>
    /// Un símbolo es representable en la fuente PDF del comprobante solo si es ASCII imprimible
    /// (la plomería Type1/WinAnsi escapa el español pero no tiene glifos Unicode arbitrarios).
    /// </summary>
    public static bool IsPdfRepresentable(string? text)
        => !string.IsNullOrWhiteSpace(text) && text.All(c => c >= 32 && c <= 126);

    /// <summary>Defaults de compatibilidad cuando el perfil no aporta símbolo para el código.</summary>
    public static string DefaultSymbol(string? code)
    {
        var normalized = NormalizeCode(code);
        return normalized switch
        {
            null => string.Empty,
            UsdCode => "$",
            VesCode => VesPresentationCode,
            _ => normalized
        };
    }

    /// <summary>
    /// Etiqueta segura: el símbolo configurado si es ASCII imprimible; si no, el código de
    /// presentación (nunca <c>?</c>).
    /// </summary>
    public static string ResolveSymbol(string? code, string? configuredSymbol)
        => IsPdfRepresentable(configuredSymbol)
            ? configuredSymbol!.Trim()
            : DefaultSymbol(code);

    /// <summary>
    /// Resuelve el código y su etiqueta. El símbolo del perfil solo aplica cuando el código
    /// coincide con la primaria o la secundaria configurada.
    /// </summary>
    public static ReceiptCurrencyLabel ResolveCurrency(
        string? code,
        string? fallbackCode,
        MonetaryProfileDto? profile)
    {
        var normalized = NormalizeCode(code)
            ?? NormalizeCode(fallbackCode)
            ?? NormalizeCode(profile?.PrimaryCurrencyCode)
            ?? UsdCode;

        string? configuredSymbol = null;
        if (profile is not null)
        {
            if (IsSameCode(normalized, profile.PrimaryCurrencyCode))
            {
                configuredSymbol = profile.PrimaryCurrencySymbol;
            }
            else if (IsSameCode(normalized, profile.SecondaryCurrencyCode))
            {
                configuredSymbol = profile.SecondaryCurrencySymbol;
            }
        }

        return new ReceiptCurrencyLabel(normalized, ResolveSymbol(normalized, configuredSymbol));
    }

    /// <summary>
    /// Normaliza el modo persistido. Un valor ausente o ilegible (fila legacy) cae al modo del
    /// perfil o, sin perfil, al default histórico <see cref="DualMode"/>.
    /// </summary>
    /// <summary>Indica si USD pertenece al perfil (primaria o secundaria). Sin perfil, legacy true.</summary>
    public static bool ProfileIncludesUsd(MonetaryProfileDto? profile)
    {
        if (profile is null)
        {
            return true;
        }

        bool primaryUsd = IsSameCode(profile.PrimaryCurrencyCode, UsdCode);
        return profile.Mode == MonetaryMode.Single
            ? primaryUsd
            : primaryUsd || IsSameCode(profile.SecondaryCurrencyCode, UsdCode);
    }

    public static string NormalizeMode(string? mode, MonetaryProfileDto? profile)
    {
        if (string.Equals(mode?.Trim(), SingleMode, StringComparison.OrdinalIgnoreCase))
        {
            return SingleMode;
        }

        if (string.Equals(mode?.Trim(), DualMode, StringComparison.OrdinalIgnoreCase))
        {
            return DualMode;
        }

        return profile?.Mode == MonetaryMode.Single ? SingleMode : DualMode;
    }

    private static bool IsSameCode(string? left, string? right)
        => NormalizeCode(left) is { } leftCode
            && NormalizeCode(right) is { } rightCode
            && string.Equals(leftCode, rightCode, StringComparison.Ordinal);
}
