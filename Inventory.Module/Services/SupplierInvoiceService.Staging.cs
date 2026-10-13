using System.Linq;
using Core.Common;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inventory.Module.Services;

public partial class SupplierInvoiceService
{
    private const int MaxStageLineCount = 5000;

    public async Task<SupplierInvoiceDetailDto> StageAsync(
        StageSupplierInvoiceRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (currency, appliedRate) = ResolveCurrencySnapshot(request);
        var lines = ValidateAndNormalizeLines(request.Lines);
        var supplier = await ResolveSupplierAsync(request, cancellationToken);
        // 8.147-S6/D11: las filas OCR ya vienen estructuradas; sin mapping en la request no hay
        // plantilla que aprender ni exigir. La ruta tabular conserva el requisito de primera importación.
        if (request.ColumnMapping is not null || !request.OcrSourced)
        {
            await UpsertColumnMappingAsync(supplier.Id, request.ColumnMapping, cancellationToken);
        }

        var similarityThreshold = await ReadSimilarityThresholdAsync(cancellationToken);
        // 8.155/PEF-01 (REQ-SIB-01): los lookups exactos se precargan una sola vez por stage;
        // el loop resuelve barcode/supplierCode en memoria y solo cae a fuzzy sin match exacto.
        var matchLookups = await LoadMatchLookupsAsync(supplier.Id, lines, cancellationToken);
        // 8.166: factores caja→unidad recordados por proveedor+producto; se prefijan por línea.
        var conversionFactorsByProductId = await _context.SupplierProductFactors
            .AsNoTracking()
            .Where(factor => factor.SupplierId == supplier.Id)
            .ToDictionaryAsync(factor => factor.ProductId, factor => factor.ConversionFactor, cancellationToken);

        var exactMatches = new ProductMatch?[lines.Count];
        var unmatchedNames = new List<string>();
        for (var index = 0; index < lines.Count; index++)
        {
            var exactMatch = TryResolveExactMatch(matchLookups, lines[index]);
            exactMatches[index] = exactMatch;
            if (exactMatch is null && !string.IsNullOrWhiteSpace(lines[index].Name))
            {
                unmatchedNames.Add(lines[index].Name!);
            }
        }

        var fuzzyMatchesByName = await ResolveFuzzyMatchesBatchAsync(
            unmatchedNames,
            similarityThreshold,
            cancellationToken);

        var invoice = new SupplierInvoice
        {
            SupplierId = supplier.Id,
            Status = SupplierInvoiceStatus.Draft,
            Currency = currency,
            AppliedRate = appliedRate
        };

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var match = exactMatches[index]
                ?? (line.Name is not null && fuzzyMatchesByName.TryGetValue(line.Name, out var fuzzyMatch)
                    ? fuzzyMatch
                    : new ProductMatch(null, MatchMethod.None));
            if (match.Product is not null && match.AdditionalProducts is { Count: > 0 } additionalProducts)
            {
                var matchedProducts = new List<Product>(1 + additionalProducts.Count) { match.Product };
                matchedProducts.AddRange(additionalProducts);

                for (var productIndex = 0; productIndex < matchedProducts.Count; productIndex++)
                {
                    var matchedProduct = matchedProducts[productIndex];
                    var factor = conversionFactorsByProductId.TryGetValue(matchedProduct.Id, out var rememberedProductFactor)
                        && rememberedProductFactor > 0m
                        ? rememberedProductFactor
                        : line.ConversionFactor is > 1m
                            ? line.ConversionFactor.Value
                            : 1m;
                    var productUnitCostUsd = Core.Helpers.PricingCalculator.CalculateInvoiceUnitCostUsd(
                        line.UnitCostDocument,
                        factor,
                        currency,
                        appliedRate);
                    var expandedInputLine = line with
                    {
                        SupplierCode = line.SupplierCode ?? line.Name,
                        Barcode = line.Barcode ?? matchedProduct.SKU,
                        Name = matchedProduct.Name,
                        Quantity = productIndex == 0 ? line.Quantity : 0m
                    };
                    var expandedStagedLine = CreateStagedLine(
                        expandedInputLine,
                        new ProductMatch(matchedProduct, match.Method),
                        productUnitCostUsd,
                        request.OcrSourced);
                    expandedStagedLine.Status = SupplierInvoiceLineStatus.Update;
                    expandedStagedLine.ConversionFactor = factor;
                    invoice.Lines.Add(expandedStagedLine);
                }

                continue;
            }

            var rememberedFactor = 1m;
            var hasRememberedFactor = match.Product is not null
                && conversionFactorsByProductId.TryGetValue(match.Product.Id, out rememberedFactor)
                && rememberedFactor > 0m;
            var inputFactor = !hasRememberedFactor && line.ConversionFactor is > 1m
                ? line.ConversionFactor.Value
                : 1m;
            var unitCostUsd = Core.Helpers.PricingCalculator.CalculateInvoiceUnitCostUsd(
                line.UnitCostDocument,
                inputFactor,
                currency,
                appliedRate);

            var stagedLine = CreateStagedLine(line, match, unitCostUsd, request.OcrSourced);
            if (hasRememberedFactor)
            {
                stagedLine.ConversionFactor = rememberedFactor;
            }
            else if (line.ConversionFactor is > 1m)
            {
                stagedLine.ConversionFactor = line.ConversionFactor.Value;
            }

            invoice.Lines.Add(stagedLine);
        }

        _context.SupplierInvoices.Add(invoice);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Staged supplier invoice {InvoiceId} for supplier {SupplierId} with {LineCount} lines ({Currency} @ {AppliedRate}).",
            invoice.Id,
            supplier.Id,
            invoice.Lines.Count,
            currency,
            appliedRate);

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
        var isFiltered = normalizedRifOrNit.Length > 0 || normalizedCommercialName.Length > 0;

        var query = _context.Suppliers
            .AsNoTracking()
            .Include(supplier => supplier.ColumnMapping)
            .Include(supplier => supplier.OcrTemplate)
            .AsQueryable();
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
        else if (normalizedCommercialName.Length > 0)
        {
            query = query.Where(supplier => supplier.NormalizedCommercialName == normalizedCommercialName);
        }

        // 8.161: sin filtros devuelve el listado completo para el selector de la vista (cota
        // defensiva de 500); con filtro conserva la cota de 20 de la busqueda exacta.
        var take = isFiltered ? 20 : 500;

        var suppliers = await query
            .OrderBy(supplier => supplier.CommercialName)
            .ThenBy(supplier => supplier.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        return suppliers
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
                        supplier.ColumnMapping.UnitCostColumnName),
                supplier.InvoiceCurrency,
                supplier.OcrTemplate == null ? null : ToOcrTemplateDto(supplier.OcrTemplate)))
            .ToArray();
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

        if (normalizedRifOrNit is null && await _context.Suppliers
                .AsNoTracking()
                .AnyAsync(supplier => supplier.NormalizedCommercialName == normalizedCommercialName, cancellationToken))
        {
            throw new InvalidOperationException("A supplier with this commercial name already exists.");
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

        _logger.LogInformation(
            "Created supplier {SupplierId} ({CommercialName}).",
            supplier.Id,
            supplier.CommercialName);

        return new SupplierSummaryDto(supplier.Id, supplier.RifOrNit, supplier.CommercialName, null, supplier.InvoiceCurrency);
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

    private static (string Currency, decimal AppliedRate) ResolveCurrencySnapshot(
        StageSupplierInvoiceRequestDto request)
    {
        if (!string.Equals(request.Currency, CurrencyCodes.Usd, StringComparison.Ordinal) &&
            !string.Equals(request.Currency, CurrencyCodes.BsS, StringComparison.Ordinal))
        {
            throw new ArgumentException("The supplier invoice currency must be USD or Bs.S.");
        }

        if (string.Equals(request.Currency, CurrencyCodes.Usd, StringComparison.Ordinal))
        {
            return (CurrencyCodes.Usd, 1m);
        }

        if (request.AppliedRate <= 0m)
        {
            throw new ArgumentException(
                "An applied exchange rate greater than zero is required for supplier invoices in Bs.S.");
        }

        if (request.AppliedRate > MaxInvoiceNumericValue)
        {
            throw new ArgumentException(
                "The applied exchange rate exceeds the maximum supported value.");
        }

        return (CurrencyCodes.BsS, Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(request.AppliedRate));
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
                line.UnitCostDocument,
                line.OcrNameConfidence,
                line.OcrQuantityConfidence,
                line.OcrUnitCostConfidence,
                line.ConversionFactor is > 0m and <= MaxInvoiceNumericValue ? line.ConversionFactor : null))
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

            if (line.Quantity > MaxInvoiceNumericValue || line.UnitCostDocument > MaxInvoiceNumericValue)
            {
                throw new ArgumentException("Invoice quantities and unit costs exceed the maximum supported value.", nameof(sourceLines));
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
