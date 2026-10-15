using Core.Common;
using Core.DTOs;
using Core.Helpers;
using Core.Interfaces;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Inventory.Module.Services.Ocr;

public sealed partial class InvoiceTableParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex Preset1X40Pattern = new(
        @"(?i)\b(?:1\s*[xX\*]\s*(\d{1,4})|(\d{1,4})\s*[xX\*]\s*\d+(?:[.,]\d+)?\s*(?:GR|G|KG|ML|L|LT|LTS|CC|OZ|MG|UND|UNID|U))\b",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex PresetBtoPattern = new(
        @"(?i)\b(?:BTO|BULTO|DISPLAY|DISP|FARDO|FD)\s*(?:DE\s*|[xX\*]\s*)?(\d{1,4})\b",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex PresetCajaPattern = new(
        @"(?i)\b(?:CAJA|CJ|CJA|PAQ|PAQUETE|PACK|ESTUCHE)\s*(?:DE\s*|[xX\*]\s*)?(\d{1,4})(?:\s*(?:UND|UNID|UNIDADES|U|PCS|PZAS))?\b",
        RegexOptions.Compiled,
        RegexTimeout);

    /// <summary>
    /// Agrupa las palabras de una página en filas por centro Y (tolerancia 0.6× la altura
    /// mediana) y ordena cada fila de izquierda a derecha por centro X. Ignora texto vacío.
    /// </summary>
    private static List<List<OcrWord>> ClusterRows(IReadOnlyList<OcrWord> words)
    {
        var visible = words.Where(word => !string.IsNullOrWhiteSpace(word.Text)).ToList();
        if (visible.Count == 0)
        {
            return [];
        }

        var tolerance = ComputeRowTolerance(visible);
        var ordered = visible
            .OrderBy(CenterY)
            .ThenBy(CenterX)
            .ToList();
        var rows = new List<List<OcrWord>>();
        var current = new List<OcrWord> { ordered[0] };
        var centerSum = CenterY(ordered[0]);

        for (var index = 1; index < ordered.Count; index++)
        {
            var word = ordered[index];
            var rowCenter = centerSum / current.Count;
            if (Math.Abs(CenterY(word) - rowCenter) <= tolerance)
            {
                current.Add(word);
                centerSum += CenterY(word);
            }
            else
            {
                rows.Add(SortByX(current));
                current = [word];
                centerSum = CenterY(word);
            }
        }

        rows.Add(SortByX(current));
        return rows;
    }

    /// <summary>Mediana de las alturas positivas de la página, escalada por el factor de tolerancia.</summary>
    private static double ComputeRowTolerance(IReadOnlyList<OcrWord> words) =>
        ComputeMedianWordHeight(words) * RowToleranceHeightFactor;

    private static double ComputeMedianWordHeight(IReadOnlyList<OcrWord> words)
    {
        var heights = words
            .Select(word => word.Height)
            .Where(height => height > 0)
            .OrderBy(height => height)
            .ToArray();
        if (heights.Length == 0)
        {
            return 0;
        }

        return heights.Length % 2 == 1
            ? heights[heights.Length / 2]
            : (heights[(heights.Length / 2) - 1] + heights[heights.Length / 2]) / 2;
    }

    private static bool TryParseDecimal(string? value, out decimal result) =>
        FlexibleDecimalParser.TryParse(value, out result);

    internal static bool TryParseDecimalWithRule(
        string? value,
        OcrDecimalSeparatorRule rule,
        out decimal result)
    {
        result = 0m;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (rule == OcrDecimalSeparatorRule.Auto)
        {
            return FlexibleDecimalParser.TryParse(value, out result);
        }

        var cleaned = ExtractNumericToken(value);
        if (cleaned.Length == 0)
        {
            return false;
        }

        if (rule == OcrDecimalSeparatorRule.Comma)
        {
            var normalized = cleaned.Replace(".", string.Empty, StringComparison.Ordinal)
                .Replace(',', '.');
            if (decimal.TryParse(
                    normalized,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out result))
            {
                return true;
            }
        }
        else if (rule == OcrDecimalSeparatorRule.Dot)
        {
            var normalized = cleaned.Replace(",", string.Empty, StringComparison.Ordinal);
            if (decimal.TryParse(
                    normalized,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out result))
            {
                return true;
            }
        }

        return FlexibleDecimalParser.TryParse(value, out result);
    }

    private static string ExtractNumericToken(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        var started = false;
        foreach (var ch in raw)
        {
            if (char.IsDigit(ch) || ch is ',' or '.')
            {
                builder.Append(ch);
                started = true;
            }
            else if (ch == '-' && !started)
            {
                builder.Append(ch);
                started = true;
            }
            else if (started && !char.IsWhiteSpace(ch))
            {
                break;
            }
        }

        return builder.ToString().Trim('.', ',');
    }

    private static IReadOnlyList<string> ParseKeywordList(string? keywords)
    {
        if (string.IsNullOrWhiteSpace(keywords))
        {
            return Array.Empty<string>();
        }

        return keywords
            .Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static int CountCustomKeywordMatches(
        IReadOnlyList<OcrWord> row,
        IReadOnlyList<string> customKeywords)
    {
        if (customKeywords.Count == 0 || row.Count == 0)
        {
            return 0;
        }

        var normalizedWords = row.Select(word => Normalize(word.Text)).Where(w => w.Length > 0).ToArray();
        var concatenatedRow = string.Concat(normalizedWords);
        var hits = 0;

        foreach (var keyword in customKeywords)
        {
            if (normalizedWords.Any(word => word.Equals(keyword, StringComparison.Ordinal) || word.StartsWith(keyword, StringComparison.Ordinal)) ||
                concatenatedRow.Contains(keyword, StringComparison.Ordinal))
            {
                hits++;
            }
        }

        return hits;
    }

    private static bool MatchesAnyCustomKeyword(
        IReadOnlyList<OcrWord> row,
        IReadOnlyList<string> customKeywords) =>
        CountCustomKeywordMatches(row, customKeywords) > 0;

    private static List<List<OcrWord>> SegmentRowIntoCells(
        IReadOnlyList<OcrWord> row,
        int maxPos,
        int? productColumnPosition,
        double medianHeight)
    {
        var cells = row.Select(word => new List<OcrWord> { word }).ToList();
        if (cells.Count <= 1)
        {
            return cells;
        }

        var maxPhraseGap = Math.Max(45.0, medianHeight > 0 ? medianHeight * 2.2 : 45.0);

        while (cells.Count > Math.Max(1, maxPos))
        {
            var bestIndex = -1;
            var bestGap = double.MaxValue;

            for (var i = 0; i < cells.Count - 1; i++)
            {
                var left = cells[i];
                var right = cells[i + 1];
                if (IsCellNumeric(left) && IsCellNumeric(right))
                {
                    continue;
                }

                var leftRightEdge = left[^1].X + left[^1].Width;
                var rightLeftEdge = right[0].X;
                var gap = Math.Max(0.0, rightLeftEdge - leftRightEdge);

                if (gap <= maxPhraseGap && gap < bestGap)
                {
                    bestGap = gap;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                break;
            }

            cells[bestIndex].AddRange(cells[bestIndex + 1]);
            cells.RemoveAt(bestIndex + 1);
        }

        // Si no había suficientes columnas tras la fusión o había palabras de texto contiguas con gap muy corto:
        for (var i = 0; i < cells.Count - 1 && cells.Count > maxPos; i++)
        {
            if (productColumnPosition is int prodPos && i == prodPos - 1 && !IsCellNumeric(cells[i]) && !IsCellNumeric(cells[i + 1]))
            {
                cells[i].AddRange(cells[i + 1]);
                cells.RemoveAt(i + 1);
                i--;
            }
        }

        return cells;
    }

    private static bool IsCellNumeric(IReadOnlyList<OcrWord> cell)
    {
        if (cell.Count == 0)
        {
            return false;
        }

        var text = string.Join(' ', cell.Select(w => w.Text));
        var hasLetter = text.Any(char.IsLetter);
        return !hasLetter && TryParseDecimal(text, out _);
    }

    private static (string? Value, decimal Confidence) ExtractTextCell(
        IReadOnlyList<List<OcrWord>> cells,
        int? position,
        string separator)
    {
        if (position is not int pos || pos < 1 || pos > cells.Count)
        {
            return (null, OcrConfidence.Unresolved);
        }

        var words = cells[pos - 1];
        if (words.Count == 0)
        {
            return (null, OcrConfidence.Unresolved);
        }

        var text = string.Join(separator, words.Select(word => word.Text)).Trim();
        return string.IsNullOrWhiteSpace(text)
            ? (null, OcrConfidence.Unresolved)
            : (text, MinConfidence(words));
    }

    private static (decimal? Value, decimal Confidence) ExtractNumberCell(
        IReadOnlyList<List<OcrWord>> cells,
        int? position,
        OcrDecimalSeparatorRule separatorRule)
    {
        if (position is not int pos || pos < 1 || pos > cells.Count)
        {
            return (null, OcrConfidence.Unresolved);
        }

        var words = cells[pos - 1];
        if (words.Count == 0)
        {
            return (null, OcrConfidence.Unresolved);
        }

        var text = string.Join(' ', words.Select(word => word.Text));
        if (TryParseDecimalWithRule(text, separatorRule, out var value))
        {
            return (value, MinConfidence(words));
        }

        foreach (var word in words)
        {
            if (TryParseDecimalWithRule(word.Text, separatorRule, out var wordValue))
            {
                return (wordValue, MinConfidence([word]));
            }
        }

        return (null, OcrConfidence.Unresolved);
    }

    internal static string? ApplyCharacterReplacements(
        string? text,
        IReadOnlyList<OcrCharacterReplacementRuleDto>? replacements)
    {
        if (string.IsNullOrWhiteSpace(text) || replacements is null || replacements.Count == 0)
        {
            return text;
        }

        var current = text;
        foreach (var rule in replacements)
        {
            if (rule is null || string.IsNullOrEmpty(rule.From))
            {
                continue;
            }

            current = current.Replace(rule.From, rule.To ?? string.Empty, StringComparison.Ordinal);
        }

        var trimmed = current.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    internal static decimal? TryExtractConversionFactor(string? productName, string? patternKey)
    {
        if (string.IsNullOrWhiteSpace(productName) || string.IsNullOrWhiteSpace(patternKey))
        {
            return null;
        }

        var normalizedKey = patternKey.Trim().ToUpperInvariant();
        if (normalizedKey == "NONE")
        {
            return null;
        }

        try
        {
            if (normalizedKey is "1X40" or "AUTO")
            {
                var match = Preset1X40Pattern.Match(productName);
                if (match.Success)
                {
                    var groupText = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                    if (TryParsePositiveFactor(groupText, out var factor))
                    {
                        return factor;
                    }
                }

                if (normalizedKey == "1X40")
                {
                    return null;
                }
            }

            if (normalizedKey is "BTO_X_N" or "AUTO")
            {
                var match = PresetBtoPattern.Match(productName);
                if (match.Success && TryParsePositiveFactor(match.Groups[1].Value, out var factor))
                {
                    return factor;
                }

                if (normalizedKey == "BTO_X_N")
                {
                    return null;
                }
            }

            if (normalizedKey is "CAJA_N_UND" or "AUTO")
            {
                var match = PresetCajaPattern.Match(productName);
                if (match.Success && TryParsePositiveFactor(match.Groups[1].Value, out var factor))
                {
                    return factor;
                }

                if (normalizedKey == "CAJA_N_UND")
                {
                    return null;
                }
            }

            if (normalizedKey != "AUTO")
            {
                var customRegex = new Regex(patternKey.Trim(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                var match = customRegex.Match(productName);
                if (match.Success)
                {
                    var candidateGroup = match.Groups.Count > 1 && match.Groups[1].Success
                        ? match.Groups[1].Value
                        : match.Value;
                    if (TryParsePositiveFactor(candidateGroup, out var factor))
                    {
                        return factor;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return null;
        }

        return null;
    }

    private static bool TryParsePositiveFactor(string? raw, out decimal factor)
    {
        factor = 1m;
        if (!TryParseDecimal(raw, out var parsed) || parsed <= 1m)
        {
            return false;
        }

        factor = parsed;
        return true;
    }

    /// <summary>Normaliza a minúsculas sin diacríticos ni símbolos (solo letras y dígitos).</summary>
    private static string Normalize(string text)
    {
        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var normalized = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark &&
                char.IsLetterOrDigit(character))
            {
                normalized.Append(char.ToLowerInvariant(character));
            }
        }

        return normalized.ToString();
    }

    private static bool MatchesGeneric(string normalizedWord, string keyword) =>
        normalizedWord.Equals(keyword, StringComparison.Ordinal) ||
        normalizedWord.StartsWith(keyword, StringComparison.Ordinal);

    private static bool IsRangeUsed(bool[] used, int start, int length)
    {
        for (var index = start; index < start + length; index++)
        {
            if (used[index])
            {
                return true;
            }
        }

        return false;
    }

    private static void MarkUsed(bool[] used, int start, int length)
    {
        for (var index = start; index < start + length; index++)
        {
            used[index] = true;
        }
    }

    /// <summary>Centro X de la caja que engloba la secuencia de palabras.</summary>
    private static double SequenceCenterX(IReadOnlyList<OcrWord> row, int start, int length)
    {
        var left = double.MaxValue;
        var right = double.MinValue;
        for (var index = start; index < start + length; index++)
        {
            left = Math.Min(left, row[index].X);
            right = Math.Max(right, row[index].X + row[index].Width);
        }

        return (left + right) / 2;
    }

    private static List<OcrWord> SortByX(List<OcrWord> row) =>
        row.OrderBy(CenterX).ToList();

    private static double CenterX(OcrWord word) => word.X + (word.Width / 2);

    private static double CenterY(OcrWord word) => word.Y + (word.Height / 2);
}
