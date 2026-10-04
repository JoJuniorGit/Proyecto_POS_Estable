using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Interfaces;
using Inventory.Module.Services.Ocr;
using OpenCvSharp;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.147-T4/D12: pruebas del orquestador de extracción OCR con motor stub (sin nativos de
/// Tesseract) y decodificadores/preprocesador reales. Cubren happy path de imagen y PDF,
/// previews en orden de página, pistas RIF/nombre, fallback genérico vs. plantilla y guardas
/// de subida con sus mensajes exactos.
/// </summary>
public class OcrExtractionServiceTests
{
    private const double DefaultHeight = 10;
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47 };

    private readonly ImagePreprocessor _preprocessor = new();

    private static OcrWord Word(
        string text,
        double x,
        double y,
        double confidence = 90,
        double width = 40,
        double height = DefaultHeight)
        => new(text, x, y, width, height, confidence);

    private static OcrPage Page(params OcrWord[] words) => new(words);

    private static OcrWord[] GenericHeader(double y = 100) =>
    [
        Word("Descripción", 40, y),
        Word("Cantidad", 200, y),
        Word("Precio", 340, y)
    ];

    private static OcrWord[] DataRow(string name, string quantity, string cost, double y) =>
    [
        Word(name, 40, y),
        Word(quantity, 200, y),
        Word(cost, 340, y)
    ];

    [Fact]
    public async Task ExtractAsync_Image_ReturnsLinesWithConfidencesPreviewsAndHints()
    {
        var engine = new StubOcrEngine(_ => Page([
            Word("COMERCIAL", 20, 5),
            Word("ESQUINA", 160, 5),
            Word("J-12345678-9", 320, 5),
            .. GenericHeader(),
            Word("Café", 40, 130, confidence: 88.25),
            Word("2", 200, 130, confidence: 95),
            Word("5,50", 340, 130, confidence: 70.44)
        ]));
        var service = CreateService(engine);

        var result = await service.ExtractAsync(new OcrExtractionRequestDto(CreatePng(), "factura.png", 7, null));

        var line = Assert.Single(result.Lines);
        Assert.Equal("Café", line.Name);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(5.50m, line.UnitCost);
        Assert.Equal(88.3m, line.NameConfidence);          // 88.25 → 88.3 (AwayFromZero)
        Assert.Equal(95.0m, line.QuantityConfidence);
        Assert.Equal(70.4m, line.UnitCostConfidence);      // 70.44 → 70.4
        Assert.Equal("J-12345678-9", result.DetectedRif);
        Assert.Equal("COMERCIAL ESQUINA", result.DetectedSupplierName);

        // Preview = PNG original decodificado (3 canales), antes del preprocesamiento (1 canal).
        var preview = Assert.Single(result.PreviewPagesBase64);
        using var decoded = DecodePreview(preview);
        Assert.Equal(3, decoded.Channels());
    }

    [Fact]
    public async Task ExtractAsync_ImageWithoutMapping_UsesGenericKeywordsPath()
    {
        // Sin mapping, el encabezado genérico (Descripción/Cantidad/Precio) resuelve columnas.
        var engine = new StubOcrEngine(_ => Page([.. GenericHeader(), .. DataRow("Harina", "4", "1,25", 130)]));
        var service = CreateService(engine);

        var result = await service.ExtractAsync(new OcrExtractionRequestDto(CreatePng(), "factura.png", null, null));

        var line = Assert.Single(result.Lines);
        Assert.Equal("Harina", line.Name);
        Assert.Equal(4m, line.Quantity);
        Assert.Equal(1.25m, line.UnitCost);
    }

    [Fact]
    public async Task ExtractAsync_WithColumnMapping_ResolvesTemplateKeywordColumns()
    {
        // "Concepto/Bultos/Monto" no matchean keywords genéricas: solo la plantilla enviada por la
        // petición puede anclar estas columnas (prueba que el mapping llega al parser).
        var engine = new StubOcrEngine(_ => Page(
            Word("Concepto", 40, 100),
            Word("Bultos", 200, 100),
            Word("Monto", 340, 100),
            Word("Harina", 40, 130),
            Word("3", 200, 130),
            Word("10,00", 340, 130)));
        var service = CreateService(engine);
        var mapping = new SupplierColumnMappingDto(null, null, "Concepto", "Bultos", "Monto");

        var result = await service.ExtractAsync(new OcrExtractionRequestDto(CreatePng(), "factura.png", 7, mapping));

        var line = Assert.Single(result.Lines);
        Assert.Equal("Harina", line.Name);
        Assert.Equal(3m, line.Quantity);
        Assert.Equal(10m, line.UnitCost);
    }

    [Fact]
    public async Task ExtractAsync_Pdf_ReturnsLinesAndPreviewsInPageOrder()
    {
        var pdf = CreatePdf(3);
        var engine = new StubOcrEngine(call => call switch
        {
            0 => Page([.. GenericHeader(), .. DataRow("Uno", "1", "1,10", 130)]),
            // Página de continuación sin encabezado: hereda los anchors de la página anterior.
            1 => Page(
                Word("Dos", 40, 50),
                Word("5", 200, 50),
                Word("1,10", 340, 50)),
            _ => Page([.. GenericHeader(), .. DataRow("Tres", "3", "3,30", 130)])
        });
        var service = CreateService(engine);

        var result = await service.ExtractAsync(new OcrExtractionRequestDto(pdf, "factura.pdf", 12, null));

        Assert.Equal(3, result.Lines.Count);
        Assert.Equal("Uno", result.Lines[0].Name);
        Assert.Equal("Dos", result.Lines[1].Name);
        Assert.Equal("Tres", result.Lines[2].Name);
        Assert.Equal(3, result.PreviewPagesBase64.Count);

        // Cada preview es el PNG decodificado de su página, en orden y sin preprocesar.
        var decoder = new DocumentPageDecoder(new ImagePageDecoder(_preprocessor), new PdfPageDecoder(72));
        var decodedPages = decoder.DecodePages(pdf, ".pdf");
        Assert.Equal(decodedPages.Count, result.PreviewPagesBase64.Count);
        for (var index = 0; index < decodedPages.Count; index++)
        {
            Assert.Equal(Convert.ToBase64String(decodedPages[index].EncodedBytes), result.PreviewPagesBase64[index]);
        }
    }

    [Fact]
    public async Task ExtractAsync_WithoutRifOrAlphabeticRows_ReturnsNullHints()
    {
        var engine = new StubOcrEngine(_ => Page(
            Word("123", 40, 100),
            Word("456", 200, 100),
            Word("789", 340, 100)));
        var service = CreateService(engine);

        var result = await service.ExtractAsync(new OcrExtractionRequestDto(CreatePng(), "factura.png", null, null));

        Assert.Empty(result.Lines);
        Assert.Null(result.DetectedRif);
        Assert.Null(result.DetectedSupplierName);
    }

    [Fact]
    public async Task ExtractAsync_MissingFileName_ThrowsWithExactMessage()
    {
        var service = CreateService(new StubOcrEngine(_ => Page()));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExtractAsync(new OcrExtractionRequestDto(CreatePng(), "   ", null, null)));

        Assert.Equal("El nombre de archivo es obligatorio.", exception.Message);
    }

    [Fact]
    public async Task ExtractAsync_EmptyFile_ThrowsWithExactMessage()
    {
        var service = CreateService(new StubOcrEngine(_ => Page()));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExtractAsync(new OcrExtractionRequestDto(Array.Empty<byte>(), "factura.png", null, null)));

        Assert.Equal("El archivo está vacío.", exception.Message);
    }

    [Fact]
    public async Task ExtractAsync_UnsupportedExtension_ThrowsWithExactMessage()
    {
        var service = CreateService(new StubOcrEngine(_ => Page()));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExtractAsync(new OcrExtractionRequestDto(new byte[] { 1, 2, 3 }, "factura.docx", null, null)));

        Assert.Equal("La extensión '.docx' no está soportada; use png, jpg, jpeg o pdf.", exception.Message);
    }

    [Fact]
    public async Task ExtractAsync_OversizedFile_ThrowsWithExactMessage()
    {
        var service = CreateService(new StubOcrEngine(_ => Page()));
        var oversized = new byte[(int)OcrExtractionService.MaxFileBytes + 1];

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExtractAsync(new OcrExtractionRequestDto(oversized, "factura.png", null, null)));

        Assert.Equal("El archivo supera el tamaño máximo permitido de 20 MB.", exception.Message);
    }

    [Fact]
    public void ServiceConstructor_DoesNotDependOnPersistence()
    {
        var parameterTypes = typeof(OcrExtractionService)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType.Name)
            .ToArray();

        Assert.DoesNotContain(parameterTypes, name => name.Contains("DbContext", StringComparison.Ordinal));
    }

    private OcrExtractionService CreateService(IOcrEngine engine)
    {
        var documentDecoder = new DocumentPageDecoder(new ImagePageDecoder(_preprocessor), new PdfPageDecoder(72));
        return new OcrExtractionService(documentDecoder, _preprocessor, engine, new InvoiceTableParser());
    }

    private byte[] CreatePng(int width = 120, int height = 60)
    {
        using var image = new Mat(height, width, MatType.CV_8UC3, Scalar.White);
        Cv2.Rectangle(image, new Rect(10, 10, width / 2, height / 3), Scalar.Black, thickness: -1);
        return _preprocessor.EncodePng(image);
    }

    private static Mat DecodePreview(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        Assert.Equal(PngSignature, bytes.Take(PngSignature.Length));
        var decoded = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
        Assert.False(decoded.Empty(), "la preview debe ser un PNG decodificable");
        return decoded;
    }

    private sealed class StubOcrEngine : IOcrEngine
    {
        private readonly Func<int, OcrPage> _pageFactory;
        private int _calls;

        public StubOcrEngine(Func<int, OcrPage> pageFactory)
        {
            _pageFactory = pageFactory;
        }

        public Task<OcrPage> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(image);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_pageFactory(_calls++));
        }
    }

    /// <summary>
    /// Genera un PDF mínimo válido (sin librerías externas) con xref correcta para que PDFium
    /// lo renderice; mismo helper determinista de OcrPageDecoderTests.
    /// </summary>
    private static byte[] CreatePdf(int pageCount)
    {
        var objects = new List<(int Number, string Body)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, BuildPagesBody(pageCount)),
            (3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
        };

        for (var page = 0; page < pageCount; page++)
        {
            var contentNumber = 4 + (page * 2);
            var pageNumber = contentNumber + 1;
            var content = $"BT /F1 18 Tf 72 720 Td (Invoice page {page + 1}) Tj ET";
            objects.Add((contentNumber, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"));
            objects.Add((pageNumber,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] " +
                $"/Contents {contentNumber} 0 R /Resources << /Font << /F1 3 0 R >> >> >>"));
        }

        objects.Sort((left, right) => left.Number.CompareTo(right.Number));

        using var stream = new MemoryStream();
        WriteAscii(stream, "%PDF-1.4\n");
        var offsets = new Dictionary<int, long>();
        foreach (var (number, body) in objects)
        {
            offsets[number] = stream.Position;
            WriteAscii(stream, $"{number} 0 obj\n{body}\nendobj\n");
        }

        var objectCount = objects.Count;
        var xrefPosition = stream.Position;
        WriteAscii(stream, $"xref\n0 {objectCount + 1}\n");
        WriteAscii(stream, "0000000000 65535 f \n");
        for (var number = 1; number <= objectCount; number++)
        {
            WriteAscii(stream, $"{offsets[number]:D10} 00000 n \n");
        }

        WriteAscii(stream, $"trailer\n<< /Size {objectCount + 1} /Root 1 0 R >>\nstartxref\n{xrefPosition}\n%%EOF\n");
        return stream.ToArray();
    }

    private static string BuildPagesBody(int pageCount)
    {
        var kids = string.Join(' ', Enumerable.Range(0, pageCount).Select(page => $"{5 + (page * 2)} 0 R"));
        return $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>";
    }

    private static void WriteAscii(Stream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }
}
