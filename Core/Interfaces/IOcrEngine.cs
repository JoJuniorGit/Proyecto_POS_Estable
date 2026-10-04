namespace Core.Interfaces;

/// <summary>
/// 8.147-T2/D1: frontera del motor OCR. Las implementaciones con nativos (Tesseract) quedan
/// aisladas detrás de esta interfaz para que la suite pueda inyectar word-boxes canned sin
/// cargar librerías nativas; el smoke nativo es el único que ejercita la implementación real.
/// </summary>
public interface IOcrEngine
{
    /// <summary>Reconoce las palabras de una página PNG-encoded.</summary>
    Task<OcrPage> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default);
}

/// <summary>Página rasterizada a PNG (por D4 el backend produce y sirve las previews).</summary>
public sealed record OcrImage(byte[] EncodedBytes, int PixelWidth, int PixelHeight);

/// <summary>Palabra reconocida con su caja en píxeles de página; confianza porcentual 0–100.</summary>
public sealed record OcrWord(string Text, double X, double Y, double Width, double Height, double Confidence);

/// <summary>Palabras reconocidas de una página, en orden de lectura.</summary>
public sealed record OcrPage(IReadOnlyList<OcrWord> Words);
