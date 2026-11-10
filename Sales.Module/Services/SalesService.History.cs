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
        // PERF-R03: proyección liviana para la validación previa. No se materializa el grafo
        // completo de la venta; DeliverPartialAsync hace su propia carga transaccional.
        var pickup = await _context.Sales
            .AsNoTracking()
            .Where(s => s.Id == saleId)
            .Select(s => new
            {
                s.Id,
                s.InvoiceNumber,
                s.Status,
                s.DeliveryStatus,
                s.ClaimedByUserId,
                s.ClaimedByUserName,
                s.ClaimAction,
                s.ClaimedAtUtc,
                PendingItems = s.Items
                    .Where(item => item.Quantity > item.DeliveredQuantity)
                    .Select(item => new { item.Id, item.Quantity, item.DeliveredQuantity })
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (pickup == null) throw new KeyNotFoundException("Venta no encontrada.");

        // El guard de custodia se evalúa con los campos proyectados: no hace falta cargar el
        // grafo completo para distinguir un pedido reclamado por otro cajero.
        EnsureHoldClaimAccess(new Sale
        {
            Id = pickup.Id,
            Status = pickup.Status,
            ClaimedByUserId = pickup.ClaimedByUserId,
            ClaimedByUserName = pickup.ClaimedByUserName,
            ClaimAction = pickup.ClaimAction,
            ClaimedAtUtc = pickup.ClaimedAtUtc
        }, actingUserId);

        var invalidPickupMessage = $"El pedido #{pickup.InvoiceNumber ?? pickup.Id} no se encuentra en estado Pendiente por Retirar.";
        if (pickup.Status != SaleStatus.Completed
            || (pickup.DeliveryStatus != SaleDeliveryStatus.PendingPickup
                && pickup.DeliveryStatus != SaleDeliveryStatus.PartiallyDelivered))
        {
            throw new InvalidOperationException(invalidPickupMessage);
        }

        var pendingItems = pickup.PendingItems
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
