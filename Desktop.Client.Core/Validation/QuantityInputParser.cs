using System;
using System.Globalization;
using System.Linq;

namespace Desktop.Client.Validation;

/// <summary>
/// Pure quantity input rules shared by the WPF behavior and the partial-pickup ViewModel.
/// It accepts both decimal separators while keeping display formatting on the active culture.
/// </summary>
public static class QuantityInputParser
{
    public const int DefaultMaxDecimalPlaces = 3;

    public static bool IsAllowedCharacter(char character, bool allowDecimals) =>
        char.IsAsciiDigit(character) || (allowDecimals && character is '.' or ',');

    public static string BuildText(
        string? current,
        int selectionStart,
        int selectionLength,
        string? inserted)
    {
        var source = current ?? string.Empty;
        var start = Math.Clamp(selectionStart, 0, source.Length);
        var length = Math.Clamp(selectionLength, 0, source.Length - start);
        return source.Remove(start, length).Insert(start, inserted ?? string.Empty);
    }

    public static bool IsValidTypingText(
        string? text,
        bool allowDecimals,
        int maxDecimalPlaces = DefaultMaxDecimalPlaces) =>
        string.IsNullOrEmpty(text) || TryParse(text, allowDecimals, maxDecimalPlaces, out _, out _);

    public static bool IsValidPaste(
        string? text,
        bool allowDecimals,
        int maxDecimalPlaces = DefaultMaxDecimalPlaces)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return TryParse(text, allowDecimals, maxDecimalPlaces, out _, out _);
    }

    public static bool TryParse(
        string? text,
        bool allowDecimals,
        int maxDecimalPlaces,
        out decimal value,
        out string? error)
    {
        value = 0m;
        error = null;

        if (string.IsNullOrWhiteSpace(text)) return true;

        if (text.Length != text.Trim().Length || text.Any(char.IsWhiteSpace))
        {
            error = "No se permiten espacios en la cantidad.";
            return false;
        }

        if (text.Contains('-') || text.Contains('+'))
        {
            error = "La cantidad no puede ser negativa.";
            return false;
        }

        if (text.Any(character => !char.IsAsciiDigit(character) && character is not '.' and not ','))
        {
            error = allowDecimals
                ? "Ingrese solo números y un separador decimal."
                : "Ingrese solo números enteros.";
            return false;
        }

        int separatorCount = text.Count(character => character is '.' or ',');
        if (separatorCount > 1)
        {
            error = "Use un solo separador decimal.";
            return false;
        }

        if (!allowDecimals && separatorCount > 0)
        {
            error = "Este producto solo admite cantidades enteras.";
            return false;
        }

        string wholePart;
        string decimalPart;
        if (separatorCount == 1)
        {
            int separatorIndex = text.IndexOfAny(['.', ',']);
            wholePart = text[..separatorIndex];
            decimalPart = text[(separatorIndex + 1)..];
        }
        else
        {
            wholePart = text;
            decimalPart = string.Empty;
        }

        if (decimalPart.Length > Math.Max(0, maxDecimalPlaces))
        {
            error = $"Máximo {Math.Max(0, maxDecimalPlaces)} decimales.";
            return false;
        }

        wholePart = wholePart.TrimStart('0');
        if (wholePart.Length == 0) wholePart = "0";

        var normalized = decimalPart.Length == 0
            ? wholePart
            : $"{wholePart}.{decimalPart}";

        if (!decimal.TryParse(
                normalized,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out value))
        {
            error = "Ingrese una cantidad válida.";
            return false;
        }

        if (value < 0m)
        {
            value = 0m;
            error = "La cantidad no puede ser negativa.";
            return false;
        }

        return true;
    }

    public static string Normalize(
        string? text,
        bool allowDecimals,
        int maxDecimalPlaces = DefaultMaxDecimalPlaces)
    {
        if (!TryParse(text, allowDecimals, maxDecimalPlaces, out var value, out _))
        {
            return text?.Trim() ?? string.Empty;
        }

        return Format(value);
    }

    public static string Format(decimal value) =>
        value.ToString("0.###", CultureInfo.CurrentCulture);
}
