using Core.Helpers;
using Core.Interfaces;
using System.Globalization;
using System.Text;

namespace Inventory.Module.Services.Ocr;

public sealed partial class InvoiceTableParser
{
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
    private static double ComputeRowTolerance(IReadOnlyList<OcrWord> words)
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

        var median = heights.Length % 2 == 1
            ? heights[heights.Length / 2]
            : (heights[(heights.Length / 2) - 1] + heights[heights.Length / 2]) / 2;
        return median * RowToleranceHeightFactor;
    }

    private static bool TryParseDecimal(string? value, out decimal result) =>
        FlexibleDecimalParser.TryParse(value, out result);

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
