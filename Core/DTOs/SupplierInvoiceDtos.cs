using System.Collections.Generic;

namespace Core.DTOs;

public sealed record StageSupplierInvoiceRequestDto(
    int? SupplierId,
    string? SupplierRifOrNit,
    string? SupplierCommercialName,
    SupplierColumnMappingDto? ColumnMapping,
    IReadOnlyList<StageLineDto> Lines,
    string Currency,
    decimal AppliedRate);

public sealed record StageLineDto(
    string? SupplierCode,
    string? Barcode,
    string? Name,
    decimal Quantity,
    decimal UnitCostDocument);

public sealed record SupplierInvoiceDetailDto(
    int Id,
    int SupplierId,
    string Status,
    string Currency,
    decimal AppliedRate,
    IReadOnlyList<SupplierInvoiceLineDto> Lines);

public sealed record SupplierInvoiceLineDto(
    int Id,
    string? SupplierCode,
    string? Barcode,
    string? Name,
    decimal Quantity,
    decimal UnitCostDocument,
    decimal UnitCostUSD,
    string Status,
    int? ResolvedProductId,
    decimal? OldCostPriceUSD,
    decimal? OldProfitMarginRetail,
    decimal? OldProfitMarginWholesale,
    decimal? OldStockQuantity,
    decimal? MarginRetailOverride,
    decimal? MarginWholesaleOverride,
    decimal? SuggestedRetailPriceUSD,
    decimal? SuggestedWholesalePriceUSD,
    bool IsApproved,
    string MatchMethod);

public sealed record CreateInvoiceProductRequestDto(string Barcode, string Name);

public sealed record ConfirmSupplierInvoiceRequestDto(IReadOnlyList<ConfirmLineDto> Lines);

public sealed record ConfirmLineDto(
    int LineId,
    bool IsApproved,
    decimal? MarginRetailOverride,
    decimal? MarginWholesaleOverride);

public sealed record SupplierColumnMappingDto(
    string? BarcodeColumnName,
    string? SupplierCodeColumnName,
    string NameColumnName,
    string QuantityColumnName,
    string UnitCostColumnName);

// 8.147-T3: fila extraída por el parser heurístico de word-boxes OCR. Confianzas 0–100 (1
// decimal); un campo no resuelto lleva valor null y confianza 0. T4 la expone en la respuesta
// del endpoint de extracción (junto con previews y pistas de proveedor).
public sealed record OcrExtractedLineDto(
    string? SupplierCode,
    string? Barcode,
    string? Name,
    decimal? Quantity,
    decimal? UnitCost,
    decimal NameConfidence,
    decimal QuantityConfidence,
    decimal UnitCostConfidence);

// 8.147-T4/D12: entrada del endpoint de extracción OCR (multipart ya materializado). El mapping
// es opcional: solo lo envía el cliente cuando hay plantilla del proveedor; sin él el parser usa
// keywords genéricas. Las pistas RIF/nombre son best-effort y nunca seleccionan ni persisten nada.
public sealed record OcrExtractionRequestDto(
    byte[] FileBytes,
    string FileName,
    int? SupplierId,
    SupplierColumnMappingDto? ColumnMapping);

// 8.147-T4/D12/S5: respuesta de extracción con filas (confianza por campo), una preview PNG por
// página procesada (base64, en orden) y pistas detectadas.
public sealed record OcrExtractionResultDto(
    IReadOnlyList<OcrExtractedLineDto> Lines,
    IReadOnlyList<string> PreviewPagesBase64,
    string? DetectedRif,
    string? DetectedSupplierName);
