using System;
using Core.DTOs;
using Core.Helpers;

namespace Desktop.Client.Services;

/// <summary>
/// 8.143: estado de display del formato de moneda activo en el cliente WPF.
/// El ajuste vive server-side (clave "CurrencyFormat"); se sincroniza al arrancar la app
/// y al cambiarlo desde Settings, y por defecto es Venezuelan.
/// 8.211-T4c1 (S9): además guarda el perfil monetario de presentación (modo, códigos y símbolos)
/// que los clientes leen del endpoint read-only. El formato numérico sigue siendo independiente
/// (<see cref="Current"/>/<see cref="Number"/>) y no se duplica aritmética financiera.
/// </summary>
public static class CurrencyDisplay
{
    private const string VesCode = "VES";
    private const string VesSymbol = "Bs.S";
    private const string DefaultPrimaryCode = "USD";
    private const string DefaultPrimarySymbol = "$";
    private const string DefaultSecondaryCode = "VES";
    private const string DefaultSecondarySymbol = "Bs.S";
    private const int MaxSymbolLength = 8;

    /// <summary>8.211-T6-U12: raised after the profile state changes; WPF binds to the presenter.</summary>
    public static event Action? ProfileChanged;

    public static MoneyDisplayFormat Current { get; set; } = MoneyDisplayFormat.Venezuelan;

    public static string PrimaryCurrencyCode { get; private set; } = DefaultPrimaryCode;

    public static string PrimaryCurrencySymbol { get; private set; } = DefaultPrimarySymbol;

    public static string? SecondaryCurrencyCode { get; private set; } = DefaultSecondaryCode;

    public static string? SecondaryCurrencySymbol { get; private set; } = DefaultSecondarySymbol;

    public static MonetaryMode Mode { get; private set; } = MonetaryMode.Dual;

    public static bool IsSingle => Mode == MonetaryMode.Single;

    /// <summary>Moneda local de la operación: secundaria en Dual, primaria en Single.</summary>
    public static string LocalCurrencyCode => Mode == MonetaryMode.Single
        ? PrimaryCurrencyCode
        : SecondaryCurrencyCode ?? PrimaryCurrencyCode;

    /// <summary>Símbolo de presentación de la moneda local (nunca <c>?</c>).</summary>
    public static string LocalCurrencySymbol => Mode == MonetaryMode.Single
        ? PrimaryCurrencySymbol
        : SecondaryCurrencySymbol ?? PrimaryCurrencySymbol;

    public static void SetFromSetting(string? key) => Current = MoneyFormat.ParseFormat(key);

    public static string Number(decimal value, int decimals = 2) => MoneyFormat.Number(value, Current, decimals);

    /// <summary>
    /// 8.211-T4c1 (S9): aplica el perfil de presentación devuelto por el backend. Un perfil null,
    /// con modo desconocido o con campos inválidos degrada a valores seguros (defaults Dual
    /// USD/$ + VES/Bs.S, símbolos por código) sin lanzar; <c>Single</c> siempre limpia la secundaria.
    /// </summary>
    public static void SetMonetaryProfile(MonetaryProfileDto? profile)
    {
        if (profile is null || !Enum.IsDefined(profile.Mode))
        {
            ApplyDefaults();
            ProfileChanged?.Invoke();
            return;
        }

        var primaryCode = NormalizeCurrencyCode(profile.PrimaryCurrencyCode) ?? DefaultPrimaryCode;
        var primarySymbol = NormalizeSymbol(profile.PrimaryCurrencySymbol, DefaultSymbolForCode(primaryCode));

        Mode = profile.Mode;
        PrimaryCurrencyCode = primaryCode;
        PrimaryCurrencySymbol = primarySymbol;

        if (profile.Mode == MonetaryMode.Single)
        {
            SecondaryCurrencyCode = null;
            SecondaryCurrencySymbol = null;
            ProfileChanged?.Invoke();
            return;
        }

        var requestedSecondaryCode = NormalizeCurrencyCode(profile.SecondaryCurrencyCode);
        var hasDistinctSecondary = requestedSecondaryCode is not null
            && !string.Equals(requestedSecondaryCode, primaryCode, StringComparison.Ordinal);

        if (!hasDistinctSecondary)
        {
            // Un par inválido (misma moneda o código ilegible) no debe heredar el símbolo
            // solicitado para la moneda descartada.
            var fallbackCode = primaryCode == DefaultPrimaryCode ? DefaultSecondaryCode : DefaultPrimaryCode;
            SecondaryCurrencyCode = fallbackCode;
            SecondaryCurrencySymbol = DefaultSymbolForCode(fallbackCode);
            ProfileChanged?.Invoke();
            return;
        }

        SecondaryCurrencyCode = requestedSecondaryCode;
        SecondaryCurrencySymbol = NormalizeSymbol(
            profile.SecondaryCurrencySymbol,
            DefaultSymbolForCode(requestedSecondaryCode!));
        ProfileChanged?.Invoke();
    }

    /// <summary>Monto primario con su símbolo, delegando los separadores en <see cref="Number"/>.</summary>
    public static string FormatPrimary(decimal value, int decimals = 2)
        => FormatWithSymbol(PrimaryCurrencySymbol, value, decimals);

    /// <summary>
    /// Monto secundario con su símbolo. En <c>Single</c> no hay segunda moneda: se presenta la
    /// primaria para que cualquier consumidor rezagado mantenga un resultado coherente.
    /// </summary>
    public static string FormatSecondary(decimal value, int decimals = 2)
        => Mode == MonetaryMode.Dual
            ? FormatWithSymbol(SecondaryCurrencySymbol ?? DefaultSecondarySymbol, value, decimals)
            : FormatPrimary(value, decimals);

    /// <summary>
    /// Resuelve el símbolo configurado para un código ISO (acepta los alias legacy de VES).
    /// Un código ilegible devuelve cadena vacía y una moneda ajena al perfil usa su código ISO;
    /// nunca devuelve <c>?</c>.
    /// </summary>
    public static string SymbolFor(string? code)
    {
        var normalized = NormalizeCurrencyCode(code);
        if (normalized is null)
        {
            return string.Empty;
        }

        if (string.Equals(normalized, PrimaryCurrencyCode, StringComparison.Ordinal))
        {
            return PrimaryCurrencySymbol;
        }

        if (Mode == MonetaryMode.Dual
            && string.Equals(normalized, SecondaryCurrencyCode, StringComparison.Ordinal))
        {
            return SecondaryCurrencySymbol ?? DefaultSymbolForCode(normalized);
        }

        return DefaultSymbolForCode(normalized);
    }

    private static void ApplyDefaults()
    {
        Mode = MonetaryMode.Dual;
        PrimaryCurrencyCode = DefaultPrimaryCode;
        PrimaryCurrencySymbol = DefaultPrimarySymbol;
        SecondaryCurrencyCode = DefaultSecondaryCode;
        SecondaryCurrencySymbol = DefaultSecondarySymbol;
    }

    private static string FormatWithSymbol(string? symbol, decimal value, int decimals)
    {
        var formatted = Number(value, decimals);
        return string.IsNullOrWhiteSpace(symbol) ? formatted : $"{symbol} {formatted}";
    }

    private static string? NormalizeCurrencyCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToUpperInvariant();
        if (normalized is VesCode or "BS.S" or "BSS")
        {
            return VesCode;
        }

        if (normalized.Length != 3)
        {
            return null;
        }

        foreach (var character in normalized)
        {
            if (character is < 'A' or > 'Z')
            {
                return null;
            }
        }

        return normalized;
    }

    private static string DefaultSymbolForCode(string code) => code switch
    {
        DefaultPrimaryCode => DefaultPrimarySymbol,
        VesCode => VesSymbol,
        _ => code
    };

    private static string NormalizeSymbol(string? value, string fallback)
    {
        if (value is null)
        {
            return fallback;
        }

        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > MaxSymbolLength || ContainsControlCharacter(trimmed))
        {
            return fallback;
        }

        return trimmed;
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character) || character == '\u2028' || character == '\u2029')
            {
                return true;
            }
        }

        return false;
    }
}

