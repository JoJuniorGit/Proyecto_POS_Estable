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
}
