using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.Services;

public partial class InventoryService
{
    public static decimal ApplyConversion(decimal quantity, decimal factor, bool isDeduction)
    {
        decimal raw = quantity * (factor > 0 ? factor : 1.0000m);
        return Math.Round(raw, 4, MidpointRounding.AwayFromZero);
    }

    public async Task UpdateStockBatchAsync(IEnumerable<StockDeductionRequest> items, string? userId = null, bool allowNegativeStock = false)
    {
        var itemList = items?.Where(i => i.QuantityChange != 0).ToList();
        if (itemList == null || itemList.Count == 0) return;

        bool hasExistingTx = _context.Database.CurrentTransaction != null;
        await using var tx = (!hasExistingTx && _context.Database.IsRelational()) ? await _context.Database.BeginTransactionAsync() : null;

        var productIds = itemList.Select(i => i.ProductId).Distinct().ToList();
        var productsData = await _context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.SKU,
                p.IsCashAdvance,
                p.ConversionFactor,
                ParentId = p.ParentProductId,
                ParentIsStockShared = p.ParentProduct != null && p.ParentProduct.IsStockShared,
                ParentName = p.ParentProduct != null ? p.ParentProduct.Name : null,
                ParentSKU = p.ParentProduct != null ? p.ParentProduct.SKU : null
            })
            .ToDictionaryAsync(p => p.Id);

        var movements = new List<StockMovement>();
        var skusToInvalidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in itemList)
        {
            if (!productsData.TryGetValue(item.ProductId, out var productData))
            {
                throw new KeyNotFoundException($"Product {item.ProductId} not found");
            }

            if (productData.IsCashAdvance)
            {
                throw new InvalidOperationException($"El producto '{productData.Name}' es un servicio de adelanto de efectivo y no maneja inventario físico.");
            }

            int targetProductId = item.ProductId;
            decimal effectiveQuantityChange = item.QuantityChange;
            string movementReason = item.Reason;

            if (productData.ParentId.HasValue && productData.ParentIsStockShared)
            {
                targetProductId = productData.ParentId.Value;
                decimal factor = productData.ConversionFactor > 0 ? productData.ConversionFactor : 1.0000m;
                effectiveQuantityChange = ApplyConversion(item.QuantityChange, factor, item.QuantityChange < 0);
                movementReason = $"Variante: {productData.Name} (SKU: {productData.SKU}, Factor: {factor:G29}) | {item.Reason}";
            }

            decimal newStockLevel;

            if (_context.Database.IsRelational())
            {
                if (effectiveQuantityChange < 0 && !allowNegativeStock)
                {
                    // Conditional decrement preventing negative stock
                    int rows = await _context.Products
                        .Where(p => p.Id == targetProductId && (p.StockQuantity + effectiveQuantityChange) >= 0)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(p => p.StockQuantity, p => p.StockQuantity + effectiveQuantityChange)
                            .SetProperty(p => p.UpdatedAt, DateTime.UtcNow));

                    if (rows == 0)
                    {
                        var targetProd = await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == targetProductId);
                        if (targetProd == null) throw new KeyNotFoundException($"Product {targetProductId} not found");
                        throw new InvalidOperationException($"Stock insuficiente para el producto '{targetProd.Name}' (SKU: {targetProd.SKU}). Stock actual: {targetProd.StockQuantity}, deducción requerida: {Math.Abs(effectiveQuantityChange)}.");
                    }
                }
                else
                {
                    // Unconditional increment or sale deduction allowing negative stock
                    int rows = await _context.Products
                        .Where(p => p.Id == targetProductId)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(p => p.StockQuantity, p => p.StockQuantity + effectiveQuantityChange)
                            .SetProperty(p => p.UpdatedAt, DateTime.UtcNow));

                    if (rows == 0) throw new KeyNotFoundException($"Product {targetProductId} not found");
                }

                var updatedTarget = await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == targetProductId);
                newStockLevel = updatedTarget?.StockQuantity ?? 0m;
            }
            else
            {
                // In-Memory Database execution for unit tests
                var targetProd = await _context.Products.FindAsync(targetProductId);
                if (targetProd == null) throw new KeyNotFoundException($"Product {targetProductId} not found");

                if (effectiveQuantityChange < 0 && !allowNegativeStock && (targetProd.StockQuantity + effectiveQuantityChange) < 0)
                {
                    throw new InvalidOperationException($"Stock insuficiente para el producto '{targetProd.Name}' (SKU: {targetProd.SKU}). Stock actual: {targetProd.StockQuantity}, deducción requerida: {Math.Abs(effectiveQuantityChange)}.");
                }

                targetProd.StockQuantity += effectiveQuantityChange;
                targetProd.UpdatedAt = DateTime.UtcNow;
                newStockLevel = targetProd.StockQuantity;
            }

            movements.Add(new StockMovement
            {
                ProductId = targetProductId,
                QuantityChange = effectiveQuantityChange,
                NewStockLevel = newStockLevel,
                Reason = movementReason,
                MovementDate = DateTime.UtcNow,
                UserId = _currentUserService?.UserId ?? userId
            });

            if (!string.IsNullOrWhiteSpace(productData.SKU)) skusToInvalidate.Add(productData.SKU);
            if (!string.IsNullOrWhiteSpace(productData.ParentSKU)) skusToInvalidate.Add(productData.ParentSKU);
        }

        _context.StockMovements.AddRange(movements);
        await _context.SaveChangesAsync();

        if (tx != null)
        {
            await tx.CommitAsync();
        }

        foreach (var sku in skusToInvalidate)
        {
            InvalidateProductSkuCache(sku);
        }
        InvalidateAllProductCaches();
    }

    public async Task UpdateStockAsync(int productId, decimal quantityChange, string reason, string? userId = null, bool allowNegativeStock = false)
    {
        await UpdateStockBatchAsync(new[] { new StockDeductionRequest(productId, quantityChange, reason) }, userId, allowNegativeStock);
    }

    public async Task AdjustStockAsync(int productId, decimal quantityChange, string reason, string? userId = null)
    {
        EnsureCatalogMutationPermission();
        if (quantityChange == 0) return;

        var product = await _context.Products
            .AsNoTracking()
            .Where(p => p.Id == productId)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.SKU,
                p.IsDeleted,
                p.IsCashAdvance,
                p.IsGroupHeader,
                p.IsStockShared,
                ParentId = p.ParentProductId,
                ParentIsStockShared = p.ParentProduct != null && p.ParentProduct.IsStockShared,
                ParentName = p.ParentProduct != null ? p.ParentProduct.Name : null
            })
            .FirstOrDefaultAsync();

        if (product == null) throw new KeyNotFoundException($"Product {productId} not found");

        if (product.IsDeleted)
        {
            throw new InvalidOperationException(Core.Constants.InventoryMessages.DeletedProductStockAdjustmentBlocked);
        }

        if (product.IsCashAdvance)
        {
            throw new InvalidOperationException(Core.Constants.InventoryMessages.CashAdvanceStockAdjustmentBlocked);
        }

        if (product.IsGroupHeader && !product.IsStockShared)
        {
            throw new InvalidOperationException(Core.Constants.InventoryMessages.GroupIndividualStockAdjustmentBlocked);
        }

        if (product.ParentId.HasValue && product.ParentIsStockShared)
        {
            throw new InvalidOperationException(Core.Constants.InventoryMessages.VariantSharedStockAdjustmentBlocked);
        }

        await UpdateStockAsync(productId, quantityChange, $"Ajuste Manual: {reason}", userId, allowNegativeStock: false);
    }

    public async Task<int> ReserveStockAsync(int productId, decimal quantity, TimeSpan duration, string? referenceId = null)
    {
        var product = await _context.Products.FindAsync(productId);
        if (product == null) throw new KeyNotFoundException($"Product {productId} not found");

        if (product.IsCashAdvance)
        {
            return 0; // Bypass reservation for Cash Advance / services
        }

        var targetProduct = product;
        int? sourceProductId = null;
        decimal effectiveQuantity = quantity;

        if (product.ParentProductId.HasValue)
        {
            var parent = await _context.Products.FindAsync(product.ParentProductId.Value);
            if (parent != null && parent.IsStockShared)
            {
                targetProduct = parent;
                sourceProductId = product.Id;
                decimal factor = product.ConversionFactor > 0 ? product.ConversionFactor : 1.0000m;
                effectiveQuantity = ApplyConversion(quantity, factor, isDeduction: true);
            }
        }

        // Check availability and reserve atomically in relational database
        if (_context.Database.IsRelational())
        {
            int updated = await _context.Products
                .Where(p => p.Id == targetProduct.Id && (p.StockQuantity - p.ReservedQuantity) >= effectiveQuantity)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.ReservedQuantity, p => p.ReservedQuantity + effectiveQuantity)
                    .SetProperty(p => p.UpdatedAt, DateTime.UtcNow));

            if (updated == 0)
            {
                throw new InvalidOperationException("Not enough stock available.");
            }
        }
        else
        {
            if ((targetProduct.StockQuantity - targetProduct.ReservedQuantity) < effectiveQuantity)
            {
                throw new InvalidOperationException("Not enough stock available.");
            }
            targetProduct.ReservedQuantity += effectiveQuantity;
        }

        var reservation = new StockReservation
        {
            ProductId = targetProduct.Id,
            SourceProductId = sourceProductId,
            Quantity = effectiveQuantity,
            ExpiryDate = DateTime.UtcNow.Add(duration),
            IsConfirmed = false,
            ReferenceId = referenceId
        };

        _context.StockReservations.Add(reservation);

        try
        {
            await _context.SaveChangesAsync();
            InvalidateProductSkuCache(targetProduct.SKU);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (_context.Database.IsRelational())
            {
                await _context.Products
                    .Where(p => p.Id == targetProduct.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.ReservedQuantity, p => p.ReservedQuantity - effectiveQuantity));
            }
            throw new InvalidOperationException("Stock changed concurrently. Please try again.");
        }

        return reservation.Id;
    }

    public async Task ConfirmReservationAsync(int reservationId, string reason)
    {
        if (reservationId == 0) return; // Ignore service reservations

        var reservation = await _context.StockReservations
            .AsNoTracking()
            .Include(r => r.Product)
            .FirstOrDefaultAsync(r => r.Id == reservationId);

        if (reservation == null) throw new KeyNotFoundException("Reservation not found.");
        if (reservation.IsConfirmed) return; // Already confirmed

        // 8.7-L3: confirmación atómica y condicional. Dos ejecuciones concurrentes no pueden
        // descontar el stock dos veces ni dejar ReservedQuantity negativo.
        if (_context.Database.IsRelational())
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();

            // 1) Reclamación atómica: solo la primera confirmación gana.
            int claimed = await _context.StockReservations
                .Where(r => r.Id == reservationId && !r.IsConfirmed)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsConfirmed, true));

            if (claimed == 0)
            {
                await transaction.RollbackAsync();
                return; // Confirmación concurrente: otra request ya la confirmó.
            }

            // 2) Descuento condicional: no permite quedarse en negativo.
            int updated = await _context.Products
                .Where(p => p.Id == reservation.ProductId && p.ReservedQuantity >= reservation.Quantity)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.StockQuantity, p => p.StockQuantity - reservation.Quantity)
                    .SetProperty(p => p.ReservedQuantity, p => p.ReservedQuantity - reservation.Quantity));

            if (updated == 0)
            {
                await transaction.RollbackAsync();
                throw new InvalidOperationException("Stock changed concurrently. Please try again.");
            }

            reservation.Product.StockQuantity -= reservation.Quantity;
            reservation.Product.ReservedQuantity = Math.Max(0, reservation.Product.ReservedQuantity - reservation.Quantity);

            // 3) Movimiento de inventario y eliminación de la reserva.
            var movement = new StockMovement
            {
                ProductId = reservation.ProductId,
                QuantityChange = -reservation.Quantity,
                NewStockLevel = reservation.Product.StockQuantity,
                Reason = $"Confirmed Reservation {reservationId}: {reason}",
                MovementDate = DateTime.UtcNow
            };
            _context.StockMovements.Add(movement);
            await _context.StockReservations.Where(r => r.Id == reservationId).ExecuteDeleteAsync();

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        else
        {
            // InMemory (tests): cargar entidad TRACKEADA (Remove requiere tracking en el provider).
            var trackedReservation = await _context.StockReservations
                .Include(r => r.Product)
                .FirstOrDefaultAsync(r => r.Id == reservationId);

            if (trackedReservation == null || trackedReservation.IsConfirmed) return;

            trackedReservation.Product.StockQuantity -= trackedReservation.Quantity;
            trackedReservation.Product.ReservedQuantity = Math.Max(0, trackedReservation.Product.ReservedQuantity - trackedReservation.Quantity);
            trackedReservation.IsConfirmed = true;

            var movement = new StockMovement
            {
                ProductId = trackedReservation.ProductId,
                QuantityChange = -trackedReservation.Quantity,
                NewStockLevel = trackedReservation.Product.StockQuantity,
                Reason = $"Confirmed Reservation {reservationId}: {reason}",
                MovementDate = DateTime.UtcNow
            };
            _context.StockMovements.Add(movement);
            _context.StockReservations.Remove(trackedReservation);

            await _context.SaveChangesAsync();
            InvalidateProductSkuCache(trackedReservation.Product.SKU);
            return;
        }

        InvalidateProductSkuCache(reservation.Product.SKU);
    }

    public async Task CancelReservationAsync(int reservationId)
    {
        if (reservationId == 0) return; // Ignore service reservations

        var reservation = await _context.StockReservations
            .Include(r => r.Product)
            .FirstOrDefaultAsync(r => r.Id == reservationId);

        if (reservation == null) return; // Already gone

        if (_context.Database.IsRelational())
        {
            await _context.Products
                .Where(p => p.Id == reservation.ProductId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ReservedQuantity, p => p.ReservedQuantity > reservation.Quantity ? p.ReservedQuantity - reservation.Quantity : 0));
        }
        else
        {
            reservation.Product.ReservedQuantity = Math.Max(0, reservation.Product.ReservedQuantity - reservation.Quantity);
        }

        _context.StockReservations.Remove(reservation);

        await _context.SaveChangesAsync();
        if (reservation.Product != null)
        {
            InvalidateProductSkuCache(reservation.Product.SKU);
        }
    }
}
