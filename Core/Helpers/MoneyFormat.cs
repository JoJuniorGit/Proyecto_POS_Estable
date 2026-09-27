using System.Globalization;

namespace Core.Helpers;

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
}
