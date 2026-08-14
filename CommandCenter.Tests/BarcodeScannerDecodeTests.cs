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
}
