using Core.DTOs;

namespace Core.Interfaces;

/// <summary>
/// 8.147-T4/D12: frontera del servicio de extracción OCR (endpoint
/// <c>POST /api/supplier-invoices/ocr-extract</c>). Orquesta decodificación, preprocesamiento,
/// motor OCR y parser heurístico; no persiste nada: las filas, previews y pistas vuelven al
/// cliente para la revisión humana (el confirm sigue siendo el único punto de aplicación).
/// </summary>
public interface IOcrExtractionService
{
    /// <summary>Extrae filas OCR de una imagen/PDF más las previews y pistas de proveedor.</summary>
    Task<OcrExtractionResultDto> ExtractAsync(
        OcrExtractionRequestDto request,
        CancellationToken cancellationToken = default);
}
