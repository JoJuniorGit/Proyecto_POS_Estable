using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
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

    /// <summary>
    /// 8.177-T1: proyección única de productos para la resolución/consolidación de deducciones y
    /// el snapshot histórico de costo del Kardex (el producto destino de una variante con stock
    /// compartido es su padre).
    /// </summary>
    private static readonly Expression<Func<Product, StockDeductionProductInfo>> StockDeductionProductProjection =
        p => new StockDeductionProductInfo
        {
            Id = p.Id,
            Name = p.Name,
            SKU = p.SKU,
            IsCashAdvance = p.IsCashAdvance,
            ConversionFactor = p.ConversionFactor,
            ParentId = p.ParentProductId,
            ParentIsStockShared = p.ParentProduct != null && p.ParentProduct.IsStockShared,
            ParentName = p.ParentProduct != null ? p.ParentProduct.Name : null,
            ParentSKU = p.ParentProduct != null ? p.ParentProduct.SKU : null,
            // 8.177-T1: costo del producto destino para el snapshot histórico del Kardex.
            CostPriceUSD = p.CostPriceUSD
        };

    public async Task UpdateStockBatchAsync(IEnumerable<StockDeductionRequest> items, string? userId = null, bool allowNegativeStock = false, System.Threading.CancellationToken cancellationToken = default)
    {
        var itemList = items?.Where(i => i.QuantityChange != 0).ToList();
        if (itemList == null || itemList.Count == 0) return;

        // 8.16-H02: la transacción manual debe vivir DENTRO de CreateExecutionStrategy().ExecuteAsync();
        // de lo contrario NpgsqlRetryingExecutionStrategy lanza InvalidOperationException en producción
        // cuando UpdateStockBatchAsync se invoca SIN una transacción ambiente (p. ej. desde el
        // InventorySaleMadeEventHandler post-commit). Cuando el caller ya enroló una tx (main sales
        // flow), hasExistingTx=true y no se abre una nueva (sin retry por estar en tx ambiente).
        await _context.Database.CreateExecutionStrategy().ExecuteAsync(async _ =>
        {
            bool hasExistingTx = _context.Database.CurrentTransaction != null;
            await using var tx = (!hasExistingTx && _context.Database.IsRelational()) ? await _context.Database.BeginTransactionAsync(cancellationToken) : null;

            var productIds = itemList.Select(i => i.ProductId).Distinct().ToList();
            var productsData = await _context.Products
                .AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .Select(StockDeductionProductProjection)
                .ToDictionaryAsync(p => p.Id, cancellationToken);

            // 8.177-T1 (fix verificación independiente): una variante con stock compartido tiene como
            // producto DESTINO a su PADRE; se cargan los padres faltantes para que el snapshot de
            // costo del movimiento (y todo lookup por TargetProductId) resuelva sin null — el costo
            // histórico solo existe en el momento del movimiento y no es backfilleable.
            var missingParentIds = productsData.Values
                .Where(info => info.ParentId.HasValue
                    && info.ParentIsStockShared
                    && !productsData.ContainsKey(info.ParentId.Value))
                .Select(info => info.ParentId!.Value)
                .Distinct()
                .ToList();
            if (missingParentIds.Count > 0)
            {
                var parentInfos = await _context.Products
                    .AsNoTracking()
                    .Where(p => missingParentIds.Contains(p.Id))
                    .Select(StockDeductionProductProjection)
                    .ToListAsync(cancellationToken);
                foreach (var parentInfo in parentInfos)
                {
                    productsData[parentInfo.Id] = parentInfo;
                }
            }

            // 8.149-T1 (SRE-01): resolver + consolidar ANTES de tocar stock: una deducción por
            // (target, razón, venta) con la cantidad sumada, ordenada ascendente por ProductId, para
            // que lotes concurrentes adquieran los row locks en la misma secuencia y sin perder los
            // totales por producto del comportamiento legado (cualquier orden de entrada).
            var plan = StockDeductionConsolidator.ResolveAndConsolidate(itemList, productsData);
            if (plan.Deductions.Count == 0) return;

            var updatedTargetIds = new HashSet<int>();

            foreach (var deduction in plan.Deductions)
            {
                if (_context.Database.IsRelational())
                {
                    if (deduction.QuantityChange < 0 && !allowNegativeStock)
                    {
                        // Conditional decrement preventing negative stock
                        int rows = await _context.Products
                            .Where(p => p.Id == deduction.TargetProductId && (p.StockQuantity + deduction.QuantityChange) >= 0)
                            .ExecuteUpdateAsync(setters => setters
                                .SetProperty(p => p.StockQuantity, p => p.StockQuantity + deduction.QuantityChange)
                                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), cancellationToken);

                        if (rows == 0)
                        {
                            var targetProd = await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == deduction.TargetProductId, cancellationToken);
                            if (targetProd == null) throw new KeyNotFoundException($"Producto {deduction.TargetProductId} no encontrado");
                            throw new InvalidOperationException($"Stock insuficiente para el producto '{targetProd.Name}' (SKU: {targetProd.SKU}). Stock actual: {targetProd.StockQuantity}, deducción requerida: {Math.Abs(deduction.QuantityChange)}.");
                        }
                    }
                    else
                    {
                        // Unconditional increment or sale deduction allowing negative stock
                        int rows = await _context.Products
                            .Where(p => p.Id == deduction.TargetProductId)
                            .ExecuteUpdateAsync(setters => setters
                                .SetProperty(p => p.StockQuantity, p => p.StockQuantity + deduction.QuantityChange)
                                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), cancellationToken);

                        if (rows == 0) throw new KeyNotFoundException($"Producto {deduction.TargetProductId} no encontrado");
                    }

                    updatedTargetIds.Add(deduction.TargetProductId);
                }
                else
                {
                    // In-Memory Database execution for unit tests
                    var targetProd = await _context.Products.FindAsync(new object[] { deduction.TargetProductId }, cancellationToken);
                    if (targetProd == null) throw new KeyNotFoundException($"Producto {deduction.TargetProductId} no encontrado");

                    if (deduction.QuantityChange < 0 && !allowNegativeStock && (targetProd.StockQuantity + deduction.QuantityChange) < 0)
                    {
                        throw new InvalidOperationException($"Stock insuficiente para el producto '{targetProd.Name}' (SKU: {targetProd.SKU}). Stock actual: {targetProd.StockQuantity}, deducción requerida: {Math.Abs(deduction.QuantityChange)}.");
                    }

                    targetProd.StockQuantity += deduction.QuantityChange;
                    targetProd.UpdatedAt = DateTime.UtcNow;
                }
            }

            // 8I-M4: NewStockLevel se resuelve con UNA relectura batch (WHERE Id IN ...) en lugar
            // de una consulta por producto. En memoria se lee directamente de la entidad trackeada.
            var stockByProductId = new Dictionary<int, decimal>();
            if (_context.Database.IsRelational() && updatedTargetIds.Count > 0)
            {
                var updatedStocks = await _context.Products
                    .AsNoTracking()
                    .Where(p => updatedTargetIds.Contains(p.Id))
                    .Select(p => new { p.Id, p.StockQuantity })
                    .ToListAsync(cancellationToken);

                foreach (var s in updatedStocks)
                {
                    stockByProductId[s.Id] = s.StockQuantity;
                }
            }

            // 8.180-T1: el plan consolidado no transporta el vínculo con la sesión de conteo físico
            // (el consolidador agrupa por target/razón/venta); se resuelve aquí desde los requests
            // originales usando el producto DESTINO ya calculado por el resolver.
            var stockCountSessionByTarget = new Dictionary<int, int>();
            foreach (var item in itemList)
            {
                if (item.StockCountSessionId is not int stockCountSessionId) continue;
                int targetProductId = productsData.TryGetValue(item.ProductId, out var requestInfo)
                    && requestInfo.ParentId.HasValue
                    && requestInfo.ParentIsStockShared
                        ? requestInfo.ParentId.Value
                        : item.ProductId;
                stockCountSessionByTarget.TryAdd(targetProductId, stockCountSessionId);
            }

            var movements = new List<StockMovement>();
            foreach (var deduction in plan.Deductions)
            {
                decimal stockAfter;
                if (_context.Database.IsRelational())
                {
                    stockAfter = stockByProductId.TryGetValue(deduction.TargetProductId, out var s) ? s : 0m;
                }
                else
                {
                    var inMemoryTarget = await _context.Products.FindAsync(new object[] { deduction.TargetProductId }, cancellationToken);
                    stockAfter = inMemoryTarget?.StockQuantity ?? 0m;
                }

                movements.Add(new StockMovement
                {
                    ProductId = deduction.TargetProductId,
                    QuantityChange = deduction.QuantityChange,
                    NewStockLevel = stockAfter,
                    Reason = deduction.Reason,
                    // 8.16-H03: idempotencia estructurada por SaleId (si el caller lo provee).
                    SaleId = deduction.SaleId,
                    MovementDate = DateTime.UtcNow,
                    UserId = _currentUserService?.UserId ?? userId,
                    // 8.177-T1: defaults centrales de auditoría. Una deducción con SaleId sin
                    // clasificación explícita es Sistema/Venta: así los productores cuyo archivo
                    // tiene WIP ajeno (Checkout/HoldOrders en Sales.Module, 8.177-L3) quedan
                    // cubiertos sin editarlos.
                    OriginType = deduction.OriginType ?? (deduction.SaleId.HasValue ? StockMovementOriginType.System : null),
                    ReasonCode = deduction.ReasonCode ?? (deduction.SaleId.HasValue ? StockMovementReason.Sale : null),
                    AuthorizedByUserId = deduction.AuthorizedByUserId,
                    SupplierInvoiceId = deduction.SupplierInvoiceId,
                    Observation = deduction.Observation,
                    // 8.180-T1: vínculo con la sesión de conteo físico (cuando el request lo trae).
                    StockCountSessionId = stockCountSessionByTarget.TryGetValue(deduction.TargetProductId, out var movementStockCountSessionId)
                        ? movementStockCountSessionId
                        : (int?)null,
                    // 8.177-T1: snapshot del costo del producto DESTINO del movimiento (para una
                    // variante con stock compartido, el destino es el padre).
                    UnitCostUSD = productsData.TryGetValue(deduction.TargetProductId, out var targetInfo)
                        ? targetInfo.CostPriceUSD
                        : null
                });
            }

            _context.StockMovements.AddRange(movements);
            await _context.SaveChangesAsync(cancellationToken);

            if (tx != null)
            {
                await tx.CommitAsync(cancellationToken);
            }

            foreach (var sku in plan.SkusToInvalidate)
            {
                InvalidateProductSkuCache(sku);
            }
            InvalidateAllProductCaches();
        }, cancellationToken);
    }

    public async Task UpdateStockAsync(int productId, decimal quantityChange, string reason, string? userId = null, bool allowNegativeStock = false, System.Threading.CancellationToken cancellationToken = default)
    {
        await UpdateStockBatchAsync(new[] { new StockDeductionRequest(productId, quantityChange, reason) }, userId, allowNegativeStock, cancellationToken);
    }

    public async Task AdjustStockAsync(int productId, decimal quantityChange, string reason, string? userId = null, StockMovementReason? reasonCode = null, string? observation = null, string? authorizedByUserId = null, System.Threading.CancellationToken cancellationToken = default)
    {
        EnsureCatalogMutationPermission();
        if (quantityChange == 0) return;

        // 8.177-T1: los campos de auditoría son OPCIONALES en este tramo (el cliente Desktop
        // actual no los envía; T3 los exige en la UI). Se validan solo cuando llegan.
        ValidateManualAdjustmentAudit(reasonCode, ref observation, ref authorizedByUserId);

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
            .FirstOrDefaultAsync(cancellationToken);

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

        // 8.177-T1: cuando llegan datos de auditoría, el movimiento se clasifica Manual; sin ellos
        // el movimiento legado queda sin clasificar (derivación en la capa de consulta, T2).
        var originType = reasonCode.HasValue || observation is not null || authorizedByUserId is not null
            ? StockMovementOriginType.Manual
            : (StockMovementOriginType?)null;

        await UpdateStockBatchAsync(
            new[]
            {
                new StockDeductionRequest(
                    productId,
                    quantityChange,
                    $"Ajuste Manual: {reason}",
                    SaleId: null,
                    OriginType: originType,
                    ReasonCode: reasonCode,
                    AuthorizedByUserId: authorizedByUserId,
                    SupplierInvoiceId: null,
                    Observation: observation)
            },
            userId,
            allowNegativeStock: false,
            cancellationToken);
    }

    // 8.177-T1: tope del texto de observación del ajuste manual (contrato del AdjustStockDto).
    private const int MaxManualAdjustmentObservationLength = 500;

    /// <summary>
    /// 8.177-T1: valida los datos de auditoría del ajuste manual SOLO cuando llegan y normaliza
    /// los textos (trim) antes de persistirlos.
    /// </summary>
    private static void ValidateManualAdjustmentAudit(StockMovementReason? reasonCode, ref string? observation, ref string? authorizedByUserId)
    {
        if (reasonCode.HasValue && !Enum.IsDefined(reasonCode.Value))
        {
            throw new ArgumentException("El motivo estandarizado del ajuste no es válido.", nameof(reasonCode));
        }

        if (observation is not null)
        {
            observation = observation.Trim();
            if (observation.Length == 0)
            {
                throw new ArgumentException("La observación del ajuste no puede estar vacía.", nameof(observation));
            }

            if (observation.Length > MaxManualAdjustmentObservationLength)
            {
                throw new ArgumentException($"La observación del ajuste no puede exceder los {MaxManualAdjustmentObservationLength} caracteres.", nameof(observation));
            }
        }

        if (authorizedByUserId is not null)
        {
            authorizedByUserId = authorizedByUserId.Trim();
            if (authorizedByUserId.Length == 0)
            {
                throw new ArgumentException("El usuario autorizador no puede estar vacío.", nameof(authorizedByUserId));
            }
        }
    }

    public async Task<int> ReserveStockAsync(int productId, decimal quantity, TimeSpan duration, string? referenceId = null, System.Threading.CancellationToken cancellationToken = default)
    {
        var product = await _context.Products.FindAsync(new object[] { productId }, cancellationToken);
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
            var parent = await _context.Products.FindAsync(new object[] { product.ParentProductId.Value }, cancellationToken);
            if (parent != null && parent.IsStockShared)
            {
                targetProduct = parent;
                sourceProductId = product.Id;
                decimal factor = product.ConversionFactor > 0 ? product.ConversionFactor : 1.0000m;
                effectiveQuantity = ApplyConversion(quantity, factor, isDeduction: true);
            }
        }

        if (!_context.Database.IsRelational())
        {
            if ((targetProduct.StockQuantity - targetProduct.ReservedQuantity) < effectiveQuantity)
            {
                throw new InvalidOperationException("Stock insuficiente disponible.");
            }
            targetProduct.ReservedQuantity += effectiveQuantity;

            var inMemoryReservation = new StockReservation
            {
                ProductId = targetProduct.Id,
                SourceProductId = sourceProductId,
                Quantity = effectiveQuantity,
                ExpiryDate = DateTime.UtcNow.Add(duration),
                IsConfirmed = false,
                ReferenceId = referenceId
            };

            _context.StockReservations.Add(inMemoryReservation);
            await _context.SaveChangesAsync(cancellationToken);
            InvalidateProductSkuCache(targetProduct.SKU);
            return inMemoryReservation.Id;
        }

        int reservationId = 0;
        bool hasExistingTransaction = _context.Database.CurrentTransaction != null;

        async Task ReserveWithinTransactionAsync()
        {
            await using var transaction = hasExistingTransaction ? null : await _context.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                int updated = await _context.Products
                    .Where(p => p.Id == targetProduct.Id && (p.StockQuantity - p.ReservedQuantity) >= effectiveQuantity)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.ReservedQuantity, p => p.ReservedQuantity + effectiveQuantity)
                        .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), cancellationToken);

                if (updated == 0)
                {
                    throw new InvalidOperationException("Stock insuficiente disponible.");
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
                await _context.SaveChangesAsync(cancellationToken);

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                reservationId = reservation.Id;
            }
            catch
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                throw;
            }
        }

        if (hasExistingTransaction)
        {
            await ReserveWithinTransactionAsync();
        }
        else
        {
            await _context.Database.CreateExecutionStrategy().ExecuteAsync(async _ => await ReserveWithinTransactionAsync(), cancellationToken);
        }

        InvalidateProductSkuCache(targetProduct.SKU);
        return reservationId;
    }

    public async Task ConfirmReservationAsync(int reservationId, string reason, System.Threading.CancellationToken cancellationToken = default)
    {
        if (reservationId == 0) return; // Ignore service reservations

        var reservation = await _context.StockReservations
            .AsNoTracking()
            .Include(r => r.Product)
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

        if (reservation == null) throw new KeyNotFoundException("Reserva no encontrada.");
        if (reservation.IsConfirmed) return; // Already confirmed

        // 8.7-L3: confirmación atómica y condicional. Dos ejecuciones concurrentes no pueden
        // descontar el stock dos veces ni dejar ReservedQuantity negativo.
        // 8.16-H02: la transacción manual debe vivir DENTRO de CreateExecutionStrategy().ExecuteAsync()
        // para no lanzar InvalidOperationException bajo NpgsqlRetryingExecutionStrategy en producción.
        if (_context.Database.IsRelational())
        {
            await _context.Database.CreateExecutionStrategy().ExecuteAsync(async _ =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

                // 1) Reclamación atómica: solo la primera confirmación gana.
                int claimed = await _context.StockReservations
                    .Where(r => r.Id == reservationId && !r.IsConfirmed)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsConfirmed, true), cancellationToken);

                if (claimed == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return; // Confirmación concurrente: otra request ya la confirmó.
                }

                // 2) Descuento condicional: no permite quedarse en negativo.
                int updated = await _context.Products
                    .Where(p => p.Id == reservation.ProductId && p.ReservedQuantity >= reservation.Quantity)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.StockQuantity, p => p.StockQuantity - reservation.Quantity)
                        .SetProperty(p => p.ReservedQuantity, p => p.ReservedQuantity - reservation.Quantity), cancellationToken);

                if (updated == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw new InvalidOperationException("El stock fue modificado concurrentemente. Por favor intente nuevamente.");
                }

                // Re-read the actual stock level after ExecuteUpdate to get the authoritative value.
                var actualStock = await _context.Products
                    .Where(p => p.Id == reservation.ProductId)
                    .Select(p => p.StockQuantity)
                    .FirstOrDefaultAsync(cancellationToken);

                // 3) Movimiento de inventario y eliminación de la reserva.
                var movement = new StockMovement
                {
                    ProductId = reservation.ProductId,
                    QuantityChange = -reservation.Quantity,
                    NewStockLevel = actualStock,
                    Reason = $"Confirmed Reservation {reservationId}: {reason}",
                    MovementDate = DateTime.UtcNow
                };
                _context.StockMovements.Add(movement);
                await _context.StockReservations.Where(r => r.Id == reservationId).ExecuteDeleteAsync(cancellationToken);

                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }, cancellationToken);
        }
        else
        {
            // InMemory (tests): cargar entidad TRACKEADA (Remove requiere tracking en el provider).
            var trackedReservation = await _context.StockReservations
                .Include(r => r.Product)
                .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

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

            await _context.SaveChangesAsync(cancellationToken);
            InvalidateProductSkuCache(trackedReservation.Product.SKU);
            return;
        }

        InvalidateProductSkuCache(reservation.Product.SKU);
    }

    public async Task CancelReservationAsync(int reservationId, System.Threading.CancellationToken cancellationToken = default)
    {
        if (reservationId == 0) return; // Ignore service reservations

        if (_context.Database.IsRelational())
        {
            async Task CancelRelationalCoreAsync()
            {
                await using var transaction = _context.Database.CurrentTransaction is null
                    ? await _context.Database.BeginTransactionAsync(cancellationToken)
                    : null;

                var relationalReservation = await _context.StockReservations
                    .AsNoTracking()
                    .Include(r => r.Product)
                    .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

                if (relationalReservation == null) return;

                var deleted = await _context.StockReservations
                    .Where(r => r.Id == reservationId)
                    .ExecuteDeleteAsync(cancellationToken);

                if (deleted == 0) return;

                await _context.Products
                    .Where(p => p.Id == relationalReservation.ProductId)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(
                            p => p.ReservedQuantity,
                            p => p.ReservedQuantity > relationalReservation.Quantity ? p.ReservedQuantity - relationalReservation.Quantity : 0m),
                        cancellationToken);

                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                if (relationalReservation.Product != null)
                {
                    InvalidateProductSkuCache(relationalReservation.Product.SKU);
                }
            }

            if (_context.Database.CurrentTransaction is null)
            {
                await _context.Database.CreateExecutionStrategy().ExecuteAsync(CancelRelationalCoreAsync);
            }
            else
            {
                await CancelRelationalCoreAsync();
            }

            return;
        }

        var reservation = await _context.StockReservations
            .Include(r => r.Product)
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

        if (reservation == null) return; // Already gone

        reservation.Product.ReservedQuantity = Math.Max(0, reservation.Product.ReservedQuantity - reservation.Quantity);
        _context.StockReservations.Remove(reservation);

        await _context.SaveChangesAsync(cancellationToken);
        if (reservation.Product != null)
        {
            InvalidateProductSkuCache(reservation.Product.SKU);
        }
    }
}
