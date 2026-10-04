using System;
using System.Linq;
using System.Threading.Tasks;
using Core.Interfaces;
using Inventory.Module.Services.Ocr;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.147-T2/D15: único test que ejercita los nativos reales (Tesseract + leptonica + tessdata
/// vendido). Sin <c>TEST_OCR_NATIVE=1</c> retorna silenciosamente para que la suite permanezca
/// determinista y libre de nativos; CI la habilita en el step de tests (windows-2025).
/// </summary>
public class OcrNativeEngineSmokeTests
{
    private readonly ITestOutputHelper _output;

    public OcrNativeEngineSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task TesseractOcrEngine_RecognizesSyntheticInvoiceText_WhenNativeSmokeEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TEST_OCR_NATIVE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        using var canvas = new Mat(320, 900, MatType.CV_8UC3, Scalar.White);
        Cv2.PutText(canvas, "FACTURA PROVEEDOR", new Point(30, 110), HersheyFonts.HersheySimplex, 2.0, Scalar.Black, 5);
        Cv2.PutText(canvas, "TOTAL 12345", new Point(30, 220), HersheyFonts.HersheySimplex, 2.0, Scalar.Black, 5);

        var preprocessor = new ImagePreprocessor();
        using var processed = preprocessor.Preprocess(canvas);
        var png = preprocessor.EncodePng(processed);

        using var engine = new TesseractOcrEngine();
        var page = await engine.RecognizeAsync(new OcrImage(png, processed.Width, processed.Height));

        var recognized = string.Join(" | ", page.Words.Select(word => word.Text));
        _output.WriteLine($"tessdata={engine.DataPath}");
        _output.WriteLine($"words={page.Words.Count}: {recognized}");

        Assert.NotEmpty(page.Words);
        Assert.Contains(page.Words, word => word.Text.Contains("12345", StringComparison.OrdinalIgnoreCase));
    }
}
