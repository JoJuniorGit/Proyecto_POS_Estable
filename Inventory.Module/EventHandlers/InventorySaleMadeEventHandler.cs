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

        foreach (var item in notification.Items)
        {
            try
            {
                // Idempotency check: verify if this sale stock deduction was already processed
                var alreadyProcessed = await _context.StockMovements
                    .AsNoTracking()
                    .AnyAsync(sm => sm.Reason.Contains(reason) && (sm.ProductId == item.ProductId || sm.Reason.Contains("Variante:")), cancellationToken);

                if (alreadyProcessed)
                {
                    _logger?.LogInformation("[InventoryHandler] Deducción ya procesada previamente para producto {ProductId} en Venta #{SaleId}. Omitiendo.", item.ProductId, notification.SaleId);
                    continue;
                }

                // We use negative quantity to deduct stock without fractional truncation, allowing negative stock on sales
                await _inventoryService.UpdateStockAsync(item.ProductId, -item.Quantity, reason, allowNegativeStock: true);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[InventoryHandler] Fallo temporal al deducir stock para producto {ProductId} en Venta #{SaleId}. Reintentando...", item.ProductId, notification.SaleId);

                bool success = false;
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        await Task.Delay(500, cancellationToken);
                        await _inventoryService.UpdateStockAsync(item.ProductId, -item.Quantity, reason, allowNegativeStock: true);
                        success = true;
                        break;
                    }
                    catch { /* ignore inner exceptions during retries */ }
                }

                if (!success)
                {
                    _logger?.LogCritical("[InventoryHandler] CRÍTICO: Fallaron todos los reintentos para deducir stock del producto {ProductId} en Venta #{SaleId}.", item.ProductId, notification.SaleId);
                }
            }
        }
    }
}
