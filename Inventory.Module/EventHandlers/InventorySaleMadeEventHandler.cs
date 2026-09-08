using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Core.Events;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inventory.Module.EventHandlers;

public class InventorySaleMadeEventHandler : INotificationHandler<SaleMadeEvent>
{
    private readonly IInventoryService _inventoryService;
    private readonly InventoryDbContext _context;
    private readonly ILogger<InventorySaleMadeEventHandler>? _logger;

    public InventorySaleMadeEventHandler(
        IInventoryService inventoryService,
        InventoryDbContext context,
        ILogger<InventorySaleMadeEventHandler>? logger = null)
    {
        _inventoryService = inventoryService;
        _context = context;
        _logger = logger;
    }

    public async Task Handle(SaleMadeEvent notification, CancellationToken cancellationToken)
    {
        var reason = $"Sale #{notification.SaleId}";
        string? invoiceReason = notification.InvoiceNumber.HasValue ? $"Sale #{notification.InvoiceNumber.Value}" : null;

        // 8.7-B4: una única carga de productos para todos los ítems (sin N+1).
        var requestedIds = notification.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await _context.Products
            .AsNoTracking()
            .Include(p => p.ParentProduct)
            .Where(p => requestedIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        // Idempotencia en lote (H-INV-1 / DST-1): movimientos ya registrados para estos productos
        // con reason o invoiceReason (exacto o tras el sufijo "| " de variantes).
        var effectiveIds = new HashSet<int>(requestedIds);
        foreach (var item in notification.Items)
        {
            if (products.TryGetValue(item.ProductId, out var product)
                && product.ParentProductId.HasValue
                && product.ParentProduct != null
                && product.ParentProduct.IsStockShared)
            {
                effectiveIds.Add(product.ParentProductId.Value);
            }
        }

        var existingMovements = await _context.StockMovements
            .AsNoTracking()
            .Where(sm =>
                effectiveIds.Contains(sm.ProductId)
                && (sm.SaleId == notification.SaleId
                    || sm.Reason == reason
                    || sm.Reason.EndsWith($"| {reason}")
                    || (invoiceReason != null && (sm.Reason == invoiceReason || sm.Reason.EndsWith($"| {invoiceReason}")))))
            .Select(sm => new { sm.ProductId, sm.Reason, sm.SaleId })
            .ToListAsync(cancellationToken);

        // Elegibilidad por ítem: omitir ya procesados (no depender de índices) y servicios sin stock.
        var pending = new List<Core.Interfaces.StockDeductionRequest>();

        foreach (var item in notification.Items)
        {
            var product = products.TryGetValue(item.ProductId, out var prod) ? prod : null;

            int effectiveProductId = item.ProductId;
            if (product?.ParentProductId.HasValue == true && product.ParentProduct != null && product.ParentProduct.IsStockShared)
            {
                effectiveProductId = product.ParentProductId.Value;
            }

            if (product != null && product.IsCashAdvance)
            {
                continue;
            }

            var already = existingMovements.Any(m =>
                (m.ProductId == item.ProductId || m.ProductId == effectiveProductId)
                && (m.SaleId == notification.SaleId
                    || m.Reason == reason
                    || m.Reason.EndsWith($"| {reason}")
                    || (invoiceReason != null && (m.Reason == invoiceReason || m.Reason.EndsWith($"| {invoiceReason}")))));

            if (already)
            {
                _logger?.LogInformation("[InventoryHandler] Deducción ya procesada previamente para producto {ProductId} en Venta #{SaleId} (Factura #{InvoiceNumber}). Omitiendo.", item.ProductId, notification.SaleId, notification.InvoiceNumber);
                continue;
            }

            pending.Add(new Core.Interfaces.StockDeductionRequest(item.ProductId, -item.Quantity, invoiceReason ?? reason, notification.SaleId));
        }

        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            await _inventoryService.UpdateStockBatchAsync(pending, allowNegativeStock: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[InventoryHandler] Fallo temporal al deducir stock (lote de {Count} productos) en Venta #{SaleId}. Reintentando...", pending.Count, notification.SaleId);

            bool success = false;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    await Task.Delay(500, cancellationToken);
                    await _inventoryService.UpdateStockBatchAsync(pending, allowNegativeStock: true);
                    success = true;
                    break;
                }
                catch (Exception retryEx)
                {
                    _logger?.LogDebug(retryEx, "[InventoryHandler] Reintento {Attempt} falló al deducir stock para Venta #{SaleId} ({Count} productos)", i + 1, notification.SaleId, pending.Count);
                }
            }

            if (!success)
            {
                _logger?.LogCritical("[InventoryHandler] CRÍTICO: Fallaron todos los reintentos para deducir stock de {Count} productos en Venta #{SaleId}.", pending.Count, notification.SaleId);
            }
        }
    }
}