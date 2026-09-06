using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Core.Events;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inventory.Module.EventHandlers;

public class InventorySaleMadeEventHandler : INotificationHandler<SaleMadeEvent>
{
    private readonly Core.Interfaces.IInventoryService _inventoryService;
    private readonly InventoryDbContext _context;
    private readonly ILogger<InventorySaleMadeEventHandler>? _logger;

    public InventorySaleMadeEventHandler(
        Core.Interfaces.IInventoryService inventoryService, 
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

        foreach (var item in notification.Items)
        {
            try
            {
                var product = await _context.Products
                    .AsNoTracking()
                    .Include(p => p.ParentProduct)
                    .FirstOrDefaultAsync(p => p.Id == item.ProductId, cancellationToken);

                if (product != null && product.IsCashAdvance)
                {
                    _logger?.LogInformation("[InventoryHandler] Producto {ProductId} es servicio de adelanto de efectivo. Omitiendo deducción física de stock.", item.ProductId);
                    continue;
                }

                int effectiveProductId = (product?.ParentProductId.HasValue == true && product.ParentProduct != null && product.ParentProduct.IsStockShared)
                    ? product.ParentProductId.Value
                    : item.ProductId;

                // Idempotency check: exact reason/suffix match to prevent "Sale #1" false-matching "Sale #11" (H-INV-1)
                // Chequea tanto Sale #{SaleId} como Sale #{InvoiceNumber} (DST-1: previene doble deducción cuando SaleId != InvoiceNumber)
                var alreadyProcessed = await _context.StockMovements
                    .AsNoTracking()
                    .AnyAsync(sm => (sm.Reason == reason 
                                     || sm.Reason.EndsWith($"| {reason}")
                                     || (invoiceReason != null && (sm.Reason == invoiceReason || sm.Reason.EndsWith($"| {invoiceReason}")))) 
                                    && (sm.ProductId == item.ProductId || sm.ProductId == effectiveProductId), cancellationToken);

                if (alreadyProcessed)
                {
                    _logger?.LogInformation("[InventoryHandler] Deducción ya procesada previamente para producto {ProductId} en Venta #{SaleId} (Factura #{InvoiceNumber}). Omitiendo.", item.ProductId, notification.SaleId, notification.InvoiceNumber);
                    continue;
                }

                // Usamos invoiceReason si está disponible para máxima coherencia con SalesService, o reason como fallback
                var deductionReason = invoiceReason ?? reason;
                await _inventoryService.UpdateStockAsync(item.ProductId, -item.Quantity, deductionReason, allowNegativeStock: true);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[InventoryHandler] Fallo temporal al deducir stock para producto {ProductId} en Venta #{SaleId}. Reintentando...", item.ProductId, notification.SaleId);

                var deductionReason = invoiceReason ?? reason;
                bool success = false;
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        await Task.Delay(500, cancellationToken);
                        await _inventoryService.UpdateStockAsync(item.ProductId, -item.Quantity, deductionReason, allowNegativeStock: true);
                        success = true;
                        break;
                    }
                    catch (Exception retryEx)
                    {
                        _logger?.LogDebug(retryEx, "[InventoryHandler] Reintento {Attempt} falló al deducir stock para producto {ProductId} en Venta #{SaleId}", i + 1, item.ProductId, notification.SaleId);
                    }
                }

                if (!success)
                {
                    _logger?.LogCritical("[InventoryHandler] CRÍTICO: Fallaron todos los reintentos para deducir stock del producto {ProductId} en Venta #{SaleId}.", item.ProductId, notification.SaleId);
                }
            }
        }
    }
}
