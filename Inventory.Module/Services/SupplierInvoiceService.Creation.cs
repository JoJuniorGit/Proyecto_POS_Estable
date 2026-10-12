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

                if (line.ResolvedProductId is not null)
                {
                    throw new InvalidOperationException(
                        $"Supplier invoice line {lineId} already resolves to a product.");
                }

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
                    "Created product {ProductId} (SKU {Sku}) from supplier invoice {InvoiceId} line {LineId}.",
                    created.Id,
                    created.SKU,
                    invoiceId,
                    lineId);

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
        if (request.ProductId <= 0)
        {
            throw new ArgumentException("A valid existing product ID is required.", nameof(request));
        }

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

                if (line.ResolvedProductId is not null)
                {
                    throw new InvalidOperationException(
                        $"Supplier invoice line {lineId} already resolves to a product.");
                }

                var product = await _context.Products
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        candidate => candidate.Id == request.ProductId && !candidate.IsDeleted,
                        cancellationToken)
                    ?? throw new KeyNotFoundException($"Product {request.ProductId} was not found.");

                // Persistencia del vínculo (aprendizaje de alias): guarda la relación
                // ProveedorId + Código/Descripción en Factura -> ProductId para futuras facturas.
                var originalDescription = line.Name?.Trim();
                var editedDescription = request.InvoiceDescription?.Trim();
                await UpsertSupplierProductCodeAsync(
                    invoice.SupplierId,
                    line.SupplierCode,
                    product.Id,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(originalDescription)
                    && !string.Equals(originalDescription, line.SupplierCode?.Trim(), StringComparison.Ordinal))
                {
                    await UpsertSupplierProductCodeAsync(
                        invoice.SupplierId,
                        originalDescription,
                        product.Id,
                        cancellationToken);
                }

                if (!string.IsNullOrWhiteSpace(editedDescription)
                    && !string.Equals(editedDescription, originalDescription, StringComparison.Ordinal)
                    && !string.Equals(editedDescription, line.SupplierCode?.Trim(), StringComparison.Ordinal))
                {
                    await UpsertSupplierProductCodeAsync(
                        invoice.SupplierId,
                        editedDescription,
                        product.Id,
                        cancellationToken);
                }

                var rememberedFactor = await _context.SupplierProductFactors
                    .AsNoTracking()
                    .Where(factor => factor.SupplierId == invoice.SupplierId && factor.ProductId == product.Id)
                    .Select(factor => (decimal?)factor.ConversionFactor)
                    .FirstOrDefaultAsync(cancellationToken);
                if (line.ConversionFactor <= 1m && rememberedFactor is > 0m)
                {
                    line.ConversionFactor = rememberedFactor.Value;
                }

                line.UnitCostUSD = PricingCalculator.CalculateInvoiceUnitCostUsd(
                    line.UnitCostDocument,
                    line.ConversionFactor <= 0m ? 1m : line.ConversionFactor,
                    invoice.Currency,
                    invoice.AppliedRate);

                line.ResolvedProductId = product.Id;
                line.Name = product.Name;
                if (string.IsNullOrWhiteSpace(line.Barcode))
                {
                    line.Barcode = product.SKU;
                }

                line.Status = SupplierInvoiceLineStatus.Update;
                line.MatchMethod = MatchMethod.SupplierCode;
                line.OldCostPriceUSD = product.CostPriceUSD;
                line.OldProfitMarginRetail = product.ProfitMarginRetail;
                line.OldProfitMarginWholesale = product.ProfitMarginWholesale;
                line.OldStockQuantity = product.StockQuantity;

                var retailMargin = product.ProfitMarginRetail == 0m && product.ProfitPercentage > 0m
                    ? product.ProfitPercentage
                    : product.ProfitMarginRetail;
                var wholesaleMargin = product.HasWholesale
                    ? product.ProfitMarginWholesale
                    : retailMargin;
                line.MarginRetailOverride = retailMargin;
                line.MarginWholesaleOverride = wholesaleMargin;
                line.SuggestedRetailPriceUSD = PricingCalculator.RoundPriceUp(
                    line.UnitCostUSD * (1m + retailMargin / 100m));
                line.SuggestedWholesalePriceUSD = PricingCalculator.RoundPriceUp(
                    line.UnitCostUSD * (1m + wholesaleMargin / 100m));
                line.IsApproved = true;

                await _context.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                _logger.LogInformation(
                    "Linked supplier invoice {InvoiceId} line {LineId} to existing product {ProductId} (SKU {Sku}).",
                    invoiceId,
                    lineId,
                    product.Id,
                    product.SKU);

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

    private async Task UpsertSupplierProductCodeAsync(
        int supplierId,
        string? supplierCode,
        int productId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(supplierCode))
        {
            return;
        }

        var code = supplierCode.Trim();
        var existing = await _context.SupplierProductCodes
            .FirstOrDefaultAsync(
                candidate => candidate.SupplierId == supplierId && candidate.Code == code,
                cancellationToken);

        if (existing is null)
        {
            _context.SupplierProductCodes.Add(new SupplierProductCode
            {
                SupplierId = supplierId,
                Code = code,
                ProductId = productId
            });
        }
        else if (existing.ProductId != productId)
        {
            // 8.146-D8: last-write-wins hacia el producto humano-confirmado.
            existing.ProductId = productId;
        }
    }
}
