using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Desktop.Client.Services;
using Xunit;
using ZXing;
using ZXing.Common;
using ZXing.Windows.Compatibility;

namespace CommandCenter.Tests;

/// <summary>
/// Verifies the camera scanner's decode pipeline without a webcam: barcodes are generated
/// in memory with ZXing and decoded with the exact reader (BarcodeReaderBitmapSource +
/// BarcodeScannerService.ToBgra32) that BarcodeScannerWindow uses at runtime.
/// </summary>
public class BarcodeScannerDecodeTests
{
    public static TheoryData<string, BarcodeFormat> Barcodes => new()
    {
        // 759... = Venezuela GS1 prefix; 7591001002009 is the checksum-valid form of
        // the plan's example (7591001002003 had a wrong check digit).
        { "7591001002009", BarcodeFormat.EAN_13 },
        { "5901234123457", BarcodeFormat.EAN_13 },
        { "036000291452", BarcodeFormat.UPC_A },
        { "CODE-128-TEST-1", BarcodeFormat.CODE_128 },
        { "HELLO-ZXING-123", BarcodeFormat.QR_CODE },
        { "DATAMATRIX-7", BarcodeFormat.DATA_MATRIX }
    };

    [Theory]
    [MemberData(nameof(Barcodes))]
    public void Decode_GeneratedBarcode_ReturnsExpectedText(string expectedText, BarcodeFormat format)
    {
        // Arrange: render the code with the same ZXing library used for scanning.
        var bitmap = GenerateBitmap(format, expectedText);

        // Act: mirror the scanner window's decode path (Bgra32 conversion + BitmapSource reader).
        var reader = new BarcodeReaderBitmapSource();
        var result = reader.Decode(BarcodeScannerService.ToBgra32(bitmap));

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedText, result!.Text);
        Assert.Equal(format, result.BarcodeFormat);
    }

    [Fact]
    public void Decode_BlankImage_ReturnsNull()
    {
        // A frame with no barcode must decode to null (no crash, no false positive).
        var bitmap = CreateSolidBitmap(200, 100, Colors.White);

        var reader = new BarcodeReaderBitmapSource();
        var result = reader.Decode(BarcodeScannerService.ToBgra32(bitmap));

        Assert.Null(result);
    }

    [Fact]
    public void ToBgra32_ConvertsAnyFormatToBgra32()
    {
        var bgra = CreateSolidBitmap(64, 64, Colors.Black);
        Assert.Equal(PixelFormats.Bgra32, bgra.Format);

        // Same-instance fast path when already Bgra32.
        Assert.Same(bgra, BarcodeScannerService.ToBgra32(bgra));

        // Non-Bgra32 input (Bgr24, as JPEG frames typically decode to) is converted.
        var bgr24 = new FormatConvertedBitmap(bgra, PixelFormats.Bgr24, null, 0);
        bgr24.Freeze();
        Assert.Equal(PixelFormats.Bgr24, bgr24.Format);

        var converted = BarcodeScannerService.ToBgra32(bgr24);
        Assert.Equal(PixelFormats.Bgra32, converted.Format);
    }

    private static BitmapSource GenerateBitmap(BarcodeFormat format, string contents)
    {
        bool is2D = format == BarcodeFormat.QR_CODE || format == BarcodeFormat.DATA_MATRIX;

        var writer = new BarcodeWriterPixelData
        {
            Format = format,
            Options = new EncodingOptions
            {
                Width = is2D ? 320 : 480,
                Height = is2D ? 320 : 140,
                Margin = is2D ? 8 : 20,
                PureBarcode = false
            }
        };

        var pixelData = writer.Write(contents);
        return CreateBitmapFromRgba(pixelData.Pixels, pixelData.Width, pixelData.Height);
    }

    private static BitmapSource CreateBitmapFromRgba(byte[] rgbaPixels, int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), rgbaPixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateSolidBitmap(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;     // B
            pixels[i + 1] = color.G; // G
            pixels[i + 2] = color.R; // R
            pixels[i + 3] = color.A; // A
        }
        return CreateBitmapFromRgba(pixels, width, height);
    }

    [Theory]
    // Valid EAN-13
    [InlineData("7591001002009", true)]
    [InlineData("5901234123457", true)]
    // Valid UPC-A
    [InlineData("036000291452", true)]
    // Valid EAN-8
    [InlineData("75910013", true)]
    // Valid GS1 In-store scale barcode (prefix 20-29)
    [InlineData("2012345005006", true)]
    // Valid alphanumeric and internal SKUs
    [InlineData("SKU123456", true)]
    [InlineData("PROD001", true)]
    [InlineData("1001", true)]
    [InlineData("123456789012345", true)] // Exactly 15 chars
    // Corrupted GS1 barcodes with wrong check digit
    [InlineData("7591001002003", false)] // Bad EAN-13 check digit
    [InlineData("5901234123450", false)] // Bad EAN-13 check digit
    [InlineData("036000291459", false)] // Bad UPC-A check digit
    [InlineData("75910019", false)] // Bad EAN-8 check digit
    [InlineData("2012345005009", false)] // Bad scale check digit
    // URLs / QR
    [InlineData("http://example.com/item?id=10", false)]
    [InlineData("https://menu.pos.com/qr", false)]
    [InlineData("www.example.com?query=1&param=2", false)]
    [InlineData("PROD 123", false)] // Space
    [InlineData("SKU-1001", false)] // Hyphen/Symbol
    [InlineData("123", false)] // Too short (< 4 chars)
    [InlineData("1234567890123456", false)] // Too long (> 15 chars)
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void BarcodeValidator_ValidatesFormatsProperly(string? code, bool expectedValid)
    {
        bool isValid = Core.Helpers.BarcodeValidator.IsValidBarcode(code);
        Assert.Equal(expectedValid, isValid);
    }

    [Fact]
    public void ScannerFeedbackService_PlaysTonesWithoutThrowing()
    {
        using var feedback = new ScannerFeedbackService();
        var exSuccess = Record.Exception(() => feedback.PlaySuccess());
        var exNotFound = Record.Exception(() => feedback.PlayNotFound());
        var exError = Record.Exception(() => feedback.PlayError());

        Assert.Null(exSuccess);
        Assert.Null(exNotFound);
        Assert.Null(exError);
    }

    [Fact]
    public void RestrictedReader_Decodes1D_AndValidatorRejectsQRContent()
    {
        // 1D EAN-13 barcode image
        var eanBitmap = GenerateBitmap(BarcodeFormat.EAN_13, "7591001002009");
        var reader1D = new BarcodeReaderBitmapSource
        {
            AutoRotate = true,
            Options =
            {
                TryHarder = false,
                PossibleFormats = new System.Collections.Generic.List<BarcodeFormat>
                {
                    BarcodeFormat.EAN_13,
                    BarcodeFormat.EAN_8,
                    BarcodeFormat.UPC_A,
                    BarcodeFormat.UPC_E,
                    BarcodeFormat.CODE_128,
                    BarcodeFormat.CODE_39,
                    BarcodeFormat.ITF,
                    BarcodeFormat.CODABAR
                }
            }
        };

        var result1D = reader1D.Decode(BarcodeScannerService.ToBgra32(eanBitmap));
        Assert.NotNull(result1D);
        Assert.True(Core.Helpers.BarcodeValidator.IsValidBarcode(result1D!.Text));

        // QR Code image with URL
        var qrBitmap = GenerateBitmap(BarcodeFormat.QR_CODE, "https://example.com/product?id=999");
        var resultQR = reader1D.Decode(BarcodeScannerService.ToBgra32(qrBitmap));
        
        // Either reader ignores it (null) because QR_CODE is not in PossibleFormats,
        // or if decoded by general reader, BarcodeValidator rejects it.
        Assert.Null(resultQR);
        Assert.False(Core.Helpers.BarcodeValidator.IsValidBarcode("https://example.com/product?id=999"));
    }
}
