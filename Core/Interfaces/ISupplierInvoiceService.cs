using Core.DTOs;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

public interface ISupplierInvoiceService
{
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

    Task<IReadOnlyList<SupplierSummaryDto>> GetSuppliersAsync(
        string? rifOrNit,
        string? commercialName,
        CancellationToken cancellationToken = default);

    Task<SupplierSummaryDto> CreateSupplierAsync(
        CreateSupplierRequestDto request,
        CancellationToken cancellationToken = default);
}

public sealed record SupplierSummaryDto(int Id, string? RifOrNit, string CommercialName);

public sealed record CreateSupplierRequestDto(string? RifOrNit, string CommercialName);
