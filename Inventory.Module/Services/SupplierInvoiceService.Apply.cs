using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.Services;

public partial class SupplierInvoiceService
{
    private readonly ICurrentUserService? _currentUserService;

    public SupplierInvoiceService(
        InventoryDbContext context,
        ISystemSettingsService systemSettingsService,
        ISupplierProductSimilaritySearch similaritySearch,
        ICurrentUserService currentUserService)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(systemSettingsService);
        ArgumentNullException.ThrowIfNull(similaritySearch);
        ArgumentNullException.ThrowIfNull(currentUserService);

        _context = context;
        _systemSettingsService = systemSettingsService;
        _similaritySearch = similaritySearch;
        _currentUserService = currentUserService;
    }

    public Task<SupplierInvoiceDetailDto> ConfirmAsync(
        int invoiceId,
        ConfirmSupplierInvoiceRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Lines);
        if (request.Lines.Any(line => line is null))
        {
            throw new ArgumentException("The confirmation contains a null line.", nameof(request));
        }

        if (request.Lines.Select(line => line.LineId).Distinct().Count() != request.Lines.Count)
        {
            throw new ArgumentException("The confirmation contains duplicate supplier invoice line IDs.", nameof(request));
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

                var lineIds = invoice.Lines.Select(line => line.Id).ToHashSet();
                if (request.Lines.Any(confirmation => !lineIds.Contains(confirmation.LineId)))
                {
                    throw new ArgumentException("A confirmed line does not belong to this supplier invoice.", nameof(request));
                }

                var confirmationsByLineId = request.Lines.ToDictionary(confirmation => confirmation.LineId);
                foreach (var line in invoice.Lines.OrderBy(candidate => candidate.Id))
                {
                    if (!confirmationsByLineId.TryGetValue(line.Id, out var confirmation))
                    {
                        line.IsApproved = false;
                        continue;
                    }

                    line.IsApproved = confirmation.IsApproved;
                    if (!confirmation.IsApproved)
                    {
                        continue;
                    }

                    ValidateApprovedLine(line, confirmation);
                    line.MarginRetailOverride = confirmation.MarginRetailOverride;
                    line.MarginWholesaleOverride = confirmation.MarginWholesaleOverride;
                    if (line.Status == SupplierInvoiceLineStatus.Conflict || line.ResolvedProductId is not int productId)
                    {
                        continue;
                    }

                    await ApplyApprovedLineAsync(invoice.Id, line, productId, confirmation, cancellationToken);
                }

                invoice.Status = SupplierInvoiceStatus.Applied;
                invoice.AppliedAt = DateTime.UtcNow;
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

    private async Task ApplyApprovedLineAsync(
        int invoiceId,
        SupplierInvoiceLine line,
        int productId,
        ConfirmLineDto confirmation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var product = await _context.Products
                .FirstOrDefaultAsync(candidate => candidate.Id == productId && !candidate.IsDeleted, cancellationToken)
                ?? throw new KeyNotFoundException($"Product {productId} was not found or has been deleted.");

            var retailMargin = confirmation.MarginRetailOverride ?? product.ProfitMarginRetail;
            var wholesaleMargin = product.HasWholesale
                ? confirmation.MarginWholesaleOverride ?? product.ProfitMarginWholesale
                : retailMargin;

            product.CostPriceUSD = line.UnitCostUSD;
            product.ProfitMarginRetail = retailMargin;
            product.ProfitMarginWholesale = wholesaleMargin;
            product.ProfitPercentage = retailMargin;
            product.PriceRetailUSD = PricingCalculator.RoundPriceUp(line.UnitCostUSD * (1m + retailMargin / 100m));
            product.PriceUSD = product.PriceRetailUSD;
            product.PriceWholesaleUSD = PricingCalculator.RoundPriceUp(line.UnitCostUSD * (1m + wholesaleMargin / 100m));
            product.StockQuantity += line.Quantity;
            product.UpdatedAt = DateTime.UtcNow;

            var movement = new StockMovement
            {
                Product = product,
                ProductId = product.Id,
                QuantityChange = line.Quantity,
                NewStockLevel = product.StockQuantity,
                Reason = $"Supplier invoice {invoiceId} applied",
                MovementDate = DateTime.UtcNow,
                UserId = _currentUserService?.UserId
            };
            _context.StockMovements.Add(movement);

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException exception) when (
                attempt == 0 && exception.Entries.Any(entry => entry.Entity is Product))
            {
                // 8.144: el savepoint revierte la fila insertada en el intento fallido, pero el
                // tracker puede dejarla como guardada; se descarta y el reintento crea otra.
                if (_context.Entry(movement).State != EntityState.Detached)
                {
                    _context.Entry(movement).State = EntityState.Detached;
                }

                _context.Entry(product).State = EntityState.Detached;
            }
        }
    }

    private static void ValidateApprovedLine(SupplierInvoiceLine line, ConfirmLineDto confirmation)
    {
        if (line.UnitCostUSD < 0m)
        {
            throw new ArgumentException("Approved supplier invoice unit cost cannot be negative.", nameof(line));
        }

        if (line.Quantity < 0m)
        {
            throw new ArgumentException("Approved supplier invoice quantity cannot be negative.", nameof(line));
        }

        if (confirmation.MarginRetailOverride is < 0m || confirmation.MarginWholesaleOverride is < 0m)
        {
            throw new ArgumentException("Approved supplier invoice margin overrides cannot be negative.", nameof(confirmation));
        }
    }
}
