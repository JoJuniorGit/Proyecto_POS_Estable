using System.Text.RegularExpressions;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;

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
