using Core.DTOs;
using Core.Interfaces;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public interface ISupplierInvoiceService
{
    Task<List<string>> ReadHeadersAsync(string filePath, CancellationToken cancellationToken = default);

    Task<List<StageLineDto>> ParseFileWithMappingAsync(
        string filePath,
        SupplierColumnMappingDto columnMapping,
        CancellationToken cancellationToken = default);

    Task<OcrExtractionResultDto> ExtractOcrAsync(
        string filePath,
        int? supplierId,
        SupplierColumnMappingDto? columnMapping,
        CancellationToken cancellationToken = default);

    Task<OcrExtractionResultDto> ExtractOcrAsync(
        string filePath,
        int? supplierId,
        SupplierColumnMappingDto? columnMapping,
        SupplierOcrTemplateDto? ocrTemplate,
        CancellationToken cancellationToken = default) =>
        ExtractOcrAsync(filePath, supplierId, columnMapping, cancellationToken);

    Task<SupplierOcrTemplateDto?> GetSupplierOcrTemplateAsync(
        int supplierId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<SupplierOcrTemplateDto?>(null);

    Task<SupplierOcrTemplateDto> SaveSupplierOcrTemplateAsync(
        int supplierId,
        SupplierOcrTemplateDto template,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(template);

    Task<IReadOnlyList<SupplierSummaryDto>> GetSuppliersAsync(
        string? rifOrNit,
        string? commercialName,
        CancellationToken cancellationToken = default);

    Task<SupplierSummaryDto> CreateSupplierAsync(
        CreateSupplierRequestDto request,
        CancellationToken cancellationToken = default);

    Task<SupplierInvoiceDetailDto> StageAsync(
        StageSupplierInvoiceRequestDto request,
        CancellationToken cancellationToken = default);

    Task<SupplierInvoiceDetailDto?> GetInvoiceAsync(
        int invoiceId,
        CancellationToken cancellationToken = default);

    Task<SupplierInvoiceDetailDto> ConfirmAsync(
        int invoiceId,
        ConfirmSupplierInvoiceRequestDto request,
        CancellationToken cancellationToken = default);

    Task<SupplierInvoiceDetailDto> CreateProductFromLineAsync(
        int invoiceId,
        int lineId,
        CreateInvoiceProductRequestDto request,
        CancellationToken cancellationToken = default);

    Task<SupplierInvoiceDetailDto> LinkProductToLineAsync(
        int invoiceId,
        int lineId,
        LinkInvoiceProductRequestDto request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductDto>> SearchCatalogProductsAsync(
        string? filter,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProductDto>>([]);

    // 8.175-S6: hidratación del modal de actualización — productos del catálogo por id (ruta existente).
    // Default vacío para no romper mocks/stubs existentes que no lo configuran.
    Task<IReadOnlyList<ProductDto>> GetCatalogProductsByIdsAsync(
        IReadOnlyList<int> productIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProductDto>>([]);
}

