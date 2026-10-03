using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Sales.Module.DTOs;
using Sales.Module.Entities;

namespace Sales.Module.Services;

public partial class SalesService
{
    private const string EmptyDeliveryItemsMessage = "Debe indicar al menos una cantidad a entregar.";

    public async Task<DeliveryReceiptDto> DeliverPartialAsync(
        int saleId,
        IReadOnlyList<(int SaleItemId, decimal Quantity)> items,
        string? notes,
        int? actingUserId = null,
        CancellationToken cancellationToken = default,
        string? idempotencyKey = null,
        byte[]? idempotencyPayloadHash = null,
        string? requestPath = null)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sale = await _context.Sales
                .AsSplitQuery()
                .Include(s => s.Items)
                .Include(s => s.Customer)
                .FirstOrDefaultAsync(s => s.Id == saleId, cancellationToken);

            if (sale == null) throw new KeyNotFoundException("Venta no encontrada.");

            if (sale.Status != SaleStatus.Completed
                || (sale.DeliveryStatus != SaleDeliveryStatus.PendingPickup
                    && sale.DeliveryStatus != SaleDeliveryStatus.PartiallyDelivered))
            {
                throw new InvalidOperationException("La venta no se encuentra en estado de retiro pendiente.");
            }

            EnsureHoldClaimAccess(sale, actingUserId);

            if (items is null || items.Count == 0 || items.Any(item => item.Quantity <= 0m))
            {
                throw new ArgumentException(EmptyDeliveryItemsMessage);
            }

            var requestedItemIds = new HashSet<int>();
            var requestedItems = new List<(SaleItem Item, decimal Quantity)>(items.Count);
            foreach (var requestedItem in items)
            {
                if (!requestedItemIds.Add(requestedItem.SaleItemId))
                {
                    throw new ArgumentException("La solicitud contiene artículos duplicados.");
                }

                var saleItem = sale.Items.FirstOrDefault(item => item.Id == requestedItem.SaleItemId);
                if (saleItem == null)
                {
                    throw new ArgumentException($"El artículo {requestedItem.SaleItemId} no pertenece a la venta.");
                }

                var pendingQuantity = saleItem.Quantity - saleItem.DeliveredQuantity;
                if (requestedItem.Quantity > pendingQuantity)
                {
                    throw new ArgumentException($"La cantidad a entregar supera la cantidad pendiente del producto {saleItem.ProductName}.");
                }

                requestedItems.Add((saleItem, requestedItem.Quantity));
            }

            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken)
                : null;

            var deliveredByName = await ResolveDeliveryUserNameAsync(actingUserId, cancellationToken);
            var deliveredAt = DateTime.UtcNow;
            foreach (var (item, quantity) in requestedItems)
            {
                item.DeliveredQuantity += quantity;
            }

            var delivery = new SaleDelivery
            {
                SaleId = sale.Id,
                DeliveredAt = deliveredAt,
                DeliveredByUserId = actingUserId,
                DeliveredByName = deliveredByName,
                Notes = notes,
                Items = requestedItems.Select(requestedItem => new SaleDeliveryItem
                {
                    SaleItemId = requestedItem.Item.Id,
                    ProductId = requestedItem.Item.ProductId,
                    ProductName = requestedItem.Item.ProductName,
                    QuantityDelivered = requestedItem.Quantity
                }).ToList()
            };
            _context.SaleDeliveries.Add(delivery);

            bool fullyDelivered = sale.Items.All(item => item.DeliveredQuantity == item.Quantity);
            if (fullyDelivered)
            {
                sale.DeliveryStatus = SaleDeliveryStatus.Delivered;
                sale.PickupDate = deliveredAt;
            }
            else
            {
                sale.DeliveryStatus = SaleDeliveryStatus.PartiallyDelivered;
                sale.PickupDate = null;
            }

            await _context.SaveChangesAsync(cancellationToken);

            var totalUnits = sale.Items.Sum(item => item.Quantity);
            var deliveredUnits = sale.Items.Sum(item => item.DeliveredQuantity);
            var receipt = new DeliveryReceiptDto
            {
                DeliveryId = delivery.Id,
                SaleId = sale.Id,
                InvoiceNumber = sale.InvoiceNumber,
                DeliveredAt = deliveredAt,
                DeliveredByName = deliveredByName,
                CustomerName = sale.CustomerName ?? sale.Customer?.Name,
                CustomerCedula = sale.CustomerCedula ?? sale.Customer?.CedulaOrRif,
                DeliveryStatus = sale.DeliveryStatus.ToString(),
                TotalUnits = totalUnits,
                DeliveredUnits = deliveredUnits,
                PendingUnits = totalUnits - deliveredUnits,
                Items = requestedItems.Select(requestedItem => new DeliveryReceiptItemDto
                {
                    SaleItemId = requestedItem.Item.Id,
                    ProductId = requestedItem.Item.ProductId,
                    ProductName = requestedItem.Item.ProductName,
                    QuantityDelivered = requestedItem.Quantity,
                    UnitPrice = requestedItem.Item.UnitPrice,
                    UnitPriceBsS = requestedItem.Item.UnitPriceBsS,
                    SubtotalBsS = Math.Round(
                        requestedItem.Quantity * requestedItem.Item.UnitPriceBsS,
                        2,
                        MidpointRounding.AwayFromZero)
                    }).ToList()
            };

            if (!string.IsNullOrWhiteSpace(idempotencyKey) && idempotencyPayloadHash != null)
            {
                RegisterIdempotencyRecord(
                    idempotencyKey,
                    idempotencyPayloadHash,
                    requestPath ?? $"/api/sales/{saleId}/deliveries",
                    JsonSerializer.Serialize(receipt, JsonSerializerOptions.Web),
                    actingUserId);
                await _context.SaveChangesAsync(cancellationToken);
            }

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return receipt;
        });
    }

    private async Task<string> ResolveDeliveryUserNameAsync(int? actingUserId, CancellationToken cancellationToken)
    {
        if (!actingUserId.HasValue) return "Usuario Desconocido";

        var user = await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == actingUserId.Value, cancellationToken);
        if (user == null) return "Usuario Desconocido";
        if (!string.IsNullOrWhiteSpace(user.Name)) return user.Name;
        if (!string.IsNullOrWhiteSpace(user.FullName)) return user.FullName;
        return string.IsNullOrWhiteSpace(user.Cedula) ? "Usuario Desconocido" : user.Cedula;
    }
}
