using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Desktop.Client.Services;

/// <summary>
/// Thin wrapper around the native Windows 10/11 OCR engine (Windows.Media.Ocr).
/// Frames are processed strictly in memory and discarded afterwards.
/// </summary>
public sealed class OcrService
{
    private readonly OcrEngine _engine;

    private OcrService(OcrEngine engine)
    {
        _engine = engine;
    }

    public string LanguageTag => _engine.RecognizerLanguage.LanguageTag;

    /// <summary>
    /// Creates the service using the user's installed OCR languages, or null when
    /// no OCR language pack is available on the system.
    /// </summary>
    public static OcrService? TryCreate()
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            return engine == null ? null : new OcrService(engine);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Recognizes text inside an in-memory JPEG image.
    /// </summary>
    public async Task<string> RecognizeAsync(byte[] jpegBytes)
    {
        using var stream = new MemoryStream(jpegBytes);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());

        SoftwareBitmap bitmap;
        uint maxDimension = OcrEngine.MaxImageDimension;

        if (decoder.PixelWidth > maxDimension || decoder.PixelHeight > maxDimension)
        {
            // The OCR engine refuses oversized images; scale down while decoding.
            double scale = Math.Min(1.0, (double)maxDimension / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, (int)Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = (uint)Math.Max(1, (int)Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Linear
            };

            bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
        }
        else
        {
            bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore);
        }

        using (bitmap)
        {
            var result = await _engine.RecognizeAsync(bitmap);
            return result?.Text ?? string.Empty;
        }
    }
}
