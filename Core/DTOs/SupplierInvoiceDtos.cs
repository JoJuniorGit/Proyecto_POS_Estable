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
