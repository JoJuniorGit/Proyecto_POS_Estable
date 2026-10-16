using System.Text.RegularExpressions;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inventory.Module.Services;

public partial class SupplierInvoiceService
{
    private readonly IProductManagementService _productManagementService;

    public Task<SupplierInvoiceDetailDto> CreateProductFromLineAsync(
        int invoiceId,
        int lineId,
        CreateInvoiceProductRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(cancellationToken)
                : null;

            try
            {
                var invoice = await _context.SupplierInvoices
                    .AsSplitQuery()
                    .Include(candidate => candidate.Lines)
                    .FirstOrDefaultAsync(candidate => candidate.Id == invoiceId, cancellationToken)
                    ?? throw new KeyNotFoundException($"Supplier invoice {invoiceId} was not found.");

                if (invoice.Status != SupplierInvoiceStatus.Draft)
                {
                    throw new InvalidOperationException($"Supplier invoice {invoiceId} is not a draft.");
                }

                var line = invoice.Lines.FirstOrDefault(candidate => candidate.Id == lineId)
                    ?? throw new KeyNotFoundException(
                        $"Supplier invoice line {lineId} was not found in invoice {invoiceId}.");

                // 8.175-S5: el ancla puede venir ya resuelta; la creación re-resuelve la línea y
                // absorbe las filas reemplazadas del vínculo previo (misma validación zero-trust).
                var wasResolved = line.ResolvedProductId is not null;

                // 8.146-S4/D7: el SKU ES el código de barras y las columnas de códigos de la
                // factura jamás se heredan; solo el EAN/UPC capturado (zero-trust) es válido.
                var barcode = request.Barcode?.Trim() ?? string.Empty;
                if (!Regex.IsMatch(barcode, @"^\d{8,14}$"))
                {
                    throw new ArgumentException("The product barcode must be a valid EAN/UPC (8 to 14 digits).");
                }

                var name = request.Name?.Trim() ?? string.Empty;
                if (name.Length == 0)
                {
                    throw new ArgumentException("A product name is required.");
                }

                if (name.Length > 100)
                {
                    throw new ArgumentException("The product name cannot exceed 100 characters.");
                }

                if (wasResolved)
                {
                    // 8.175-S5: se valida y reconcilia el grupo previo antes de crear; una
                    // validación fallida no debe dejar producto creado ni filas eliminadas.
                    PrepareResolutionSlots(invoice, line, request.ReplacedLineIds, targetCount: 1);
                }

                // 8.146-D5: creación de identidad únicamente; costo, márgenes, precios y stock
                // se aplican solo en confirm (el producto nace en cero).
                var created = await _productManagementService.CreateProductFromDtoAsync(
                    new CreateProductDto
                    {
                        Name = name,
                        SKU = barcode,
                        CostPriceUSD = 0m,
                        ProfitMarginRetail = 0m,
                        ProfitMarginWholesale = 0m,
                        PriceUSD = 0m,
                        PriceRetailUSD = 0m,
                        PriceWholesaleUSD = 0m,
                        PriceBsS = 0m,
                        StockQuantity = 0m,
                        HasWholesale = false,
                        IsActive = true
                    },
                    cancellationToken);

                await UpsertSupplierProductCodeAsync(
                    invoice.SupplierId,
                    line.SupplierCode,
                    created.Id,
                    cancellationToken);

                line.ResolvedProductId = created.Id;
                line.Name = name;
                if (string.IsNullOrWhiteSpace(line.Barcode))
                {
                    line.Barcode = barcode;
                }

                line.Status = line.UnitCostUSD != 0m
                    ? SupplierInvoiceLineStatus.Update
                    : SupplierInvoiceLineStatus.Unchanged;
                line.OldCostPriceUSD = 0m;
                line.OldProfitMarginRetail = 0m;
                line.OldProfitMarginWholesale = 0m;
                line.OldStockQuantity = 0m;

                var retailMargin = line.MarginRetailOverride ?? 0m;
                var wholesaleMargin = line.MarginWholesaleOverride ?? line.MarginRetailOverride ?? 0m;
                line.SuggestedRetailPriceUSD = PricingCalculator.RoundPriceUp(
                    line.UnitCostUSD * (1m + retailMargin / 100m));
                line.SuggestedWholesalePriceUSD = PricingCalculator.RoundPriceUp(
                    line.UnitCostUSD * (1m + wholesaleMargin / 100m));

                await _context.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                _logger.LogInformation(
                    "Created product {ProductId} (SKU {Sku}) from supplier invoice {InvoiceId} line {LineId} (re-resolution: {IsReResolution}).",
                    created.Id,
                    created.SKU,
                    invoiceId,
                    lineId,
                    wasResolved);

                return ToDetailDto(invoice);
            }
            catch
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }

                throw;
            }
        });
    }

    public Task<SupplierInvoiceDetailDto> LinkProductToLineAsync(
        int invoiceId,
        int lineId,
        LinkInvoiceProductRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 8.184-T2 (S5): la regla de redondeo per-línea viaja como entero; un valor fuera del
        // catálogo se rechaza (whitelist zero-trust, molde SupplierInvoiceService.Apply).
        if (request.RoundingRule.HasValue && !Enum.IsDefined(request.RoundingRule.Value))
        {
            throw new ArgumentException("The rounding rule is not recognized.", nameof(request));
        }

        var requestedProductIds = request.ProductIds is { Count: > 0 }
            ? request.ProductIds.ToList()
            : new List<int> { request.ProductId };
        if (requestedProductIds.Count == 0 || requestedProductIds.Any(productId => productId <= 0))
        {
            throw new ArgumentException("A valid existing product ID is required.", nameof(request));
        }

        var targetProductIds = requestedProductIds.Distinct().ToList();

        if (_currentUserService is not null && !_currentUserService.CanMutateCatalog)
        {
            throw new UnauthorizedAccessException("No tiene permisos para modificar el catálogo de productos.");
        }

        return _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(cancellationToken)
                : null;

            try
            {
                var invoice = await _context.SupplierInvoices
                    .AsSplitQuery()
                    .Include(candidate => candidate.Lines)
                    .FirstOrDefaultAsync(candidate => candidate.Id == invoiceId, cancellationToken)
                    ?? throw new KeyNotFoundException($"Supplier invoice {invoiceId} was not found.");

                if (invoice.Status != SupplierInvoiceStatus.Draft)
                {
                    throw new InvalidOperationException($"Supplier invoice {invoiceId} is not a draft.");
                }

                var line = invoice.Lines.FirstOrDefault(candidate => candidate.Id == lineId)
                    ?? throw new KeyNotFoundException(
                        $"Supplier invoice line {lineId} was not found in invoice {invoiceId}.");

                // 8.175-S5: el ancla puede venir ya resuelta; la re-resolución reconcilia el grupo
                // 1:N previo con las filas reemplazadas explícitas del cliente.
                var wasResolved = line.ResolvedProductId is not null;

                var productsById = await _context.Products
                    .AsNoTracking()
                    .Where(candidate => targetProductIds.Contains(candidate.Id) && !candidate.IsDeleted)
                    .ToDictionaryAsync(candidate => candidate.Id, cancellationToken);
                var targetProducts = new List<Product>(targetProductIds.Count);
                foreach (var targetProductId in targetProductIds)
                {
                    if (!productsById.TryGetValue(targetProductId, out var foundProduct))
                    {
                        throw new KeyNotFoundException($"Product {targetProductId} was not found.");
                    }

                    targetProducts.Add(foundProduct);
                }

                // 8.175-S5: antes de tocar alias se reconcilia el grupo destino: ancla + filas
                // reutilizadas + filas nuevas; las absorbidas sobrantes se eliminan.
                var resolutionSlots = PrepareResolutionSlots(
                    invoice,
                    line,
                    request.ReplacedLineIds,
                    targetProducts.Count);

                // Persistencia del vínculo (aprendizaje de alias 1:N): guarda la relación
                // ProveedorId + Código/Descripción en Factura -> ProductIds para futuras facturas.
                var originalDescription = line.Name?.Trim();
                var editedDescription = request.InvoiceDescription?.Trim();
                await UpsertSupplierProductCodesAsync(
                    invoice.SupplierId,
                    line.SupplierCode,
                    targetProductIds,
                    cancellationToken);

                if (!wasResolved)
                {
                    // Ruta sin resolver: comportamiento vigente (descripción original + editada).
                    if (!string.IsNullOrWhiteSpace(originalDescription)
                        && !string.Equals(originalDescription, line.SupplierCode?.Trim(), StringComparison.Ordinal))
                    {
                        await UpsertSupplierProductCodesAsync(
                            invoice.SupplierId,
                            originalDescription,
                            targetProductIds,
                            cancellationToken);
                    }

                    if (!string.IsNullOrWhiteSpace(editedDescription)
                        && !string.Equals(editedDescription, originalDescription, StringComparison.Ordinal)
                        && !string.Equals(editedDescription, line.SupplierCode?.Trim(), StringComparison.Ordinal))
                    {
                        await UpsertSupplierProductCodesAsync(
                            invoice.SupplierId,
                            editedDescription,
                            targetProductIds,
                            cancellationToken);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(editedDescription)
                    && !string.Equals(editedDescription, line.Name?.Trim(), StringComparison.Ordinal)
                    && !string.Equals(editedDescription, line.SupplierCode?.Trim(), StringComparison.Ordinal))
                {
                    // 8.175-S5: en re-vínculo solo se re-apunta el código de proveedor y, si el
                    // revisor la aportó, la descripción editada. line.Name pertenece al vínculo
                    // anterior (nombre del producto previo) y jamás se aliasea.
                    await UpsertSupplierProductCodesAsync(
                        invoice.SupplierId,
                        editedDescription,
                        targetProductIds,
                        cancellationToken);
                }

                var rememberedFactorsByProductId = await _context.SupplierProductFactors
                    .AsNoTracking()
                    .Where(factor => factor.SupplierId == invoice.SupplierId && targetProductIds.Contains(factor.ProductId))
                    .ToDictionaryAsync(factor => factor.ProductId, factor => factor.ConversionFactor, cancellationToken);
                var marginOverridesByProductId = (request.MarginOverrides ?? Array.Empty<LinkedProductMarginOverrideDto>())
                    .Where(item => item is not null && item.ProductId > 0)
                    .GroupBy(item => item.ProductId)
                    .ToDictionary(group => group.Key, group => group.Last());

                var lineSupplierCode = targetProducts.Count > 1 && string.IsNullOrWhiteSpace(line.SupplierCode)
                    ? (editedDescription ?? originalDescription)
                    : line.SupplierCode;

                for (var index = 0; index < resolutionSlots.Count; index++)
                {
                    var product = targetProducts[index];
                    var currentLine = resolutionSlots[index];

                    currentLine.SupplierCode = lineSupplierCode;
                    if (currentLine.ConversionFactor <= 1m
                        && rememberedFactorsByProductId.TryGetValue(product.Id, out var rememberedFactor)
                        && rememberedFactor > 0m)
                    {
                        currentLine.ConversionFactor = rememberedFactor;
                    }

                    currentLine.UnitCostUSD = PricingCalculator.CalculateInvoiceUnitCostUsd(
                        currentLine.UnitCostDocument,
                        currentLine.ConversionFactor <= 0m ? 1m : currentLine.ConversionFactor,
                        invoice.Currency,
                        invoice.AppliedRate);

                    currentLine.ResolvedProductId = product.Id;
                    currentLine.Name = product.Name;
                    if (targetProducts.Count > 1 || string.IsNullOrWhiteSpace(currentLine.Barcode))
                    {
                        currentLine.Barcode = product.SKU;
                    }

                    currentLine.Status = SupplierInvoiceLineStatus.Update;
                    currentLine.MatchMethod = MatchMethod.SupplierCode;
                    currentLine.OldCostPriceUSD = product.CostPriceUSD;
                    currentLine.OldProfitMarginRetail = product.ProfitMarginRetail;
                    currentLine.OldProfitMarginWholesale = product.ProfitMarginWholesale;
                    currentLine.OldStockQuantity = product.StockQuantity;

                    var retailMargin = product.ProfitMarginRetail == 0m && product.ProfitPercentage > 0m
                        ? product.ProfitPercentage
                        : product.ProfitMarginRetail;
                    var wholesaleMargin = product.HasWholesale || product.ProfitMarginWholesale > 0m
                        ? product.ProfitMarginWholesale
                        : retailMargin;
                    if (marginOverridesByProductId.TryGetValue(product.Id, out var marginOverride))
                    {
                        retailMargin = marginOverride.MarginRetail;
                        wholesaleMargin = marginOverride.MarginWholesale;
                    }

                    currentLine.MarginRetailOverride = retailMargin;
                    currentLine.MarginWholesaleOverride = wholesaleMargin;
                    // 8.184-T2 (S5): la regla de redondeo per-línea reemplaza el valor previo con la
                    // misma semántica que los overrides de margen; null = heredar la regla de la
                    // factura al confirmar (precedencia línea ?? factura, 8.184-S7 — T4).
                    currentLine.RoundingRule = request.RoundingRule;
                    currentLine.SuggestedRetailPriceUSD = PricingCalculator.RoundPriceUp(
                        currentLine.UnitCostUSD * (1m + retailMargin / 100m));
                    currentLine.SuggestedWholesalePriceUSD = PricingCalculator.RoundPriceUp(
                        currentLine.UnitCostUSD * (1m + wholesaleMargin / 100m));
                    currentLine.IsApproved = true;
                }

                await _context.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                _logger.LogInformation(
                    "Linked supplier invoice {InvoiceId} line {LineId} to {ProductCount} existing product(s): {ProductIds} (re-resolution: {IsReResolution}; replaced lines: {ReplacedLineIds}).",
                    invoiceId,
                    lineId,
                    targetProducts.Count,
                    string.Join(",", targetProductIds),
                    wasResolved,
                    request.ReplacedLineIds is { Count: > 0 }
                        ? string.Join(",", request.ReplacedLineIds)
                        : "none");

                return ToDetailDto(invoice);
            }
            catch
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }

                throw;
            }
        });
    }

    private Task UpsertSupplierProductCodeAsync(
        int supplierId,
        string? supplierCode,
        int productId,
        CancellationToken cancellationToken) =>
        UpsertSupplierProductCodesAsync(supplierId, supplierCode, new[] { productId }, cancellationToken);

    private async Task UpsertSupplierProductCodesAsync(
        int supplierId,
        string? supplierCode,
        IReadOnlyList<int> productIds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(supplierCode) || productIds.Count == 0)
        {
            return;
        }

        var code = supplierCode.Trim();
        var existingRows = await _context.SupplierProductCodes
            .Where(candidate => candidate.SupplierId == supplierId && candidate.Code == code)
            .OrderBy(candidate => candidate.Id)
            .ToListAsync(cancellationToken);

        var targetSet = productIds.ToHashSet();
        var coveredProductIds = new HashSet<int>();
        var reusableRows = new Queue<SupplierProductCode>();
        foreach (var row in existingRows)
        {
            if (targetSet.Contains(row.ProductId) && coveredProductIds.Add(row.ProductId))
            {
                continue;
            }

            reusableRows.Enqueue(row);
        }

        foreach (var productId in productIds)
        {
            if (!coveredProductIds.Add(productId))
            {
                continue;
            }

            if (reusableRows.TryDequeue(out var reusableRow))
            {
                reusableRow.ProductId = productId;
            }
            else
            {
                _context.SupplierProductCodes.Add(new SupplierProductCode
                {
                    SupplierId = supplierId,
                    Code = code,
                    ProductId = productId
                });
            }
        }

        while (reusableRows.TryDequeue(out var leftoverRow))
        {
            _context.SupplierProductCodes.Remove(leftoverRow);
        }
    }
}
