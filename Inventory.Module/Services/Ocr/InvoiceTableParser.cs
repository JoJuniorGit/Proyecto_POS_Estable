using Core.Common;
using Core.DTOs;
using Core.Interfaces;

namespace Inventory.Module.Services.Ocr;

/// <summary>
/// 8.147-T3/D6/D7/D8: parser heurístico de word-boxes OCR a filas de factura. Puro (sin I/O ni
/// WPF): agrupa palabras por línea (tolerancia 0.6× la altura mediana), ancla columnas en la fila
/// de encabezado (nombres de la plantilla del proveedor primero; keywords genéricas como
/// fallback), asigna cada palabra a la banda X de su columna y proyecta
/// <see cref="OcrExtractedLineDto"/> con confianza por campo (mínimo de las palabras que lo
/// componen, 1 decimal; campo no resuelto = <c>OcrConfidence.Unresolved</c>).
/// </summary>
public sealed partial class InvoiceTableParser
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

    /// <summary>
    /// 8.163: palabras normalizadas que marcan el fin de la tabla de ítems en el pie fiscal
    /// (Sub-Total, TOTAL, Nota legal, GRACIAS, SALDO, I.V.A, FORMA de pago, TASA de cambio).
    /// La comparación es por igualdad completa: un producto como "TOTALCARE" no corta la lectura.
    /// </summary>
    private static readonly HashSet<string> FooterTerminators =
        ["subtotal", "total", "nota", "gracias", "saldo", "iva", "forma", "tasa"];

    /// <summary>Parsea páginas OCR (en orden) a filas de factura.</summary>
    /// <param name="pages">Páginas reconocidas, cada una con sus word-boxes.</param>
    /// <param name="template">Plantilla de columnas del proveedor; null usa solo keywords genéricas.</param>
    /// <param name="ocrTemplate">Plantilla avanzada de escáner OCR del proveedor (anclas, posiciones, reglas numéricas y de texto).</param>
    public IReadOnlyList<OcrExtractedLineDto> Parse(
        IReadOnlyList<OcrPage> pages,
        SupplierColumnMappingDto? template,
        SupplierOcrTemplateDto? ocrTemplate = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var lines = new List<OcrExtractedLineDto>();
        List<ColumnAnchor>? anchors = null;
        var hasPositionMapping = HasPositionMapping(ocrTemplate);
        var customHeaderKeywords = ParseKeywordList(ocrTemplate?.HeaderAnchorKeywords);
        var customFooterKeywords = ParseKeywordList(ocrTemplate?.FooterAnchorKeywords);

        foreach (var page in pages)
        {
            var rows = ClusterRows(page.Words);
            if (rows.Count == 0)
            {
                continue;
            }

            var header = FindHeader(rows, template, customHeaderKeywords, hasPositionMapping);
            List<List<OcrWord>> dataRows;

            if (header is not null)
            {
                if (header.Anchors.Count >= MinimumHeaderColumns)
                {
                    anchors = header.Anchors;
                }

                // Las filas sobre el encabezado (RIF, datos del proveedor) no son filas de factura.
                dataRows = rows.Skip(header.RowIndex + 1).ToList();
            }
            else if (hasPositionMapping)
            {
                dataRows = rows;
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

            var bands = anchors is { Count: >= MinimumHeaderColumns } ? BuildBands(anchors) : null;
            var medianHeight = ComputeMedianWordHeight(page.Words);

            foreach (var row in dataRows)
            {
                // 8.163: el pie fiscal (totales, nota legal, formas de pago) no son ítems; al
                // detectar una palabra marcadora se deja de leer la página.
                if (IsFooterRow(row, customFooterKeywords))
                {
                    break;
                }

                OcrExtractedLineDto? line;
                if (hasPositionMapping)
                {
                    if (IsLikelyHeaderRow(row, template, customHeaderKeywords))
                    {
                        continue;
                    }

                    line = MapRowByPosition(row, ocrTemplate!, medianHeight);
                }
                else if (bands is not null)
                {
                    line = MapRow(row, bands, ocrTemplate);
                }
                else
                {
                    continue;
                }

                if (line is not null)
                {
                    lines.Add(line);
                }
            }
        }

        return lines;
    }

    private static bool HasPositionMapping(SupplierOcrTemplateDto? ocrTemplate) =>
        ocrTemplate is not null &&
        (ocrTemplate.ProductColumnPosition is >= 1 ||
         ocrTemplate.QuantityColumnPosition is >= 1 ||
         ocrTemplate.UnitCostColumnPosition is >= 1 ||
         ocrTemplate.SupplierCodeColumnPosition is >= 1);

    private static bool IsFooterRow(IReadOnlyList<OcrWord> row, IReadOnlyList<string> customFooterKeywords)
    {
        if (row.Any(word => FooterTerminators.Contains(Normalize(word.Text))))
        {
            return true;
        }

        if (customFooterKeywords.Count == 0)
        {
            return false;
        }

        return MatchesAnyCustomKeyword(row, customFooterKeywords);
    }

    /// <summary>Elige como encabezado la fila con más columnas reconocidas (mínimo 2) o anclada por keywords del usuario.</summary>
    private static HeaderMatch? FindHeader(
        List<List<OcrWord>> rows,
        SupplierColumnMappingDto? template,
        IReadOnlyList<string> customHeaderKeywords,
        bool hasPositionMapping)
    {
        HeaderMatch? best = null;
        for (var index = 0; index < rows.Count; index++)
        {
            var (anchors, templateMatches) = MatchHeaderRow(rows[index], template);
            var customHits = CountCustomKeywordMatches(rows[index], customHeaderKeywords);

            if (customHits > 0)
            {
                templateMatches += customHits * 10;
            }

            if (anchors.Count < MinimumHeaderColumns)
            {
                if (customHits > 0 && hasPositionMapping)
                {
                    return new HeaderMatch(index, anchors, templateMatches);
                }

                if (customHits >= MinimumHeaderColumns)
                {
                    var synthesized = SynthesizeAnchorsFromRow(rows[index]);
                    if (synthesized.Count >= MinimumHeaderColumns)
                    {
                        anchors = synthesized;
                    }
                }

                if (anchors.Count < MinimumHeaderColumns)
                {
                    continue;
                }
            }

            // Si hay keywords de encabezado personalizadas configuradas y alguna fila las cumple,
            // se priorizan las filas que coinciden con dichas keywords.
            if (best is null ||
                (customHits > 0 && best.TemplateMatches < 10) ||
                ((customHits > 0 || best.TemplateMatches < 10) &&
                 (anchors.Count > best.Anchors.Count ||
                  (anchors.Count == best.Anchors.Count && templateMatches > best.TemplateMatches))))
            {
                best = new HeaderMatch(index, anchors, templateMatches);
            }
        }

        return best;
    }

    private static List<ColumnAnchor> SynthesizeAnchorsFromRow(IReadOnlyList<OcrWord> row)
    {
        var anchors = new List<ColumnAnchor>();
        var defaultOrder = new[]
        {
            InvoiceColumn.Name,
            InvoiceColumn.Quantity,
            InvoiceColumn.UnitCost,
            InvoiceColumn.SupplierCode,
            InvoiceColumn.Barcode
        };

        for (var i = 0; i < Math.Min(row.Count, defaultOrder.Length); i++)
        {
            anchors.Add(new ColumnAnchor(defaultOrder[i], CenterX(row[i])));
        }

        return anchors;
    }

    private static bool IsLikelyHeaderRow(
        IReadOnlyList<OcrWord> row,
        SupplierColumnMappingDto? template,
        IReadOnlyList<string> customHeaderKeywords)
    {
        if (row.Any(word => TryParseDecimal(word.Text, out _)))
        {
            return false;
        }

        if (customHeaderKeywords.Count > 0 && MatchesAnyCustomKeyword(row, customHeaderKeywords))
        {
            return true;
        }

        var (anchors, _) = MatchHeaderRow(row, template);
        return anchors.Count >= MinimumHeaderColumns;
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
            bands.Add(new Band(sorted[index].Column, upper, sorted[index].X));
        }

        return bands;
    }

    /// <summary>Proyecta una fila a línea; null si no aporta identificador ni valores numéricos (8.163).</summary>
    private static OcrExtractedLineDto? MapRow(
        IReadOnlyList<OcrWord> row,
        IReadOnlyList<Band> bands,
        SupplierOcrTemplateDto? ocrTemplate = null)
    {
        var effectiveRow = ApplyReplacementsToRow(row, ocrTemplate?.CharacterReplacements);
        if (effectiveRow.Count == 0)
        {
            return null;
        }

        var buckets = new Dictionary<InvoiceColumn, List<OcrWord>>();
        foreach (var word in effectiveRow)
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

        var separatorRule = ocrTemplate?.DecimalSeparator ?? OcrDecimalSeparatorRule.Auto;
        var name = BuildText(buckets, InvoiceColumn.Name, " ");
        var supplierCode = BuildText(buckets, InvoiceColumn.SupplierCode, string.Empty);
        var barcode = BuildText(buckets, InvoiceColumn.Barcode, string.Empty);
        var quantity = BuildNumber(buckets, bands, InvoiceColumn.Quantity, separatorRule);
        var unitCost = BuildNumber(buckets, bands, InvoiceColumn.UnitCost, separatorRule);

        return CreateExtractedLine(name, supplierCode, barcode, quantity, unitCost, ocrTemplate);
    }

    /// <summary>
    /// Proyecta una fila según las posiciones horizontales 1-based configuradas en la plantilla OCR.
    /// </summary>
    private static OcrExtractedLineDto? MapRowByPosition(
        IReadOnlyList<OcrWord> row,
        SupplierOcrTemplateDto ocrTemplate,
        double medianHeight)
    {
        var effectiveRow = ApplyReplacementsToRow(row, ocrTemplate.CharacterReplacements);
        if (effectiveRow.Count == 0)
        {
            return null;
        }

        var maxPos = Math.Max(
            Math.Max(ocrTemplate.ProductColumnPosition ?? 0, ocrTemplate.QuantityColumnPosition ?? 0),
            Math.Max(ocrTemplate.UnitCostColumnPosition ?? 0, ocrTemplate.SupplierCodeColumnPosition ?? 0));

        var cells = SegmentRowIntoCells(effectiveRow, maxPos, ocrTemplate.ProductColumnPosition, medianHeight);
        if (cells.Count == 0)
        {
            return null;
        }

        var separatorRule = ocrTemplate.DecimalSeparator;
        var name = ExtractTextCell(cells, ocrTemplate.ProductColumnPosition, " ");
        var supplierCode = ExtractTextCell(cells, ocrTemplate.SupplierCodeColumnPosition, string.Empty);
        var barcode = (Value: (string?)null, Confidence: OcrConfidence.Unresolved);
        var quantity = ExtractNumberCell(cells, ocrTemplate.QuantityColumnPosition, separatorRule);
        var unitCost = ExtractNumberCell(cells, ocrTemplate.UnitCostColumnPosition, separatorRule);

        return CreateExtractedLine(name, supplierCode, barcode, quantity, unitCost, ocrTemplate);
    }

    private static IReadOnlyList<OcrWord> ApplyReplacementsToRow(
        IReadOnlyList<OcrWord> row,
        IReadOnlyList<OcrCharacterReplacementRuleDto>? replacements)
    {
        if (replacements is not { Count: > 0 })
        {
            return row;
        }

        var result = new List<OcrWord>(row.Count);
        foreach (var word in row)
        {
            var replaced = ApplyCharacterReplacements(word.Text, replacements);
            if (!string.IsNullOrWhiteSpace(replaced))
            {
                result.Add(word with { Text = replaced });
            }
        }

        return result;
    }

    private static OcrExtractedLineDto? CreateExtractedLine(
        (string? Value, decimal Confidence) name,
        (string? Value, decimal Confidence) supplierCode,
        (string? Value, decimal Confidence) barcode,
        (decimal? Value, decimal Confidence) quantity,
        (decimal? Value, decimal Confidence) unitCost,
        SupplierOcrTemplateDto? ocrTemplate)
    {
        // 8.163/8.170: sin identificador legible Y sin cantidad la fila no aporta nada (costos
        // sueltos del pie, rayas manuscritas, perforaciones): no se emite para no bloquear el
        // flujo con filas fantasma.
        var sanitizedName = OcrConfidence.SanitizeProductName(name.Value);
        if (sanitizedName is not null && ocrTemplate?.CharacterReplacements is { Count: > 0 } replacements)
        {
            sanitizedName = ApplyCharacterReplacements(sanitizedName, replacements);
        }

        var hasIdentifier = sanitizedName is not null
            || supplierCode.Value is not null
            || barcode.Value is not null;
        if (!hasIdentifier && quantity.Value is null)
        {
            return null;
        }

        var effectiveUnitCost = unitCost.Value;
        if (ocrTemplate?.CostAmountRule == OcrCostAmountRule.LineTotal
            && effectiveUnitCost is decimal totalCost
            && quantity.Value is decimal qty
            && qty > 0m)
        {
            effectiveUnitCost = Math.Round(totalCost / qty, 4, MidpointRounding.AwayFromZero);
        }

        var extractedFactor = TryExtractConversionFactor(sanitizedName ?? name.Value, ocrTemplate?.FactorExtractionPattern);

        return new OcrExtractedLineDto(
            supplierCode.Value,
            barcode.Value,
            sanitizedName,
            quantity.Value,
            effectiveUnitCost,
            name.Confidence,
            quantity.Confidence,
            unitCost.Confidence,
            extractedFactor);
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
    /// 8.162: resuelve el valor numérico del bucket. Primero intenta el parseo del bucket completo
    /// (comportamiento histórico 8.147: une números partidos en varias palabras, p. ej. "$" +
    /// "4.250"); si el bucket quedó sucio con texto de columnas sin anclar (%, subtotales, rayas
    /// manuscritas), elige la palabra parseable más cercana en X al ancla de la columna. Sin
    /// candidatos: valor null y confianza no resuelta.
    /// </summary>
    private static (decimal? Value, decimal Confidence) BuildNumber(
        IReadOnlyDictionary<InvoiceColumn, List<OcrWord>> buckets,
        IReadOnlyList<Band> bands,
        InvoiceColumn column,
        OcrDecimalSeparatorRule separatorRule = OcrDecimalSeparatorRule.Auto)
    {
        if (!buckets.TryGetValue(column, out var words) || words.Count == 0)
        {
            return (null, OcrConfidence.Unresolved);
        }

        var text = string.Join(' ', words.Select(word => word.Text));
        if (TryParseDecimalWithRule(text, separatorRule, out var value))
        {
            return (value, MinConfidence(words));
        }

        var anchorX = bands.First(band => band.Column == column).AnchorX;
        OcrWord? nearest = null;
        var nearestDistance = double.MaxValue;
        decimal nearestValue = 0m;
        foreach (var word in words)
        {
            if (!TryParseDecimalWithRule(word.Text, separatorRule, out var wordValue))
            {
                continue;
            }

            var distance = Math.Abs(CenterX(word) - anchorX);
            if (nearest is null ||
                distance < nearestDistance ||
                (distance == nearestDistance && CenterX(word) < CenterX(nearest)))
            {
                nearest = word;
                nearestDistance = distance;
                nearestValue = wordValue;
            }
        }

        return nearest is null
            ? (null, OcrConfidence.Unresolved)
            : (nearestValue, MinConfidence([nearest]));
    }

    /// <summary>Mínimo de las confianzas contribuyentes, acotado a 0–100 y redondeado a 1 decimal.</summary>
    private static decimal MinConfidence(IReadOnlyList<OcrWord> words)
    {
        var minimum = words.Min(word => word.Confidence);
        var clamped = Math.Clamp((decimal)minimum, 0m, 100m);
        return Math.Round(clamped, 1, MidpointRounding.AwayFromZero);
    }

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
    private sealed record Band(InvoiceColumn Column, double Upper, double AnchorX);

    /// <summary>Fila elegida como encabezado y sus anchors.</summary>
    private sealed record HeaderMatch(int RowIndex, List<ColumnAnchor> Anchors, int TemplateMatches);
}
