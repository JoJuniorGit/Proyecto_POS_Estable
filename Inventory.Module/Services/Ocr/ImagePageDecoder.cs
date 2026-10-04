using Core.Interfaces;
using OpenCvSharp;

namespace Inventory.Module.Services.Ocr;

/// <summary>
/// 8.147-T2: decodifica una imagen (PNG/JPG) a una página normalizada. OpenCvSharp decodifica y
/// re-codifica a PNG de 3 canales para que el motor OCR y las previews reciban siempre el mismo
/// formato.
/// </summary>
public sealed class ImagePageDecoder
{
    private readonly ImagePreprocessor _preprocessor;

    public ImagePageDecoder(ImagePreprocessor preprocessor)
    {
        ArgumentNullException.ThrowIfNull(preprocessor);
        _preprocessor = preprocessor;
    }

    /// <summary>Decodifica los bytes de una imagen a una página PNG con sus dimensiones.</summary>
    public OcrImage Decode(byte[] fileBytes)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);
        using var decoded = Cv2.ImDecode(fileBytes, ImreadModes.Color);
        if (decoded.Empty())
        {
            throw new ArgumentException("Los bytes no corresponden a una imagen PNG/JPG válida.", nameof(fileBytes));
        }

        var png = _preprocessor.EncodePng(decoded);
        return new OcrImage(png, decoded.Width, decoded.Height);
    }
}
