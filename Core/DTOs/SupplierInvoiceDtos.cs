using System.Collections.Generic;

namespace Core.DTOs;

public sealed record StageSupplierInvoiceRequestDto(
    int? SupplierId,
    string? SupplierRifOrNit,
    string? SupplierCommercialName,
    SupplierColumnMappingDto? ColumnMapping,
    IReadOnlyList<StageLineDto> Lines,
    string Currency,
    decimal AppliedRate,
    // 8.147-S6/D11: las filas OCR llegan ya estructuradas; true relaja el requisito de plantilla
    // de la primera importación. La ruta tabular (false) conserva el requisito vigente.
    bool OcrSourced = false);

public sealed record StageLineDto(
    string? SupplierCode,
    string? Barcode,
    string? Name,
    decimal Quantity,
    decimal UnitCostDocument,
    // 8.147-S6/D10: confianza OCR por campo (0–100, 1 decimal); omitidas/null en filas tabulares.
    decimal? OcrNameConfidence = null,
    decimal? OcrQuantityConfidence = null,
    decimal? OcrUnitCostConfidence = null,
    // Factor de conversión caja→unidad extraído por plantilla OCR o preconfigurado (default null = 1).
    decimal? ConversionFactor = null);

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
    string MatchMethod,
    // 8.147-S6/D10: confianzas OCR persistidas (0–100); null en filas tabulares.
    decimal? OcrNameConfidence,
    decimal? OcrQuantityConfidence,
    decimal? OcrUnitCostConfidence,
    // 8.165: factor de conversión caja→unidad persistido por línea (default 1, sin conversión).
    decimal ConversionFactor = 1m,
    // 8.184-T2 (S5): regla de redondeo per-línea persistida; null = hereda la regla de la factura.
    // La UI la usa para repoblar el selector al recargar el borrador.
    RoundingRuleType? RoundingRule = null);

public sealed record CreateInvoiceProductRequestDto(
    string Barcode,
    string Name,
    // 8.175-S5: filas 1:N del vínculo previo que el cliente reemplaza al re-resolver la línea.
    // Vacío/null = resolución simple sin absorción de filas existentes.
    IReadOnlyList<int>? ReplacedLineIds = null);

public sealed record LinkedProductMarginOverrideDto(
    int ProductId,
    decimal MarginRetail,
    decimal MarginWholesale);

public sealed record LinkInvoiceProductRequestDto(
    int ProductId,
    string? InvoiceDescription = null,
    IReadOnlyList<int>? ProductIds = null,
    IReadOnlyList<LinkedProductMarginOverrideDto>? MarginOverrides = null,
    // 8.175-S5: filas 1:N del vínculo previo que el cliente reemplaza al re-resolver la línea.
    // Vacío/null = resolución simple sin absorción de filas existentes.
    IReadOnlyList<int>? ReplacedLineIds = null,
    // 8.184-T2 (S5): regla de redondeo elegida para la línea resuelta (nivel producto). null =
    // hereda la regla de la factura al confirmar; el re-vínculo reemplaza el valor previo.
    RoundingRuleType? RoundingRule = null);

public sealed record ConfirmSupplierInvoiceRequestDto(
    IReadOnlyList<ConfirmLineDto> Lines,
    // 8.172: permite actualizar la tasa aplicada a una factura en Bs.S durante la revisión antes de confirmar.
    decimal? AppliedRate = null,
    // 8.176: moneda viva de la cabecera en revisión; null conserva la moneda del staging (contrato previo).
    // Si difiere del snapshot del draft, el confirm conmuta la moneda y renormaliza los costos aprobados.
    string? Currency = null,
    // 8.182-T3 (S7): regla de redondeo comercial aplicada a los precios recalculados por el confirm
    // (nivel factura, supuesto v1; per-línea = residual). ExactMath (default) conserva el flujo
    // vigente intacto; UsdPsychological redondea ambos canales a la escalera .99/.95/.75/.50/.25.
    // Las reglas VesCashFriendly* no se soportan en este flujo (operan sobre Bs.S) y se rechazan.
    RoundingRuleType RoundingRule = RoundingRuleType.ExactMath);

public sealed record ConfirmLineDto(
    int LineId,
    bool IsApproved,
    decimal? MarginRetailOverride,
    decimal? MarginWholesaleOverride,
    // 8.147-T7b/S6: correcciones del revisor para campos OCR (null = sin corrección). El servicio
    // de confirm las valida zero-trust y renormaliza el costo documental con el snapshot de la factura.
    string? Name = null,
    decimal? Quantity = null,
    decimal? UnitCostDocument = null,
    // 8.165: factor de conversión caja→unidad (divisor); null = sin conversión (1).
    decimal? ConversionFactor = null);

public sealed record SupplierColumnMappingDto(
    string? BarcodeColumnName,
    string? SupplierCodeColumnName,
    string NameColumnName,
    string QuantityColumnName,
    string UnitCostColumnName);

public enum OcrCostAmountRule
{
    UnitCost = 0,
    LineTotal = 1
}

public enum OcrDecimalSeparatorRule
{
    Auto = 0,
    Comma = 1,
    Dot = 2
}

public sealed record OcrCharacterReplacementRuleDto(
    string From,
    string To);

public sealed record SupplierOcrTemplateDto(
    double RotationDegrees = 0d,
    double ContrastFactor = 1.0d,
    double BrightnessDelta = 0d,
    int BinarizationThreshold = 0,
    double? RoiX = null,
    double? RoiY = null,
    double? RoiWidth = null,
    double? RoiHeight = null,
    string? HeaderAnchorKeywords = null,
    string? FooterAnchorKeywords = null,
    int? ProductColumnPosition = null,
    int? QuantityColumnPosition = null,
    int? UnitCostColumnPosition = null,
    int? SupplierCodeColumnPosition = null,
    OcrCostAmountRule CostAmountRule = OcrCostAmountRule.UnitCost,
    OcrDecimalSeparatorRule DecimalSeparator = OcrDecimalSeparatorRule.Auto,
    IReadOnlyList<OcrCharacterReplacementRuleDto>? CharacterReplacements = null,
    string? FactorExtractionPattern = null);

// 8.147-T3: fila extraída por el parser heurístico de word-boxes OCR. Confianzas 0–100 (1
// decimal); un campo no resuelto lleva valor null y confianza 0. T4 la expone en la respuesta
// del endpoint de extracción (junto con las pistas de proveedor).
public sealed record OcrExtractedLineDto(
    string? SupplierCode,
    string? Barcode,
    string? Name,
    decimal? Quantity,
    decimal? UnitCost,
    decimal NameConfidence,
    decimal QuantityConfidence,
    decimal UnitCostConfidence,
    decimal? ExtractedConversionFactor = null);

// 8.147-T4/D12: entrada del endpoint de extracción OCR (multipart ya materializado). El mapping
// es opcional: solo lo envía el cliente cuando hay plantilla del proveedor; sin él el parser usa
// keywords genéricas. Las pistas RIF/nombre son best-effort y nunca seleccionan ni persisten nada.
public sealed record OcrExtractionRequestDto(
    byte[] FileBytes,
    string FileName,
    int? SupplierId,
    SupplierColumnMappingDto? ColumnMapping,
    SupplierOcrTemplateDto? OcrTemplate = null);

// 8.160-S2/S4 + 8.172: respuesta de extracción con filas (confianza por campo) y pistas best-effort de
// RIF, nombre de proveedor, número de factura y tasa de cambio detectada. El payload de previews por
// página se retiró por decisión del mantenedor: la revisión Desktop ya no muestra la imagen escaneada.
public sealed record OcrExtractionResultDto(
    IReadOnlyList<OcrExtractedLineDto> Lines,
    string? DetectedRif,
    string? DetectedSupplierName,
    string? DetectedInvoiceNumber,
    decimal? DetectedExchangeRate = null,
    string? RawExtractedText = null,
    byte[]? PreprocessedPreviewPng = null);

