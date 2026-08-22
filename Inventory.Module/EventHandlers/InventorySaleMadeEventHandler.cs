using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Core.Events;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.EventHandlers;

public class InventorySaleMadeEventHandler : INotificationHandler<SaleMadeEvent>
{
    private readonly Core.Interfaces.IInventoryService _inventoryService;
    private readonly InventoryDbContext _context;

    public InventorySaleMadeEventHandler(Core.Interfaces.IInventoryService inventoryService, InventoryDbContext context)
    {
        _inventoryService = inventoryService;
        _context = context;
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
                    .AnyAsync(sm => sm.ProductId == item.ProductId && sm.Reason == reason, cancellationToken);

                if (alreadyProcessed)
                {
                    Console.WriteLine($"[Inventory] Idempotency notice: Stock deduction for Product {item.ProductId} in Sale {notification.SaleId} already processed. Skipping.");
                    continue;
                }

                // We use negative quantity to deduct stock without fractional truncation
                await _inventoryService.UpdateStockAsync(item.ProductId, -item.Quantity, reason);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Inventory] Failed to deduct stock for Product {item.ProductId} in Sale {notification.SaleId}: {ex.Message}");

                bool success = false;
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        Console.WriteLine($"[Inventory] Retrying stock deduction for Product {item.ProductId}...");
                        await Task.Delay(500, cancellationToken);
                        await _inventoryService.UpdateStockAsync(item.ProductId, -item.Quantity, reason);
                        success = true;
                        break;
                    }
                    catch { /* ignore inner exceptions during retries */ }
                }

                if (!success)
                {
                    Console.WriteLine($"[Inventory] CRITICAL: Failed all retries for stock deduction of Product {item.ProductId}, Sale {notification.SaleId}.");
                }
            }
        }
    }
}
