using Core.DTOs;
using Core.Interfaces;
using OpenCvSharp;
using System.Text.RegularExpressions;

namespace Inventory.Module.Services.Ocr;

/// <summary>
/// 8.147-T4/D12: orquesta la extracción OCR de una factura de proveedor: guardas de subida
/// (archivo/tipo/tamaño), decodificación (imagen o PDF ≤5 páginas), preprocesamiento, motor OCR,
/// parser heurístico de word-boxes y pistas best-effort de RIF/nombre. No toca la base de datos
/// ni aplica nada: la revisión y el confirm siguen en el flujo existente.
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

    private readonly DocumentPageDecoder _documentDecoder;
    private readonly ImagePreprocessor _preprocessor;
    private readonly IOcrEngine _ocrEngine;
    private readonly InvoiceTableParser _parser;

    public OcrExtractionService(
        DocumentPageDecoder documentDecoder,
        ImagePreprocessor preprocessor,
        IOcrEngine ocrEngine,
        InvoiceTableParser parser)
    {
        ArgumentNullException.ThrowIfNull(documentDecoder);
        ArgumentNullException.ThrowIfNull(preprocessor);
        ArgumentNullException.ThrowIfNull(ocrEngine);
        ArgumentNullException.ThrowIfNull(parser);
        _documentDecoder = documentDecoder;
        _preprocessor = preprocessor;
        _ocrEngine = ocrEngine;
        _parser = parser;
    }

    public async Task<OcrExtractionResultDto> ExtractAsync(
        OcrExtractionRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fileBytes = Validate(request);

        // Previews: los PNG originales decodificados (S5 "imagen original escaneada"), en orden
        // de página y antes del preprocesamiento; el motor OCR recibe la versión procesada.
        var pages = _documentDecoder.DecodePages(fileBytes, Path.GetExtension(request.FileName));
        var previews = pages
            .Select(page => Convert.ToBase64String(page.EncodedBytes))
            .ToArray();

        var ocrPages = new List<OcrPage>(pages.Count);
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var decoded = Cv2.ImDecode(page.EncodedBytes, ImreadModes.Color);
            if (decoded.Empty())
            {
                throw new ArgumentException("No se pudo decodificar una página del documento para el OCR.");
            }

            using var processed = _preprocessor.Preprocess(decoded);
            var processedPng = _preprocessor.EncodePng(processed);
            var image = new OcrImage(processedPng, processed.Width, processed.Height);
            ocrPages.Add(await _ocrEngine.RecognizeAsync(image, cancellationToken).ConfigureAwait(false));
        }

        return new OcrExtractionResultDto(
            _parser.Parse(ocrPages, request.ColumnMapping),
            previews,
            DetectRif(ocrPages),
            DetectSupplierName(ocrPages));
    }

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
