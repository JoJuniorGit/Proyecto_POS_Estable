using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Desktop.Client.Services;
using Xunit;
using ZXing;
using ZXing.Common;
using ZXing.Windows.Compatibility;

namespace CommandCenter.Tests;

public class BarcodeScannerRoiTests
{
    [Fact]
    public void ExtractRoi_StandardResolution_Applies60x50Ratio()
    {
        // 960 x 540 frame (standard resolution >= 640)
        var bitmap = CreateSolidBitmap(960, 540, Colors.White);
        var roi = BarcodeScannerService.ExtractRoi(bitmap);

        Assert.NotNull(roi);
        Assert.Equal(576, roi.PixelWidth);  // 960 * 0.60
        Assert.Equal(270, roi.PixelHeight); // 540 * 0.50
    }

    [Fact]
    public void ExtractRoi_LowResolution_Applies80x70Ratio()
    {
        // 480 x 360 frame (low resolution < 640)
        var bitmap = CreateSolidBitmap(480, 360, Colors.White);
        var roi = BarcodeScannerService.ExtractRoi(bitmap);

        Assert.NotNull(roi);
        Assert.Equal(384, roi.PixelWidth);  // 480 * 0.80
        Assert.Equal(252, roi.PixelHeight); // 360 * 0.70
    }

    [Fact]
    public void Decode_CentralBarcode_DecodesSuccessfullyWithRoi()
    {
        // Generate a 960x540 frame with a barcode centered in the ROI
        var code = "7591001002009";
        var frame = CreateFrameWithBarcode(960, 540, code, posX: 360, posY: 220);

        var roi = BarcodeScannerService.ExtractRoi(frame);
        var reader = CreateReader();
        var result = reader.Decode(BarcodeScannerService.ToBgra32(roi));

        Assert.NotNull(result);
        Assert.Equal(code, result!.Text);
    }

    [Fact]
    public void Decode_PeripheralBarcodeOutsideRoi_IsExcludedByRoi()
    {
        // Generate a 960x540 frame with a barcode located at top-left corner (outside the 60%x50% ROI)
        var code = "7591001002009";
        var frame = CreateFrameWithBarcode(960, 540, code, posX: 10, posY: 10);

        var roi = BarcodeScannerService.ExtractRoi(frame);
        var reader = CreateReader();
        var result = reader.Decode(BarcodeScannerService.ToBgra32(roi));

        // The peripheral barcode is cropped out, preventing accidental scans
        Assert.Null(result);
    }

    [Fact]
    public void Benchmark_RoiProcessesSignificantlyFewerPixelsThanFullFrame()
    {
        var fullBitmap = CreateSolidBitmap(960, 540, Colors.White);
        var roiBitmap = BarcodeScannerService.ExtractRoi(fullBitmap);

        long fullPixels = fullBitmap.PixelWidth * fullBitmap.PixelHeight;
        long roiPixels = roiBitmap.PixelWidth * roiBitmap.PixelHeight;

        // ROI must process only ~30% of the pixels (70% CPU reduction)
        double ratio = (double)roiPixels / fullPixels;
        Assert.InRange(ratio, 0.28, 0.32);
    }

    private static BarcodeReaderBitmapSource CreateReader()
    {
        return new BarcodeReaderBitmapSource
        {
            AutoRotate = true,
            Options =
            {
                TryHarder = false,
                PossibleFormats = new System.Collections.Generic.List<BarcodeFormat> { BarcodeFormat.EAN_13 }
            }
        };
    }

    private static BitmapSource CreateFrameWithBarcode(int frameWidth, int frameHeight, string code, int posX, int posY)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.EAN_13,
            Options = new EncodingOptions { Width = 240, Height = 100, Margin = 5, PureBarcode = false }
        };
        var barcodeData = writer.Write(code);

        int stride = frameWidth * 4;
        byte[] pixels = new byte[stride * frameHeight];
        // Initialize with white background (Bgra32)
        Array.Fill(pixels, (byte)255);

        // Copy barcode into frame at (posX, posY)
        for (int y = 0; y < barcodeData.Height; y++)
        {
            int targetY = posY + y;
            if (targetY < 0 || targetY >= frameHeight) continue;

            for (int x = 0; x < barcodeData.Width; x++)
            {
                int targetX = posX + x;
                if (targetX < 0 || targetX >= frameWidth) continue;

                int srcIdx = (y * barcodeData.Width + x) * 4;
                int dstIdx = targetY * stride + targetX * 4;

                pixels[dstIdx] = barcodeData.Pixels[srcIdx + 2];     // B
                pixels[dstIdx + 1] = barcodeData.Pixels[srcIdx + 1]; // G
                pixels[dstIdx + 2] = barcodeData.Pixels[srcIdx];     // R
                pixels[dstIdx + 3] = barcodeData.Pixels[srcIdx + 3]; // A
            }
        }

        var source = BitmapSource.Create(frameWidth, frameHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private static BitmapSource CreateSolidBitmap(int width, int height, Color color)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = color.A;
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }
}
