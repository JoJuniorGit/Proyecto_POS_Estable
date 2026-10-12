using System.Text.RegularExpressions;

namespace Core.Common;

/// <summary>
/// 8.147-D9: bandas de confianza OCR compartidas por servidor y cliente. El cliente resalta
/// en amarillo por debajo de <see cref="YellowBelow"/> y en rojo por debajo de
/// <see cref="RedBelow"/>; un campo no resuelto lleva <see cref="Unresolved"/> (0).
/// </summary>
public static partial class OcrConfidence
{
    /// <summary>Confianza inferior a este valor (porcentaje 0–100) se considera roja.</summary>
    public const decimal RedBelow = 60m;

    /// <summary>Confianza inferior a este valor (y no roja) se considera amarilla.</summary>
    public const decimal YellowBelow = 85m;

    /// <summary>Confianza de un campo que el OCR no pudo resolver (S3: no resuelto = 0).</summary>
    public const decimal Unresolved = 0m;

    /// <summary>
    /// 8.168: corrige lecturas OCR frecuentes donde el separador de empaque "X" entre números
    /// se interpreta como "%" (p. ej. "36%250GR" → "36X250GR", "1%5" → "1X5", "10%20 UNI" → "10X20 UNI")
    /// o como "4" antes de una unidad de empaque/peso (p. ej. "10420 UNI" → "10X20 UNI").
    /// </summary>
    public static string? SanitizeProductName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var sanitized = PercentPackRegex().Replace(name, "X");
        sanitized = MisreadFourPackRegex().Replace(sanitized, "$1X$2");
        return sanitized;
    }

    [GeneratedRegex(@"(?<=\d)%(?=\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PercentPackRegex();

    [GeneratedRegex(@"\b(\d{1,2})4([1-9]\d{1,3})(?=\s*(?:UNI|UND|UN|U|GR|G|KG|ML|LT|L|CC|OZ|PZ|PZA|CAJ|PAQ|BLT|SOB)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MisreadFourPackRegex();
}

