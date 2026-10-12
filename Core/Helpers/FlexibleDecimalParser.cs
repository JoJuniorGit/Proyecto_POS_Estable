using System;
using System.Globalization;
using System.Linq;

namespace Core.Helpers;

/// <summary>
/// 8.169 [CLEAN-02]: shared tolerant decimal parser for es-VE/Latin invoice numbers across
/// tabular file ingestion (CSV/XLSX/XML) and OCR table extraction.
/// </summary>
public static class FlexibleDecimalParser
{
    public static bool TryParse(string? rawValue, out decimal result)
    {
        result = 0m;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        // 8.163: strip whitespace, currency symbols, and typographic/straight OCR quotes.
        var normalized = new string(rawValue
            .Where(character => character != ' ' && character != '$' && character != '€' &&
                character is not ('\u201C' or '\u201D' or '\u2018' or '\u2019' or '«' or '»' or '"'))
            .ToArray());
        if (normalized.Length == 0)
        {
            return false;
        }

        var commaIndex = normalized.LastIndexOf(',');
        var dotIndex = normalized.LastIndexOf('.');

        if (commaIndex >= 0 && dotIndex >= 0)
        {
            if (commaIndex > dotIndex)
            {
                normalized = normalized.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
            }
            else
            {
                normalized = normalized.Replace(",", string.Empty, StringComparison.Ordinal);
            }
        }
        else if (commaIndex >= 0)
        {
            normalized = normalized.Replace(',', '.');
        }
        else if (dotIndex >= 0 && IsThousandsSeparatedByDots(normalized))
        {
            normalized = normalized.Replace(".", string.Empty, StringComparison.Ordinal);
        }

        return decimal.TryParse(
            normalized,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out result);
    }

    private static bool IsThousandsSeparatedByDots(string value)
    {
        var digitStart = value[0] is '-' or '+' ? 1 : 0;
        if (digitStart < value.Length && value[digitStart] == '0')
        {
            return false;
        }

        var firstDot = value.IndexOf('.');
        if (firstDot <= 0 || !char.IsAsciiDigit(value[firstDot - 1]))
        {
            return false;
        }

        var groupLength = 0;
        for (var index = firstDot + 1; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '.')
            {
                if (groupLength != 3)
                {
                    return false;
                }

                groupLength = 0;
            }
            else if (char.IsAsciiDigit(character))
            {
                groupLength++;
            }
            else
            {
                return false;
            }
        }

        return groupLength == 3;
    }
}
