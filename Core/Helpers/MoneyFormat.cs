using System.Globalization;

namespace Core.Helpers;

/// <summary>8.143: formato de visualización de moneda seleccionable por el ajuste CurrencyFormat.</summary>
public enum MoneyDisplayFormat
{
    Venezuelan = 0,
    International = 1
}

/// <summary>
/// 8.143: única fuente de verdad para el formateo monetario determinista del POS.
/// La convención del producto es es-VE (miles '.', decimales ',') sin depender de la
/// cultura del SO donde corra el proceso (el runner de CI usa en-US).
/// </summary>
public static class MoneyFormat
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-VE");

    public static string N0(decimal value) => value.ToString("N0", Culture);

    public static string N2(decimal value) => value.ToString("N2", Culture);

    /// <summary>8.143: formatea un monto según el ajuste activo (Venezolano = es-VE, Internacional = invariante).</summary>
    public static string Number(decimal value, MoneyDisplayFormat format, int decimals = 2)
        => value.ToString($"N{decimals}", format == MoneyDisplayFormat.International ? CultureInfo.InvariantCulture : Culture);

    /// <summary>8.143: normaliza el valor persistido de CurrencyFormat; cualquier valor distinto de
    /// "International" (case-insensitive) cae a Venezuelan, igual criterio que SettingsController.</summary>
    public static MoneyDisplayFormat ParseFormat(string? value)
        => string.Equals(value, "International", StringComparison.OrdinalIgnoreCase)
            ? MoneyDisplayFormat.International
            : MoneyDisplayFormat.Venezuelan;
}
