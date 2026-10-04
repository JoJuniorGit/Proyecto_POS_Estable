using System.Linq;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.Services;

public partial class SupplierInvoiceService
{
    private const int MaxStageLineCount = 5000;

    public async Task<SupplierInvoiceDetailDto> StageAsync(
        StageSupplierInvoiceRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var lines = ValidateAndNormalizeLines(request.Lines);
        var supplier = await ResolveSupplierAsync(request, cancellationToken);
        await UpsertColumnMappingAsync(supplier.Id, request.ColumnMapping, cancellationToken);
        var similarityThreshold = await ReadSimilarityThresholdAsync(cancellationToken);

        var invoice = new SupplierInvoice
        {
            SupplierId = supplier.Id,
            Status = SupplierInvoiceStatus.Draft
        };

        foreach (var line in lines)
        {
            var match = await MatchProductAsync(supplier.Id, line, similarityThreshold, cancellationToken);
            invoice.Lines.Add(CreateStagedLine(line, match));
        }

        _context.SupplierInvoices.Add(invoice);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDetailDto(invoice);
    }

    public async Task<SupplierInvoiceDetailDto?> GetInvoiceAsync(
        int invoiceId,
        CancellationToken cancellationToken = default)
    {
        var invoice = await _context.SupplierInvoices
            .AsNoTracking()
            .AsSplitQuery()
            .Include(candidate => candidate.Lines)
            .FirstOrDefaultAsync(candidate => candidate.Id == invoiceId, cancellationToken);

        return invoice is null ? null : ToDetailDto(invoice);
    }

    public async Task<IReadOnlyList<SupplierSummaryDto>> GetSuppliersAsync(
        string? rifOrNit,
        string? commercialName,
        CancellationToken cancellationToken = default)
    {
        var normalizedRifOrNit = NormalizeSupplierRifOrNit(rifOrNit);
        var normalizedCommercialName = NormalizeSupplierName(commercialName);

        if (normalizedRifOrNit.Length == 0 && normalizedCommercialName.Length == 0)
        {
            throw new ArgumentException("A RIF/NIT or commercial name is required.");
        }

        var query = _context.Suppliers.AsNoTracking();
        if (normalizedRifOrNit.Length > 0 && normalizedCommercialName.Length > 0)
        {
            query = query.Where(supplier =>
                supplier.NormalizedRifOrNit == normalizedRifOrNit ||
                supplier.NormalizedCommercialName == normalizedCommercialName);
        }
        else if (normalizedRifOrNit.Length > 0)
        {
            query = query.Where(supplier => supplier.NormalizedRifOrNit == normalizedRifOrNit);
        }
        else
        {
            query = query.Where(supplier => supplier.NormalizedCommercialName == normalizedCommercialName);
        }

        return await query
            .OrderBy(supplier => supplier.CommercialName)
            .ThenBy(supplier => supplier.Id)
            .Take(20)
            .Select(supplier => new SupplierSummaryDto(
                supplier.Id,
                supplier.RifOrNit,
                supplier.CommercialName,
                supplier.ColumnMapping == null
                    ? null
                    : new SupplierColumnMappingDto(
                        supplier.ColumnMapping.BarcodeColumnName,
                        supplier.ColumnMapping.SupplierCodeColumnName,
                        supplier.ColumnMapping.NameColumnName,
                        supplier.ColumnMapping.QuantityColumnName,
                        supplier.ColumnMapping.UnitCostColumnName)))
            .ToListAsync(cancellationToken);
    }

    public async Task<SupplierSummaryDto> CreateSupplierAsync(
        CreateSupplierRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CommercialName);

        var commercialName = request.CommercialName.Trim();
        if (commercialName.Length > 200)
        {
            throw new ArgumentException("The commercial name cannot exceed 200 characters.", nameof(request));
        }

        var normalizedCommercialName = NormalizeSupplierName(commercialName);
        if (normalizedCommercialName.Length == 0)
        {
            throw new ArgumentException("The commercial name must contain letters or digits.", nameof(request));
        }

        var rifOrNit = string.IsNullOrWhiteSpace(request.RifOrNit) ? null : request.RifOrNit.Trim();
        if (rifOrNit?.Length > 50)
        {
            throw new ArgumentException("The RIF/NIT cannot exceed 50 characters.", nameof(request));
        }

        var normalizedRifOrNit = NormalizeSupplierRifOrNit(rifOrNit);
        if (normalizedRifOrNit.Length == 0)
        {
            rifOrNit = null;
            normalizedRifOrNit = null;
        }

        if (normalizedRifOrNit is not null && await _context.Suppliers
                .AsNoTracking()
                .AnyAsync(supplier => supplier.NormalizedRifOrNit == normalizedRifOrNit, cancellationToken))
        {
            throw new InvalidOperationException("A supplier with this RIF/NIT already exists.");
        }

        var supplier = new Supplier
        {
            RifOrNit = rifOrNit,
            NormalizedRifOrNit = normalizedRifOrNit,
            CommercialName = commercialName,
            NormalizedCommercialName = normalizedCommercialName
        };

        _context.Suppliers.Add(supplier);
        await _context.SaveChangesAsync(cancellationToken);

        return new SupplierSummaryDto(supplier.Id, supplier.RifOrNit, supplier.CommercialName, null);
    }

    private async Task<Supplier> ResolveSupplierAsync(
        StageSupplierInvoiceRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.SupplierId is int supplierId)
        {
            return await _context.Suppliers
                .AsNoTracking()
                .FirstOrDefaultAsync(supplier => supplier.Id == supplierId, cancellationToken)
                ?? throw new KeyNotFoundException($"Supplier {supplierId} was not found.");
        }

        var normalizedRifOrNit = NormalizeSupplierRifOrNit(request.SupplierRifOrNit);
        var normalizedCommercialName = NormalizeSupplierName(request.SupplierCommercialName);
        if (normalizedRifOrNit.Length == 0 && normalizedCommercialName.Length == 0)
        {
            throw new ArgumentException("A supplier ID, RIF/NIT, or commercial name is required.");
        }

        if (normalizedRifOrNit.Length > 0)
        {
            var rifMatches = await _context.Suppliers
                .AsNoTracking()
                .Where(supplier => supplier.NormalizedRifOrNit == normalizedRifOrNit)
                .OrderBy(supplier => supplier.Id)
                .Take(2)
                .ToListAsync(cancellationToken);

            if (rifMatches.Count > 1)
            {
                throw new InvalidOperationException("The supplier RIF/NIT matches more than one supplier.");
            }

            if (rifMatches.Count == 1)
            {
                return rifMatches[0];
            }
        }

        if (normalizedCommercialName.Length > 0)
        {
            var nameMatches = await _context.Suppliers
                .AsNoTracking()
                .Where(supplier => supplier.NormalizedCommercialName == normalizedCommercialName)
                .OrderBy(supplier => supplier.Id)
                .Take(2)
                .ToListAsync(cancellationToken);

            if (nameMatches.Count > 1)
            {
                throw new InvalidOperationException("The supplier commercial name matches more than one supplier.");
            }

            if (nameMatches.Count == 1)
            {
                return nameMatches[0];
            }
        }

        throw new KeyNotFoundException("No supplier matches the supplied RIF/NIT or commercial name. Select or create a supplier before staging.");
    }

    private static List<StageLineDto> ValidateAndNormalizeLines(IReadOnlyList<StageLineDto>? sourceLines)
    {
        if (sourceLines is null || sourceLines.Count == 0)
        {
            throw new ArgumentException("The staging request contains no readable invoice rows.", nameof(sourceLines));
        }

        if (sourceLines.Count > MaxStageLineCount)
        {
            throw new ArgumentException($"The staging request exceeds the maximum of {MaxStageLineCount} rows.", nameof(sourceLines));
        }

        var lines = sourceLines
            .Where(line => line is not null && IsReadableLine(line))
            .Select(line => new StageLineDto(
                OptionalColumn(line.SupplierCode),
                OptionalColumn(line.Barcode),
                OptionalColumn(line.Name),
                line.Quantity,
                line.UnitCostDocument))
            .ToList();

        if (lines.Count == 0)
        {
            throw new ArgumentException("The staging request contains no readable invoice rows.", nameof(sourceLines));
        }

        foreach (var line in lines)
        {
            if (line.Quantity < 0m || line.UnitCostDocument < 0m)
            {
                throw new ArgumentException("Invoice quantities and unit costs cannot be negative.", nameof(sourceLines));
            }

            if (line.SupplierCode?.Length > 100 || line.Barcode?.Length > 100 || line.Name?.Length > 200)
            {
                throw new ArgumentException("Invoice row identifiers exceed the supported length.", nameof(sourceLines));
            }
        }

        return lines;
    }

    private static bool IsReadableLine(StageLineDto line) =>
        !string.IsNullOrWhiteSpace(line.SupplierCode) ||
        !string.IsNullOrWhiteSpace(line.Barcode) ||
        !string.IsNullOrWhiteSpace(line.Name);
}
