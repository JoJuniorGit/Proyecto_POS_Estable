using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.DTOs;
using Microsoft.EntityFrameworkCore;
using Sales.Module.DTOs;
using Sales.Module.Entities;

namespace Sales.Module.Services;

public partial class SalesService
{
    public async Task<SaleHistoryDto> ConfirmPickupAsync(int saleId, int? actingUserId = null, System.Threading.CancellationToken cancellationToken = default)
    {
        // 8.188 (PERF-F2-02): fetch liviano solo de Items para validación preliminar
        // sin disparar split queries innecesarias de Customer, Cashier y Payments descartadas antes de DeliverPartialAsync.
        var sale = await _context.Sales
            .AsNoTracking()
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.Id == saleId, cancellationToken);

        if (sale == null) throw new KeyNotFoundException("Venta no encontrada.");

        EnsureHoldClaimAccess(sale, actingUserId);

        var invalidPickupMessage = $"El pedido #{sale.InvoiceNumber ?? sale.Id} no se encuentra en estado Pendiente por Retirar.";
        if (sale.Status != SaleStatus.Completed
            || (sale.DeliveryStatus != SaleDeliveryStatus.PendingPickup
                && sale.DeliveryStatus != SaleDeliveryStatus.PartiallyDelivered))
        {
            throw new InvalidOperationException(invalidPickupMessage);
        }

        var pendingItems = sale.Items
            .Select(item => (SaleItemId: item.Id, Quantity: item.Quantity - item.DeliveredQuantity))
            .Where(item => item.Quantity > 0m)
            .ToList();
        if (pendingItems.Count == 0)
        {
            throw new InvalidOperationException(invalidPickupMessage);
        }

        await DeliverPartialAsync(saleId, pendingItems, null, actingUserId, cancellationToken);

        var updatedSale = await _context.Sales
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.Customer)
            .Include(s => s.Cashier)
            .Include(s => s.Items)
            .Include(s => s.Payments).ThenInclude(p => p.PaymentMethod)
            .FirstOrDefaultAsync(s => s.Id == saleId, cancellationToken);

        if (updatedSale == null) throw new KeyNotFoundException("Venta no encontrada.");
        return SalesHistoryService.MapToHistoryDetail(updatedSale);
    }

    // 8.205: la vertical de consultas vive en SalesHistoryService; la fachada conserva el
    // contrato histórico de ISalesService para no romper clientes ni tests.
    public Task<IEnumerable<PendingPickupDto>> GetPendingPickupsAsync(
        int? cashierId = null,
        int limit = 200,
        int offset = 0,
        System.Threading.CancellationToken cancellationToken = default)
        => _history.GetPendingPickupsAsync(cashierId, limit, offset, cancellationToken);

    public Task<int> CountPendingPickupsAsync(
        int? cashierId = null,
        System.Threading.CancellationToken cancellationToken = default)
        => _history.CountPendingPickupsAsync(cashierId, cancellationToken);

    public Task<(IEnumerable<SaleHistoryDto> Items, int TotalCount)> GetSalesHistoryAsync(
        int page,
        int pageSize,
        DateTime? startDate,
        DateTime? endDate,
        string? search = null,
        int? cashierId = null,
        System.Threading.CancellationToken cancellationToken = default)
        => _history.GetSalesHistoryAsync(page, pageSize, startDate, endDate, search, cashierId, cancellationToken);

    public Task<SaleHistoryDto> GetSaleHistoryDetailAsync(
        int saleId,
        System.Threading.CancellationToken cancellationToken = default)
        => _history.GetSaleHistoryDetailAsync(saleId, cancellationToken);
}
