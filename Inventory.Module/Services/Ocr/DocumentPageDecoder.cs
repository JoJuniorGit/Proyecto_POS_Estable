using Core.Interfaces;

namespace Inventory.Module.Services.Ocr;

/// <summary>
/// 8.147-T2: fachada de decodificación por extensión para el endpoint de extracción (T4).
/// PDF → una página por hoja; imágenes → una única página normalizada.
/// </summary>
public sealed class DocumentPageDecoder
{
    private readonly ImagePageDecoder _imageDecoder;
    private readonly PdfPageDecoder _pdfDecoder;

    public DocumentPageDecoder(ImagePageDecoder imageDecoder, PdfPageDecoder pdfDecoder)
    {
        ArgumentNullException.ThrowIfNull(imageDecoder);
        ArgumentNullException.ThrowIfNull(pdfDecoder);
        _imageDecoder = imageDecoder;
        _pdfDecoder = pdfDecoder;
    }

    /// <summary>
    /// Decodifica el archivo según su extensión (png/jpg/jpeg/pdf) a páginas PNG.
    /// Una extensión no soportada lanza <see cref="ArgumentException"/>.
    /// </summary>
    public IReadOnlyList<OcrImage> DecodePages(byte[] fileBytes, string extension)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        var normalized = extension.Trim().ToLowerInvariant();
        if (!normalized.StartsWith('.'))
        {
            normalized = "." + normalized;
        }

        return normalized switch
        {
            ".pdf" => _pdfDecoder.Decode(fileBytes),
            ".png" or ".jpg" or ".jpeg" => new[] { _imageDecoder.Decode(fileBytes) },
            _ => throw new ArgumentException(
                $"La extensión '{extension}' no está soportada; use png, jpg, jpeg o pdf.", nameof(extension))
        };
    }
}
