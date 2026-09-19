using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Events;
using Core.Helpers;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Sales.Module.DTOs;
using Sales.Module.Entities;

namespace Sales.Module.Services;

public partial class SalesService
{

    public async Task<SaleDto> HoldSaleAsync(int saleId, HoldSaleRequestDto request, string? idempotencyKey = null, byte[]? idempotencyPayloadHash = null, int? actingUserId = null, System.Threading.CancellationToken cancellationToken = default)
    {
        var sale = await GetSaleEntityAsync(saleId);
        EnsureHoldClaimAccess(sale, actingUserId);
        if (sale.Status != SaleStatus.Pending && sale.Status != SaleStatus.OnHold)
            throw new InvalidOperationException("Solo se pueden poner en espera ventas pendientes o abiertas.");

        var customer = await _context.Customers.FindAsync(new object[] { request.CustomerId }, cancellationToken);
        if (customer == null) throw new KeyNotFoundException($"Cliente con ID {request.CustomerId} no encontrado.");
        
        if (customer.IsDefault || customer.CedulaOrRif == "V-00000000")
            throw new ArgumentException("Las ventas en espera requieren un cliente real identificable. Asigne un cliente distinto al Consumidor Final.");

        // 8.6-B3: La tasa del HOLD se ancla a la tasa BCV del día (misma política que CompleteSale).
        sale.AppliedRate = await ResolveAnchoredRateAsync(request.ExchangeRate, contextLabel: "HoldSale", referenceId: sale.Id);
        await RecalculateTotalAsync(sale);

        var paymentsToProcess = new List<AddPaymentRequestDto>();
        if (request.InitialPayment != null) paymentsToProcess.Add(request.InitialPayment);
        if (request.InitialPayments != null && request.InitialPayments.Any()) paymentsToProcess.AddRange(request.InitialPayments);

        if (paymentsToProcess.Any())
        {
            var pMethodIds = paymentsToProcess.Select(p => p.PaymentMethodId).Distinct().ToList();
            var pMethodsDict = await _context.PaymentMethods.Where(pm => pMethodIds.Contains(pm.Id)).ToDictionaryAsync(pm => pm.Id, cancellationToken);

            foreach (var payment in paymentsToProcess)
            {
                // 8.7-B2: Rechazo de montos negativos en abonos (misma política que CompleteSale).
                if (payment.AmountUSD < 0m || payment.AmountBsS < 0m)
                {
                    throw new ArgumentException($"La validación del abono (PaymentMethodId={payment.PaymentMethodId}) rechaza montos negativos.");
                }

                decimal rate = payment.ExchangeRate > 0
                    ? await ResolveAnchoredRateAsync(payment.ExchangeRate, contextLabel: "HoldSaleInitialPayment", referenceId: sale.Id)
                    : sale.AppliedRate;
                decimal amountUsd = payment.AmountUSD > 0 
                    ? Math.Round(payment.AmountUSD, 2, MidpointRounding.AwayFromZero) 
                    : (rate > 0 ? Math.Round(payment.AmountBsS / rate, 2, MidpointRounding.AwayFromZero) : 0m);

                var (resolvedAmountUsd, resolvedAmountBsS) = ResolveConsistentPaymentAmounts(amountUsd, payment.AmountBsS, rate);

                var initialPaymentEntity = new SalePayment
                {
                    SaleId = sale.Id,
                    PaymentMethodId = payment.PaymentMethodId,
                    Amount = Math.Round(resolvedAmountUsd, 2, MidpointRounding.AwayFromZero),
                    AmountBsS = Math.Round(resolvedAmountBsS, 2, MidpointRounding.AwayFromZero),
                    ExchangeRate = rate,
                    ReferenceNumber = payment.ReferenceNumber,
                    CreatedAt = DateTime.UtcNow
                };

                sale.Payments.Add(initialPaymentEntity);

                // H-SAL-5: Asentar ingresos físicos en sesión activa de caja
                if (resolvedAmountUsd > 0 && pMethodsDict.TryGetValue(payment.PaymentMethodId, out var pMethod) && pMethod.IsCash && _cashDrawerService != null)
                {
                    var activeSession = await _cashDrawerService.GetOrCreateActiveSessionAsync(rate);
                    if (activeSession != null)
                    {
                        _context.CashTransactions.Add(new CashTransaction
                        {
                            SessionId = activeSession.Id,
                            Type = CashTransactionType.Income,
                            Source = CashTransactionSource.SalePayment,
                            AmountUsd = resolvedAmountUsd,
                            ExchangeRate = rate,
                            AmountLocal = Math.Round(resolvedAmountBsS, 2, MidpointRounding.AwayFromZero),
                            IsPhysicalCash = true,
                            Description = $"Abono Inicial Venta #{sale.Id}",
                            TransactionTime = DateTime.UtcNow,
                            SaleId = sale.Id,
                            PaymentMethodId = payment.PaymentMethodId
                        });
                    }
                }
            }
        }

        decimal totalPaidUsd = Math.Round(sale.Payments.Sum(p => p.Amount), 2, MidpointRounding.AwayFromZero);
        decimal remainingBalanceUsd = Math.Round(sale.TotalUSD - totalPaidUsd, 2, MidpointRounding.AwayFromZero);

        sale.CustomerId = customer.Id;
        sale.CustomerName = customer.Name;
        sale.CustomerCedula = customer.CedulaOrRif;

        if (totalPaidUsd > 0 && sale.TotalUSD > 0 && remainingBalanceUsd <= 0.05m && totalPaidUsd >= (sale.TotalUSD - 0.05m))
        {
            // Se cubrió el 100% mediante los pagos iniciales -> Completar y generar factura
            // 8.9-B4: caminar TODA la operación bajo execution strategy para que un fallo
            // transitorio reintente el bloque completo (sales + inventory enrollado) y no
            // quede una escritura a medias entre las dos bases.
            await _context.Database.CreateExecutionStrategy().ExecuteAsync(async _ =>
            {
            IDbContextTransaction? txn = null;
            if (_context.Database.ProviderName != null && !_context.Database.ProviderName.Contains("InMemory"))
            {
                txn = await _context.Database.BeginTransactionAsync(cancellationToken);
                if (txn != null && _inventoryService != null)
                {
                    var rawDbTx = txn.GetDbTransaction();
                    await _inventoryService.EnrollInTransactionAsync(rawDbTx, cancellationToken);
                }
            }
            try
            {
                sale.InvoiceNumber = await GenerateNextInvoiceNumberAsync();
                sale.Status = SaleStatus.Completed;
                sale.Date = DateTime.UtcNow;
                sale.FinalPaidAmountBsS = sale.Payments.Sum(p => p.AmountBsS);
                ClearHoldClaim(sale);

                // Synchronous stock deduction
                if (_inventoryService != null && sale.Items != null)
                {
                    var productIds = sale.Items.Select(i => i.ProductId).Distinct().ToList();
                    var productsDict = new Dictionary<int, Product>();
                    var fetched = await _inventoryService.GetProductsByIdsAsync(productIds, cancellationToken);
                    if (fetched != null && fetched.Count > 0)
                    {
                        productsDict = fetched.ToDictionary(p => p.Id);
                    }
                    else
                    {
                        foreach (var id in productIds)
                        {
                            var p = await _inventoryService.GetProductByIdAsync(id, cancellationToken);
                            if (p != null) productsDict[p.Id] = p;
                        }
                    }

                    var stockDeductions = new List<StockDeductionRequest>();
                    foreach (var item in sale.Items)
                    {
                        if (productsDict.TryGetValue(item.ProductId, out var product) && product.IsCashAdvance)
                        {
                            continue;
                        }

                        stockDeductions.Add(new StockDeductionRequest(
                            item.ProductId,
                            -item.Quantity,
                            $"Sale #{sale.InvoiceNumber.Value}",
                            sale.Id));
                    }

                    if (stockDeductions.Count > 0)
                    {
                        var allowNegativeStock = await IsAllowNegativeStockEnabledAsync();
                        await _inventoryService.UpdateStockBatchAsync(
                            stockDeductions,
                            allowNegativeStock: allowNegativeStock,
                            cancellationToken: cancellationToken);
                    }
                }

                // Outbox message
                _context.OutboxMessages.Add(new OutboxMessage
                {
                    Id = Guid.NewGuid(),
                    EventType = "SaleCompleted",
                    Payload = JsonSerializer.Serialize(new
                    {
                        SaleId = sale.Id,
                        InvoiceNumber = sale.InvoiceNumber.Value,
                        Date = sale.Date,
                        TotalUSD = sale.TotalUSD,
                        TotalBsS = sale.TotalBsS,
                        CashierId = sale.CashierId
                    }),
                    CreatedAtUtc = DateTime.UtcNow,
                    NextRetryUtc = DateTime.UtcNow,
                    Status = "Pending"
                });

                RegisterIdempotencyRecord(idempotencyKey, idempotencyPayloadHash, $"/api/sales/{saleId}/hold", System.Text.Json.JsonSerializer.Serialize(MapToDto(sale)), actingUserId);

                if (_cashDrawerService != null && remainingBalanceUsd < -0.05m)
                {
                    decimal changeUsd = Math.Abs(remainingBalanceUsd);
                    if (changeUsd > 100m && changeUsd > sale.TotalUSD)
                    {
                        throw new ArgumentException($"El sobrepago o vuelto requerido (${changeUsd:F2} USD) excede los límites operacionales de seguridad.");
                    }

                    decimal changeBsS = Math.Round(changeUsd * sale.AppliedRate, 2, MidpointRounding.AwayFromZero);

                    var paidMethodIds = sale.Payments.Select(p => p.PaymentMethodId).Distinct().ToList();
                    var paidMethods = await _context.PaymentMethods
                        .Where(pm => paidMethodIds.Contains(pm.Id))
                        .ToDictionaryAsync(pm => pm.Id, cancellationToken);
                    int? cashMethodId = sale.Payments
                        .FirstOrDefault(p => paidMethods.TryGetValue(p.PaymentMethodId, out var pm) && pm.IsCash)
                        ?.PaymentMethodId;

                    var changeSession = await _cashDrawerService.GetOrCreateActiveSessionAsync(sale.AppliedRate, cancellationToken);

                    decimal pendingCashIncomeBsS = _context.CashTransactions.Local
                        .Where(t => t.SessionId == changeSession.Id
                                 && t.Type == CashTransactionType.Income
                                 && t.Source == CashTransactionSource.SalePayment
                                 && t.IsPhysicalCash)
                        .Sum(t => t.AmountLocal);

                    await _cashDrawerService.RecordSaleChangeAsync(
                        changeSession.Id,
                        changeUsd,
                        changeBsS,
                        sale.AppliedRate,
                        $"Vuelto Pedido #{sale.Id}",
                        sale.Id,
                        cashMethodId,
                        pendingCashIncomeBsS,
                        cancellationToken);
                }

                await _context.SaveChangesAsync(cancellationToken);

                if (txn != null)
                {
                    await txn.CommitAsync(cancellationToken);
                    // 8.7-B7: devolver al InventoryDbContext su conexión propia tras el commit.
                    if (_inventoryService != null)
                    {
                        await _inventoryService.DetachFromTransactionAsync(cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                if (txn != null)
                {
                    await txn.RollbackAsync(cancellationToken);
                    if (_inventoryService != null)
                    {
                        await _inventoryService.DetachFromTransactionAsync(cancellationToken);
                    }
                }
                _logger?.LogError(ex, "[SalesService] Error al completar venta en espera #{SaleId} al 100%. Transacción revertida.", saleId);
                throw;
            }
            finally
            {
                txn?.Dispose();
            }

            try
            {
                var itemsSnapshot = sale.Items.Select(i => new SaleItemSnapshot(i.ProductId, i.Quantity)).ToList();
                var saleMadeEvent = new SaleMadeEvent(sale.Id, sale.Date, itemsSnapshot, sale.InvoiceNumber.Value);
                await _mediator.Publish(saleMadeEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[SalesService] Publicación secundaria de SaleMadeEvent falló para Venta #{SaleId}, pero está respaldada en Outbox.", sale.Id);
            }
            }, cancellationToken);
        }
        else
        {
            ClearHoldClaim(sale);
            sale.Status = SaleStatus.OnHold;
            sale.DeliveryStatus = SaleDeliveryStatus.PendingPickup;
            RegisterIdempotencyRecord(idempotencyKey, idempotencyPayloadHash, $"/api/sales/{saleId}/hold", System.Text.Json.JsonSerializer.Serialize(MapToDto(sale)), actingUserId);
            await _context.SaveChangesAsync(cancellationToken);
        }

        await NotifyHoldOrdersChangedAsync();
        await PopulateItemsMetadataAsync(sale);
        return MapToDto(sale);
    }

    public async Task<SaleDto> UpdateSaleItemsAsync(int saleId, UpdateSaleItemsRequestDto request, bool isPriceOverrideAuthorized = false, int? actingUserId = null, System.Threading.CancellationToken cancellationToken = default)
    {
        var sale = await GetSaleEntityAsync(saleId);
        EnsureHoldClaimAccess(sale, actingUserId);
        if (sale.Status != SaleStatus.OnHold)
            throw new InvalidOperationException("Solo se pueden modificar productos en ventas que estén en estado en espera (OnHold).");

        decimal totalPaidUsd = sale.Payments != null ? sale.Payments.Sum(p => p.Amount) : 0;

        // Calcular nuevo total USD a partir de la lista de ítems enviada
        decimal newTotalUsd = 0;
        var newItemsList = new List<SaleItem>();

        if (request?.Items != null && request.Items.Any())
        {
            var productIds = request.Items.Where(i => i.Quantity > 0m).Select(i => i.ProductId).Distinct().ToList();
            var productsDict = new Dictionary<int, Product>();
            if (_inventoryService != null && productIds.Any())
            {
                var fetched = await _inventoryService.GetProductsByIdsAsync(productIds, cancellationToken);
                if (fetched != null && fetched.Count > 0)
                {
                    productsDict = fetched.ToDictionary(p => p.Id);
                }
                else
                {
                    foreach (var id in productIds)
                    {
                        var p = await _inventoryService.GetProductByIdAsync(id, cancellationToken);
                        if (p != null) productsDict[p.Id] = p;
                    }
                }
            }

            foreach (var reqItem in request.Items)
            {
                if (reqItem.Quantity <= 0m) continue;

                productsDict.TryGetValue(reqItem.ProductId, out var product);
                if (product == null || !product.IsActive)
                {
                    throw new KeyNotFoundException($"El producto especificado (ID: {reqItem.ProductId}) no existe o está inactivo.");
                }

                decimal adjustedQty = ValidateAndAdjustQuantity(product, reqItem.Quantity);

                string productName = product.Name;
                decimal wholesalePrice = product.PriceWholesaleUSD > 0
                    ? product.PriceWholesaleUSD
                    : (product.PriceRetailUSD > 0 ? product.PriceRetailUSD : product.PriceUSD);
                decimal retailPrice = product.PriceRetailUSD > 0 ? product.PriceRetailUSD : product.PriceUSD;
                decimal minWholesaleQty = product.MinWholesaleQuantity > 0 ? product.MinWholesaleQuantity : 6m;
                bool isWholesale = string.Equals(sale.PriceListType, "Wholesale", StringComparison.OrdinalIgnoreCase)
                    && product.HasWholesale
                    && adjustedQty >= minWholesaleQty;
                decimal effectivePrice = isWholesale ? wholesalePrice : retailPrice;

                if (!product.IsCashAdvance && reqItem.UnitPrice > 0 && reqItem.UnitPrice != effectivePrice && !isPriceOverrideAuthorized)
                {
                    throw new UnauthorizedAccessException($"Modificación de precio no autorizada para el producto '{productName}'. Se requiere autorización de Administrador o Supervisor.");
                }

                decimal unitPriceUsd = reqItem.UnitPrice > 0 ? reqItem.UnitPrice : effectivePrice;
                decimal subtotalUsd = Math.Round(unitPriceUsd * adjustedQty, 2, MidpointRounding.AwayFromZero);
                decimal unitPriceBsS = PricingCalculator.ToBsSCeiling(unitPriceUsd, sale.AppliedRate);
                decimal subtotalBsS = PricingCalculator.RoundToDigital(adjustedQty * unitPriceBsS);
                bool isCustomPrice = reqItem.UnitPrice > 0 && reqItem.UnitPrice != effectivePrice;

                newTotalUsd += subtotalUsd;

                newItemsList.Add(new SaleItem
                {
                    SaleId = sale.Id,
                    ProductId = reqItem.ProductId,
                    ProductName = productName,
                    Quantity = adjustedQty,
                    UnitCostUSD = product.CostPriceUSD,
                    UnitPrice = unitPriceUsd,
                    UnitPriceBsS = unitPriceBsS,
                    Subtotal = subtotalUsd,
                    SubtotalBsS = subtotalBsS,
                    IsCustomPrice = isCustomPrice
                });
            }
        }

        newTotalUsd = Math.Round(newTotalUsd, 2, MidpointRounding.AwayFromZero);

        // 1. Validar que el nuevo total no sea menor a lo ya abonado por el cliente
        if (newTotalUsd < totalPaidUsd)
        {
            throw new ArgumentException($"El nuevo total del pedido (${newTotalUsd:F2} USD) no puede ser menor al monto total ya abonado por el cliente (${totalPaidUsd:F2} USD).");
        }

        // Reemplazar los ítems existentes
        if (sale.Items != null && sale.Items.Any())
        {
            _context.SaleItems.RemoveRange(sale.Items);
            sale.Items.Clear();
        }

        sale.Items ??= new List<SaleItem>();
        foreach (var newItem in newItemsList)
        {
            sale.Items.Add(newItem);
        }

        await RecalculateTotalAsync(sale);
        ValidateHoldSaleTotal(sale);
        await _context.SaveChangesAsync(cancellationToken);

        await NotifyHoldOrdersChangedAsync();
        return MapToDto(sale);
    }

    public async Task<IEnumerable<SaleDto>> GetPendingSalesAsync(int? cashierId = null, int limit = 200, int offset = 0, System.Threading.CancellationToken cancellationToken = default)
    {
        // 8.7-B6: los GET no escriben. El recálculo masivo de OnHold ocurre en el POST de tasa
        // (ExchangeRateController → RecalculateOnHoldSalesAsync) e invalida/redifunde por SignalR.
        // 8.9-B2: el filtro por cajero es opcional; el listado compartido multiterminal consulta con cashierId null.
        // 8.2-M9: tope de la cola (default 200, max 1000 en el controlador) — acota memoria/CPU.
        // 8.14-N1: paginación real por offset (los clientes pueden pedir más páginas).
        if (limit <= 0) limit = 200;
        if (offset < 0) offset = 0;

        var sales = await _context.Sales
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.Customer)
            .Include(s => s.Items)
            .Include(s => s.Payments)
                .ThenInclude(p => p.PaymentMethod)
            .Include(s => s.Cashier)
            .Where(s => s.Status == SaleStatus.OnHold)
            .Where(s => !cashierId.HasValue || s.CashierId == cashierId.Value)
            .OrderByDescending(s => s.Date)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        await PopulateItemsMetadataAsync(sales);

        return sales.Select(s => MapToDto(s));
    }

    public async Task<int> CountPendingSalesAsync(int? cashierId = null, System.Threading.CancellationToken cancellationToken = default)
    {
        return await _context.Sales
            .AsNoTracking()
            .CountAsync(s => s.Status == SaleStatus.OnHold
                && (!cashierId.HasValue || s.CashierId == cashierId.Value), cancellationToken);
    }


    private void RegisterIdempotencyRecord(string? key, byte[]? payloadHash, string requestPath, string responseBody, int? actingUserId)
    {
        if (string.IsNullOrWhiteSpace(key) || payloadHash == null) return;

        _context.IdempotentRequests.Add(new IdempotentRequest
        {
            Key = key,
            RequestPath = requestPath,
            PayloadHash = payloadHash,
            StatusCode = 200,
            ResponseBody = responseBody,
            UserId = actingUserId,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(24)
        });
    }
}
