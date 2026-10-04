using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Inventory.Module.Services.Ocr;
using OpenCvSharp;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class OcrPageDecoderTests
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47 };

    private readonly ImagePreprocessor _preprocessor = new();

    private DocumentPageDecoder CreateDocumentDecoder(int pdfDpi = 72)
    {
        return new DocumentPageDecoder(new ImagePageDecoder(_preprocessor), new PdfPageDecoder(pdfDpi));
    }

    [Fact]
    public void DecodePages_PdfWithThreePages_ReturnsThreePngPages()
    {
        var decoder = CreateDocumentDecoder();

        var pages = decoder.DecodePages(CreatePdf(3), ".pdf");

        Assert.Equal(3, pages.Count);
        Assert.All(pages, AssertPngPageWithPixels);
    }

    [Fact]
    public void DecodePages_PdfWithFivePages_ReturnsFivePngPages()
    {
        var decoder = CreateDocumentDecoder();

        var pages = decoder.DecodePages(CreatePdf(PdfPageDecoder.MaxPages), ".pdf");

        Assert.Equal(PdfPageDecoder.MaxPages, pages.Count);
        Assert.All(pages, AssertPngPageWithPixels);
    }

    [Fact]
    public void DecodePages_PdfWithSixPages_ThrowsArgumentException()
    {
        var decoder = CreateDocumentDecoder();

        var exception = Assert.Throws<ArgumentException>(
            () => decoder.DecodePages(CreatePdf(PdfPageDecoder.MaxPages + 1), ".pdf"));

        Assert.Contains("5", exception.Message);
    }

    [Fact]
    public void DecodePages_PngImage_ReturnsSingleNormalizedPage()
    {
        var decoder = CreateDocumentDecoder();
        var png = CreateImagePng(120, 80);

        var pages = decoder.DecodePages(png, ".png");

        var page = Assert.Single(pages);
        Assert.Equal(120, page.PixelWidth);
        Assert.Equal(80, page.PixelHeight);
        AssertPngSignature(page);
    }

    [Fact]
    public void DecodePages_UnsupportedExtension_ThrowsArgumentException()
    {
        var decoder = CreateDocumentDecoder();

        Assert.Throws<ArgumentException>(() => decoder.DecodePages(CreatePdf(1), ".docx"));
    }

    [Fact]
    public void DecodePages_EmptyFile_ThrowsArgumentException()
    {
        var decoder = CreateDocumentDecoder();

        Assert.Throws<ArgumentException>(() => decoder.DecodePages(Array.Empty<byte>(), ".pdf"));
    }

    [Fact]
    public void DecodePages_ExtensionWithoutLeadingDotAndUppercase_IsAccepted()
    {
        var decoder = CreateDocumentDecoder();

        var pages = decoder.DecodePages(CreatePdf(1), "PDF");

        Assert.Single(pages);
    }

    [Fact]
    public void ImagePageDecoder_Decode_JpegBytes_ReturnsPngPageWithPixelDimensions()
    {
        var decoder = new ImagePageDecoder(_preprocessor);
        using var source = new Mat(60, 90, MatType.CV_8UC3, new Scalar(200, 200, 200));
        Cv2.Rectangle(source, new Rect(10, 10, 40, 20), Scalar.Black, thickness: -1);
        Assert.True(Cv2.ImEncode(".jpg", source, out var jpeg));

        var page = decoder.Decode(jpeg);

        Assert.Equal(90, page.PixelWidth);
        Assert.Equal(60, page.PixelHeight);
        AssertPngSignature(page);
    }

    [Theory]
    [InlineData(".bmp")]
    [InlineData(".tif")]
    [InlineData(".tiff")]
    [InlineData(".webp")]
    public void DecodePages_AdditionalImageFormats_RoundTripWithPixelDimensions(string extension)
    {
        var decoder = CreateDocumentDecoder();
        using var source = new Mat(70, 110, MatType.CV_8UC3, new Scalar(230, 230, 230));
        Cv2.Rectangle(source, new Rect(10, 10, 50, 25), Scalar.Black, thickness: -1);
        Assert.True(
            Cv2.ImEncode(extension, source, out var encoded),
            $"el runtime de OpenCV no pudo codificar {extension}");

        var pages = decoder.DecodePages(encoded, extension);

        var page = Assert.Single(pages);
        Assert.Equal(110, page.PixelWidth);
        Assert.Equal(70, page.PixelHeight);
        AssertPngSignature(page);
        using var decoded = Cv2.ImDecode(page.EncodedBytes, ImreadModes.Unchanged);
        Assert.Equal(3, decoded.Channels());
    }

    [Fact]
    public void ImagePageDecoder_Decode_GarbageBytes_ThrowsArgumentException()
    {
        var decoder = new ImagePageDecoder(_preprocessor);

        Assert.Throws<ArgumentException>(() => decoder.Decode(new byte[] { 9, 8, 7, 6, 5 }));
    }

    private byte[] CreateImagePng(int width, int height)
    {
        using var image = new Mat(height, width, MatType.CV_8UC3, Scalar.White);
        return _preprocessor.EncodePng(image);
    }

    private static void AssertPngPageWithPixels(Core.Interfaces.OcrImage page)
    {
        AssertPngSignature(page);
        Assert.True(page.PixelWidth > 0, "la página debe tener ancho");
        Assert.True(page.PixelHeight > 0, "la página debe tener alto");
    }

    private static void AssertPngSignature(Core.Interfaces.OcrImage page)
    {
        Assert.True(page.EncodedBytes.Length > PngSignature.Length, "la página debe contener un PNG");
        Assert.Equal(PngSignature, page.EncodedBytes.Take(PngSignature.Length));
    }

    /// <summary>
    /// Genera un PDF mínimo válido (sin librerías externas) con xref correcta para que PDFium
    /// lo renderice. Cada página lleva un texto Helvetica para producir contenido real.
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
