using Core.Common;
using Core.DTOs;
using Core.Interfaces;
using System.Globalization;
using System.Text;

namespace Inventory.Module.Services.Ocr;

/// <summary>
/// 8.147-T3/D6/D7/D8: parser heurístico de word-boxes OCR a filas de factura. Puro (sin I/O ni
/// WPF): agrupa palabras por línea (tolerancia 0.6× la altura mediana), ancla columnas en la fila
/// de encabezado (nombres de la plantilla del proveedor primero; keywords genéricas como
/// fallback), asigna cada palabra a la banda X de su columna y proyecta
/// <see cref="OcrExtractedLineDto"/> con confianza por campo (mínimo de las palabras que lo
/// componen, 1 decimal; campo no resuelto = <c>OcrConfidence.Unresolved</c>).
/// </summary>
public sealed class InvoiceTableParser
{
    /// <summary>Fracción de la altura mediana usada como tolerancia vertical de clustering.</summary>
    public const double RowToleranceHeightFactor = 0.6;

    /// <summary>Mínimo de columnas detectadas en una fila para considerarla encabezado.</summary>
    public const int MinimumHeaderColumns = 2;

    private static readonly InvoiceColumn[] AllColumns =
    [
        InvoiceColumn.Name,
        InvoiceColumn.Quantity,
        InvoiceColumn.UnitCost,
        InvoiceColumn.SupplierCode,
        InvoiceColumn.Barcode
    ];

    private static readonly IReadOnlyDictionary<InvoiceColumn, string[]> GenericKeywords =
        new Dictionary<InvoiceColumn, string[]>
        {
            [InvoiceColumn.Name] = ["descripcion", "detalle", "nombre", "producto", "articulo"],
            [InvoiceColumn.Quantity] = ["cant", "cantidad", "unidades", "unid"],
            [InvoiceColumn.UnitCost] = ["precio", "costo", "unitario", "importe"],
            [InvoiceColumn.SupplierCode] = ["codigo", "cod", "sku", "ref", "referencia"],
            [InvoiceColumn.Barcode] = ["barras", "ean", "barcode", "upc"]
        };

    /// <summary>Parsea páginas OCR (en orden) a filas de factura.</summary>
    /// <param name="pages">Páginas reconocidas, cada una con sus word-boxes.</param>
    /// <param name="template">Plantilla de columnas del proveedor; null usa solo keywords genéricas.</param>
    public IReadOnlyList<OcrExtractedLineDto> Parse(IReadOnlyList<OcrPage> pages, SupplierColumnMappingDto? template)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var lines = new List<OcrExtractedLineDto>();
        List<ColumnAnchor>? anchors = null;

        foreach (var page in pages)
        {
            var rows = ClusterRows(page.Words);
            var header = FindHeader(rows, template);
            List<List<OcrWord>> dataRows;

            if (header is not null)
            {
                anchors = header.Anchors;
                // Las filas sobre el encabezado (RIF, datos del proveedor) no son filas de factura.
                dataRows = rows.Skip(header.RowIndex + 1).ToList();
            }
            else if (anchors is not null)
            {
                // Página de continuación sin encabezado propio: se reutilizan los últimos anchors.
                dataRows = rows;
            }
            else
            {
                // Sin anchors (ni propios ni heredados) la página no produce líneas.
                continue;
            }

            var bands = BuildBands(anchors);
            foreach (var row in dataRows)
            {
                var line = MapRow(row, bands);
                if (line is not null)
                {
                    lines.Add(line);
                }
            }
        }

        return lines;
    }

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

    /// <summary>Elige como encabezado la fila con más columnas reconocidas (mínimo 2).</summary>
    private static HeaderMatch? FindHeader(List<List<OcrWord>> rows, SupplierColumnMappingDto? template)
    {
        HeaderMatch? best = null;
        for (var index = 0; index < rows.Count; index++)
        {
            var (anchors, templateMatches) = MatchHeaderRow(rows[index], template);
            if (anchors.Count < MinimumHeaderColumns)
            {
                continue;
            }

            // Empate de columnas: gana la fila con más nombres de plantilla y, si persiste, la primera.
            if (best is null ||
                anchors.Count > best.Anchors.Count ||
                (anchors.Count == best.Anchors.Count && templateMatches > best.TemplateMatches))
            {
                best = new HeaderMatch(index, anchors, templateMatches);
            }
        }

        return best;
    }

    /// <summary>
    /// Matchea una fila contra las columnas: primero los nombres de la plantilla del proveedor
    /// (secuencias de palabras, acento/mayúscula-insensible) y después las keywords genéricas de
    /// las columnas aún sin anclar.
    /// </summary>
    private static (List<ColumnAnchor> Anchors, int TemplateMatches) MatchHeaderRow(
        IReadOnlyList<OcrWord> row,
        SupplierColumnMappingDto? template)
    {
        var used = new bool[row.Count];
        var anchors = new List<ColumnAnchor>();
        var templateMatches = 0;

        foreach (var (column, templateName) in GetTemplateColumns(template))
        {
            if (string.IsNullOrWhiteSpace(templateName))
            {
                continue;
            }

            var sequence = FindTemplateSequence(row, used, Normalize(templateName));
            if (sequence is null)
            {
                continue;
            }

            MarkUsed(used, sequence.Value.Start, sequence.Value.Length);
            anchors.Add(new ColumnAnchor(column, SequenceCenterX(row, sequence.Value.Start, sequence.Value.Length)));
            templateMatches++;
        }

        foreach (var column in AllColumns)
        {
            if (anchors.Any(anchor => anchor.Column == column))
            {
                continue;
            }

            for (var index = 0; index < row.Count; index++)
            {
                if (used[index])
                {
                    continue;
                }

                var normalized = Normalize(row[index].Text);
                if (normalized.Length > 0 &&
                    GenericKeywords[column].Any(keyword => MatchesGeneric(normalized, keyword)))
                {
                    used[index] = true;
                    anchors.Add(new ColumnAnchor(column, CenterX(row[index])));
                    break;
                }
            }
        }

        return (anchors, templateMatches);
    }

    /// <summary>Columnas de la plantilla en orden de prioridad; null cuando no hay plantilla.</summary>
    private static IEnumerable<(InvoiceColumn Column, string? TemplateName)> GetTemplateColumns(
        SupplierColumnMappingDto? template)
    {
        if (template is null)
        {
            yield break;
        }

        yield return (InvoiceColumn.Name, template.NameColumnName);
        yield return (InvoiceColumn.Quantity, template.QuantityColumnName);
        yield return (InvoiceColumn.UnitCost, template.UnitCostColumnName);
        yield return (InvoiceColumn.SupplierCode, template.SupplierCodeColumnName);
        yield return (InvoiceColumn.Barcode, template.BarcodeColumnName);
    }

    /// <summary>
    /// Busca la primera secuencia consecutiva de palabras (no consumidas) cuyo texto normalizado
    /// concatenado iguala el nombre de plantilla normalizado, probando la secuencia más larga.
    /// </summary>
    private static (int Start, int Length)? FindTemplateSequence(
        IReadOnlyList<OcrWord> row,
        bool[] used,
        string normalizedTemplate)
    {
        for (var start = 0; start < row.Count; start++)
        {
            var maxLength = Math.Min(row.Count - start, normalizedTemplate.Length);
            for (var length = maxLength; length >= 1; length--)
            {
                if (IsRangeUsed(used, start, length))
                {
                    continue;
                }

                var candidate = string.Concat(
                    Enumerable.Range(0, length).Select(offset => Normalize(row[start + offset].Text)));
                if (string.Equals(candidate, normalizedTemplate, StringComparison.Ordinal))
                {
                    return (start, length);
                }
            }
        }

        return null;
    }

    /// <summary>Construye las bandas X (abiertas en los extremos) desde los midpoints de los anchors.</summary>
    private static List<Band> BuildBands(IReadOnlyList<ColumnAnchor> anchors)
    {
        var sorted = anchors.OrderBy(anchor => anchor.X).ToList();
        var bands = new List<Band>(sorted.Count);
        for (var index = 0; index < sorted.Count; index++)
        {
            var upper = index == sorted.Count - 1
                ? double.PositiveInfinity
                : (sorted[index].X + sorted[index + 1].X) / 2;
            bands.Add(new Band(sorted[index].Column, upper));
        }

        return bands;
    }

    /// <summary>Proyecta una fila a línea; null si ningún bucket mapeado recibió palabras.</summary>
    private static OcrExtractedLineDto? MapRow(IReadOnlyList<OcrWord> row, IReadOnlyList<Band> bands)
    {
        if (row.Count == 0)
        {
            return null;
        }

        var buckets = new Dictionary<InvoiceColumn, List<OcrWord>>();
        foreach (var word in row)
        {
            var column = FindBand(word, bands);
            if (!buckets.TryGetValue(column, out var bucket))
            {
                bucket = [];
                buckets[column] = bucket;
            }

            bucket.Add(word);
        }

        if (buckets.Count == 0)
        {
            return null;
        }

        var name = BuildText(buckets, InvoiceColumn.Name, " ");
        var supplierCode = BuildText(buckets, InvoiceColumn.SupplierCode, string.Empty);
        var barcode = BuildText(buckets, InvoiceColumn.Barcode, string.Empty);
        var quantity = BuildNumber(buckets, InvoiceColumn.Quantity);
        var unitCost = BuildNumber(buckets, InvoiceColumn.UnitCost);

        return new OcrExtractedLineDto(
            supplierCode.Value,
            barcode.Value,
            name.Value,
            quantity.Value,
            unitCost.Value,
            name.Confidence,
            quantity.Confidence,
            unitCost.Confidence);
    }

    /// <summary>Columna cuya banda contiene el centro X de la palabra (la última banda es abierta).</summary>
    private static InvoiceColumn FindBand(OcrWord word, IReadOnlyList<Band> bands)
    {
        var center = CenterX(word);
        foreach (var band in bands)
        {
            if (center <= band.Upper)
            {
                return band.Column;
            }
        }

        return bands[bands.Count - 1].Column;
    }

    /// <summary>Une las palabras del bucket en orden X (separador configurable) con confianza mínima.</summary>
    private static (string? Value, decimal Confidence) BuildText(
        IReadOnlyDictionary<InvoiceColumn, List<OcrWord>> buckets,
        InvoiceColumn column,
        string separator)
    {
        if (!buckets.TryGetValue(column, out var words) || words.Count == 0)
        {
            return (null, OcrConfidence.Unresolved);
        }

        var text = string.Join(separator, words.Select(word => word.Text)).Trim();
        return string.IsNullOrWhiteSpace(text)
            ? (null, OcrConfidence.Unresolved)
            : (text, MinConfidence(words));
    }

    /// <summary>
    /// Une las palabras del bucket numérico y las parsea con las reglas tolerantes espejo de la
    /// ruta tabular; si no parsean, valor null y confianza no resuelta.
    /// </summary>
    private static (decimal? Value, decimal Confidence) BuildNumber(
        IReadOnlyDictionary<InvoiceColumn, List<OcrWord>> buckets,
        InvoiceColumn column)
    {
        if (!buckets.TryGetValue(column, out var words) || words.Count == 0)
        {
            return (null, OcrConfidence.Unresolved);
        }

        var text = string.Join(' ', words.Select(word => word.Text));
        return TryParseDecimal(text, out var value)
            ? (value, MinConfidence(words))
            : (null, OcrConfidence.Unresolved);
    }

    /// <summary>Mínimo de las confianzas contribuyentes, acotado a 0–100 y redondeado a 1 decimal.</summary>
    private static decimal MinConfidence(IReadOnlyList<OcrWord> words)
    {
        var minimum = words.Min(word => word.Confidence);
        var clamped = Math.Clamp((decimal)minimum, 0m, 100m);
        return Math.Round(clamped, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// 8.147-T3: espejo exacto de <c>TryParseDecimal</c> de
    /// <c>Desktop.Client.Core/Services/SupplierInvoiceService.Parsing.cs</c> para que la ruta OCR
    /// y la tabular interpreten los números igual: símbolos de moneda y espacios fuera; con coma y
    /// punto presentes el último es el separador decimal; coma sola = decimal; punto solo =
    /// decimal InvariantCulture (p. ej. "4.250" → 4.25).
    /// </summary>
    private static bool TryParseDecimal(string? value, out decimal result)
    {
        result = 0m;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim()
            .Replace("$", string.Empty, StringComparison.Ordinal)
            .Replace("€", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
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

        return decimal.TryParse(
            normalized,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out result);
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

    private enum InvoiceColumn
    {
        Name,
        Quantity,
        UnitCost,
        SupplierCode,
        Barcode
    }

    /// <summary>Columna anclada por un encabezado, con el centro X de su palabra o secuencia.</summary>
    private sealed record ColumnAnchor(InvoiceColumn Column, double X);

    /// <summary>Banda de una columna: recibe las palabras con centro X ≤ <see cref="Upper"/>.</summary>
    private sealed record Band(InvoiceColumn Column, double Upper);

    /// <summary>Fila elegida como encabezado y sus anchors.</summary>
    private sealed record HeaderMatch(int RowIndex, List<ColumnAnchor> Anchors, int TemplateMatches);
}
