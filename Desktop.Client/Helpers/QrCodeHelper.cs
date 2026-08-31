using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.QrCode;

namespace Desktop.Client.Helpers;

public static class QrCodeHelper
{
    /// <summary>
    /// Genera un ImageSource (BitmapSource) con el código QR renderizado en alta nitidez.
    /// Utiliza BarcodeWriterPixelData nativo de ZXing para máxima velocidad y compatibilidad en .NET 10.
    /// </summary>
    public static ImageSource? GenerateQrBitmap(string text, int width = 320, int height = 320)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            var writer = new BarcodeWriterPixelData
            {
                Format = BarcodeFormat.QR_CODE,
                Options = new QrCodeEncodingOptions
                {
                    Height = height,
                    Width = width,
                    Margin = 1,
                    CharacterSet = "UTF-8"
                }
            };

            var pixelData = writer.Write(text);
            var bitmap = BitmapSource.Create(
                pixelData.Width,
                pixelData.Height,
                96,
                96,
                PixelFormats.Bgr32,
                null,
                pixelData.Pixels,
                pixelData.Width * 4);

            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
