using Core.DTOs;
using Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Inventory.Module.Services.Ocr;

/// <summary>
/// 8.147-T4/D12: orquesta la extracción OCR de una factura de proveedor: guardas de subida
/// (archivo/tipo/tamaño), decodificación (imagen o PDF ≤5 páginas), preprocesamiento, motor OCR,
/// parser heurístico de word-boxes y pistas best-effort de RIF/nombre/número de factura. No
/// toca la base de datos ni aplica nada: la revisión y el confirm siguen en el flujo existente.
/// </summary>
public sealed class OcrExtractionService : IOcrExtractionService
{
    /// <summary>Tamaño máximo de subida del flujo OCR (S4/S7: 20 MB).</summary>
    public const long MaxFileBytes = 20L * 1024 * 1024;

    /// <summary>Extensiones aceptadas por el flujo OCR (S4), normalizadas a minúsculas.</summary>
    public static readonly IReadOnlyList<string> AllowedExtensions =
        [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".pdf"];

    /// <summary>
    /// 8.147-T10: mensaje exacto de extensión no soportada, compartido con el guard rápido del
    /// controller para que la lista de formatos no derive entre capas.
    /// </summary>
    public static string UnsupportedExtensionMessage(string extension) =>
        $"La extensión '{extension}' no está soportada; use png, jpg, jpeg, webp, bmp, tif o pdf.";

    /// <summary>
    /// 8.147-T4/S7: patrón best-effort de RIF venezolano ([JGVEP] + 8–9 dígitos + dígito de
    /// chequeo opcional). Es solo una pista de prefill: nunca selecciona ni persiste nada.
    /// </summary>
    internal const string RifPattern = @"(?i)\b[JGVEP]-?\d{8,9}-?\d?\b";

    /// <summary>
    /// 8.160-S2/S4: patrón best-effort del número de factura (keyword FACTURA + ventana acotada
    /// de no-dígitos + al menos 4 dígitos con guiones internos; ej. "FACTURA: *0238317" →
    /// "0238317"). Es solo una pista de visualización: nunca selecciona ni persiste nada.
    /// </summary>
    internal const string InvoiceNumberPattern = @"(?i)\bFACTURA\b[^0-9]{0,12}?(\d[\d\-]{3,})";

    /// <summary>
    /// 8.172: patrón best-effort de la tasa de cambio impresa en la factura (ej. "TASA BCV: 78,45",
    /// "TASA DE CAMBIO: 36.50", "FACTOR DE CAMBIO: 42,1250", "TASA: Bs. 54,20"). Excluye alícuotas
    /// de IVA o porcentajes seguidos de '%'.
    /// </summary>
    internal const string ExchangeRatePattern =
        @"(?i)\b(?:TASA(?:\s+(?:DE\s+CAMBIO|CAMBIO|DEL\s+D[IÍ]A|BCV|REF(?:ERENCIAL)?|OFICIAL|USD|D[OÓ]LAR(?:ES)?|\$))?(?!\s*(?:IVA|IMP(?:UESTO)?|AL[IÍ]CUOTA|G\b|R\b|A\b))|(?:FACTOR|TIPO)\s+DE\s+CAMBIO|CAMBIO\s+(?:BCV|DEL\s+D[IÍ]A|OFICIAL|USD|\$))\b[^0-9%\r\n]{0,20}?(\d{1,6}(?:[.,]\d{1,4})?)(?!\s*%)";

    private readonly DocumentPageDecoder _documentDecoder;
    private readonly ImagePreprocessor _preprocessor;
    private readonly IOcrEngine _ocrEngine;
    private readonly InvoiceTableParser _parser;
    private readonly ILogger<OcrExtractionService> _logger;

    public OcrExtractionService(
        DocumentPageDecoder documentDecoder,
        ImagePreprocessor preprocessor,
        IOcrEngine ocrEngine,
        InvoiceTableParser parser,
        ILogger<OcrExtractionService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(documentDecoder);
        ArgumentNullException.ThrowIfNull(preprocessor);
        ArgumentNullException.ThrowIfNull(ocrEngine);
        ArgumentNullException.ThrowIfNull(parser);
        _documentDecoder = documentDecoder;
        _preprocessor = preprocessor;
        _ocrEngine = ocrEngine;
        _parser = parser;
        _logger = logger ?? NullLogger<OcrExtractionService>.Instance;
    }

    public async Task<OcrExtractionResultDto> ExtractAsync(
        OcrExtractionRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fileBytes = Validate(request);
        var extension = Path.GetExtension(request.FileName);

        var pages = _documentDecoder.DecodePages(fileBytes, extension);

        // 8.170: la calidad de la imagen varía. Si el pipeline principal (binarizado automático)
        // no produce NINGUNA fila con identificador se escalan candidatos de respaldo por
        // RESULTADO (raw y luego adaptativo sin deskew), con cortocircuito: en imágenes buenas el
        // costo de OCR no cambia.
        var ocrPages = await RecognizeDocumentAsync(pages, OcrPreprocessingMode.Primary, cancellationToken);
        var lines = _parser.Parse(ocrPages, request.ColumnMapping);
        if (CountUsableLines(lines) == 0)
        {
            foreach (var mode in new[] { OcrPreprocessingMode.Raw, OcrPreprocessingMode.AdaptiveFallback })
            {
                var candidatePages = await RecognizeDocumentAsync(pages, mode, cancellationToken);
                var candidateLines = _parser.Parse(candidatePages, request.ColumnMapping);
                if (CountUsableLines(candidateLines) > CountUsableLines(lines))
                {
                    ocrPages = candidatePages;
                    lines = candidateLines;
                    _logger.LogInformation(
                        "OCR fallback {Mode} selected for '{FileName}': primary pipeline yielded no usable lines.",
                        mode,
                        request.FileName);
                }

                if (CountUsableLines(lines) > 0)
                {
                    break;
                }
            }
        }
        var result = new OcrExtractionResultDto(
            lines,
            DetectRif(ocrPages),
            DetectSupplierName(ocrPages),
            DetectInvoiceNumber(ocrPages),
            DetectExchangeRate(ocrPages));

        _logger.LogInformation(
            "OCR extracted {LineCount} candidate lines across {PageCount} page(s) from '{FileName}' ({FileSizeBytes} bytes).",
            lines.Count,
            pages.Count,
            request.FileName,
            fileBytes.Length);

        return result;
    }

    /// <summary>8.170: modos de preprocesamiento candidatos para la escalera de respaldo.</summary>
    private enum OcrPreprocessingMode
    {
        Primary,
        Raw,
        AdaptiveFallback
    }

    /// <summary>
    /// 8.170: reconoce el documento completo con el modo de preprocesamiento indicado. El parseo
    /// se hace por DOCUMENTO (no por página) para conservar la herencia de anclas entre páginas
    /// de continuación del parser.
    /// </summary>
    private async Task<IReadOnlyList<OcrPage>> RecognizeDocumentAsync(
        IReadOnlyList<OcrImage> pages,
        OcrPreprocessingMode mode,
        CancellationToken cancellationToken)
    {
        var ocrPages = new List<OcrPage>(pages.Count);
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var decoded = Cv2.ImDecode(page.EncodedBytes, ImreadModes.Color);
            if (decoded.Empty())
            {
                throw new ArgumentException("No se pudo decodificar una página del documento para el OCR.");
            }

            using var processed = mode switch
            {
                OcrPreprocessingMode.Raw => null,
                OcrPreprocessingMode.AdaptiveFallback => _preprocessor.PreprocessAdaptiveFallback(decoded),
                _ => _preprocessor.Preprocess(decoded)
            };
            var sourceImage = processed ?? decoded;
            var encodedPng = _preprocessor.EncodePng(sourceImage);
            var image = new OcrImage(encodedPng, sourceImage.Width, sourceImage.Height);
            ocrPages.Add(await _ocrEngine.RecognizeAsync(image, cancellationToken).ConfigureAwait(false));
        }

        return ocrPages;
    }

    /// <summary>Filas con identificador legible (nombre/código/barras): mismo criterio que el cliente.</summary>
    private static int CountUsableLines(IReadOnlyList<OcrExtractedLineDto> lines) =>
        lines.Count(line =>
            !string.IsNullOrWhiteSpace(line.Name) ||
            !string.IsNullOrWhiteSpace(line.SupplierCode) ||
            !string.IsNullOrWhiteSpace(line.Barcode));

    /// <summary>Guardas zero-trust de subida: nombre, contenido, extensión y tamaño (S4/S7).</summary>
    private static byte[] Validate(OcrExtractionRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            throw new ArgumentException("El nombre de archivo es obligatorio.");
        }

        var fileBytes = request.FileBytes;
        ArgumentNullException.ThrowIfNull(fileBytes);
        if (fileBytes.Length == 0)
        {
            throw new ArgumentException("El archivo está vacío.");
        }

        var extension = Path.GetExtension(request.FileName);
        if (!AllowedExtensions.Contains(extension.ToLowerInvariant()))
        {
            throw new ArgumentException(UnsupportedExtensionMessage(extension));
        }

        if (fileBytes.Length > MaxFileBytes)
        {
            throw new ArgumentException(
                $"El archivo supera el tamaño máximo permitido de {MaxFileBytes / (1024 * 1024)} MB.");
        }

        return fileBytes;
    }

    /// <summary>Primer RIF reconocido en las palabras crudas; null si no hay ninguno.</summary>
    private static string? DetectRif(IReadOnlyList<OcrPage> pages)
    {
        var text = string.Join(' ', pages.SelectMany(page => page.Words).Select(word => word.Text));
        var match = Regex.Match(text, RifPattern);
        return match.Success ? match.Value : null;
    }

    /// <summary>Primer número tras la keyword FACTURA; null si no aparece; nunca lanza.</summary>
    private static string? DetectInvoiceNumber(IReadOnlyList<OcrPage> pages)
    {
        var text = string.Join(' ', pages.SelectMany(page => page.Words).Select(word => word.Text));
        var match = Regex.Match(text, InvoiceNumberPattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// 8.172: primera tasa de cambio positiva reconocida en las palabras crudas del documento;
    /// null si no aparece ninguna o no es válida.
    /// </summary>
    private static decimal? DetectExchangeRate(IReadOnlyList<OcrPage> pages)
    {
        var text = string.Join(' ', pages.SelectMany(page => page.Words).Select(word => word.Text));
        foreach (Match match in Regex.Matches(text, ExchangeRatePattern))
        {
            var rawRate = match.Groups[1].Value.Replace(',', '.');
            if (decimal.TryParse(
                    rawRate,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                && parsed > 0m)
            {
                return Math.Round(parsed, 4, MidpointRounding.AwayFromZero);
            }
        }

        return null;
    }

    /// <summary>
    /// 8.147-T4/S7: nombre de proveedor best-effort = primera fila (más alta) de la página 1 con
    /// al menos 2 palabras alfabéticas de largo ≥3, unidas por espacios. Heurística conservadora:
    /// sin candidatos devuelve null y nunca condiciona selección ni persistencia.
    /// </summary>
    private static string? DetectSupplierName(IReadOnlyList<OcrPage> pages)
    {
        if (pages.Count == 0)
        {
            return null;
        }

        var candidates = pages[0].Words.Where(IsAlphabeticWord).ToList();
        if (candidates.Count < 2)
        {
            return null;
        }

        var tolerance = ComputeRowTolerance(candidates);
        var ordered = candidates.OrderBy(CenterY).ThenBy(CenterX).ToList();
        var row = new List<OcrWord> { ordered[0] };
        var centerSum = CenterY(ordered[0]);

        for (var index = 1; index < ordered.Count; index++)
        {
            var word = ordered[index];
            if (Math.Abs(CenterY(word) - (centerSum / row.Count)) <= tolerance)
            {
                row.Add(word);
                centerSum += CenterY(word);
                continue;
            }

            if (row.Count >= 2)
            {
                return JoinRowText(row);
            }

            row = [word];
            centerSum = CenterY(word);
        }

        return row.Count >= 2 ? JoinRowText(row) : null;
    }

    private static bool IsAlphabeticWord(OcrWord word)
    {
        var text = word.Text.Trim();
        return text.Length >= 3 && text.All(char.IsLetter);
    }

    private static string JoinRowText(IReadOnlyList<OcrWord> row) =>
        string.Join(' ', row.Select(word => word.Text.Trim()));

    /// <summary>Misma tolerancia vertical de clustering que el parser (0.6× altura mediana).</summary>
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
        return median * InvoiceTableParser.RowToleranceHeightFactor;
    }

    private static double CenterX(OcrWord word) => word.X + (word.Width / 2);

    private static double CenterY(OcrWord word) => word.Y + (word.Height / 2);
}
