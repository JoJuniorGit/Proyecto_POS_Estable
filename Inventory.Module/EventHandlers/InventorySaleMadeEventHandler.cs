using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Core.Events;
using Inventory.Module.Services;

namespace Inventory.Module.EventHandlers;

public class InventorySaleMadeEventHandler : INotificationHandler<SaleMadeEvent>
{
    private readonly Core.Interfaces.IInventoryService _inventoryService;

    public InventorySaleMadeEventHandler(Core.Interfaces.IInventoryService inventoryService)
    {
        _inventoryService = inventoryService;
    }

    public async Task Handle(SaleMadeEvent notification, CancellationToken cancellationToken)
    {
        foreach (var item in notification.Items)
        {
            try
            {
                // We use negative quantity to deduct stock
                await _inventoryService.UpdateStockAsync(item.ProductId, (int)-item.Quantity, $"Sale #{notification.SaleId}");
            }
            catch (Exception ex)
            {
                // Fault management: Log the exception.
                // In a robust system, we would queue this for retry or store in an Inbox pattern.
                // For now, we write to console to avoid crashing the event bus or halting other handlers.
                Console.WriteLine($"[Inventory] Failed to deduct stock for Product {item.ProductId} in Sale {notification.SaleId}: {ex.Message}");

                // Depending on the strictness required, we might throw to indicate failure,
                // but usually Events (Choreography) imply eventual consistency and isolated failure.
                // Re-throwing might fail the Sale transaction if the EventBus is synchronous.
                // The prompt says "design retry mechanisms". We can simulate retry logic here.

                // For a basic 'retry' mechanism without Polly, just do a simple loop:
                bool success = false;
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        Console.WriteLine($"[Inventory] Retrying stock deduction for Product {item.ProductId}...");
                        await Task.Delay(1000, cancellationToken);
                        await _inventoryService.UpdateStockAsync(item.ProductId, (int)-item.Quantity, $"Sale #{notification.SaleId} (Retry {i + 1})");
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
