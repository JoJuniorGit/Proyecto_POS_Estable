using Core.DTOs;
using Core.Entities;
using Core.Events;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public partial class SalesService
{
    public async Task<int> CompleteSaleAsync(
        int saleId, 
        decimal exchangeRate, 
        IEnumerable<PaymentInfo> payments, 
        decimal roundingAdjustment = 0, 
        int? cashierId = null, 
        bool isPendingPickup = false, 
        string? idempotencyKey = null,
        byte[]? idempotencyPayloadHash = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        _logger?.LogInformation("[TX_START] CorrelationId={CorrelationId}, SaleId={SaleId}, IsolationLevel=ReadCommitted", correlationId, saleId);

        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sale = await GetSaleEntityAsync(saleId);

            // Idempotency check: if sale is already completed, return existing InvoiceNumber immediately
            if (sale.Status == SaleStatus.Completed && sale.InvoiceNumber.HasValue)
            {
                _logger?.LogInformation("[SalesService] Idempotency: Venta #{SaleId} ya se encontraba completada con Factura N° {InvoiceNumber}. Retornando consecutivo.", saleId, sale.InvoiceNumber.Value);
                return sale.InvoiceNumber.Value;
            }

            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken)
                : null;

            if (transaction != null && _inventoryService != null)
            {
                var rawDbTx = transaction.GetDbTransaction();
                await _inventoryService.EnrollInTransactionAsync(rawDbTx, cancellationToken);
            }

            try
            {
                if (sale.Status != SaleStatus.Pending && sale.Status != SaleStatus.OnHold) 
                    throw new InvalidOperationException("La venta no se encuentra en estado Pendiente o En Espera.");

                if (isPendingPickup)
                {
                    if (!sale.CustomerId.HasValue)
                    {
                        throw new ArgumentException("Para registrar un apartado pagado (mercancía en custodia), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono).");
                    }

                    var cust = await _context.Customers.FindAsync(sale.CustomerId.Value);
                    if (cust == null || cust.IsDefault || cust.CedulaOrRif == "V-00000000" || cust.Name.StartsWith("Consumidor Final", StringComparison.OrdinalIgnoreCase) || cust.Name.StartsWith("Cliente General", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException("Para registrar un apartado pagado (mercancía en custodia), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono).");
                    }
                }

                if (cashierId.HasValue)
                {
                    sale.CashierId = cashierId.Value;
                }

                if (exchangeRate <= 0)
                {
                    throw new InvalidOperationException("Rechazo Defensivo: Tasa de cambio AppliedRate inválida o no inicializada (<= 0).");
                }

                // 8.5-A5/8.6-B3: Anclaje de tasa BCV del día vs. tasa recibida del cliente.
                // Si el desvío supera la tolerancia configurable (>10% por defecto) la tasa BCEV del día
                // se ANCLA como tasa efectiva (evita manipulación del AppliedRate / shortage enmascarado);
                // desvíos ≥ ±100% se rechazan. Sin catch-swallow: los errores del BCV se auditan.
                exchangeRate = await ResolveAnchoredRateAsync(
                    exchangeRate,
                    contextLabel: "CompleteSale",
                    referenceId: saleId);

                sale.AppliedRate = exchangeRate;
                await RecalculateTotalAsync(sale);

                // 8.7-B2: Acotar el ajuste de redondeo a un límite operacional (refuerzo del [Range]).
                if (Math.Abs(roundingAdjustment) > 1000m)
                {
                    throw new InvalidOperationException($"Rechazo Defensivo: el ajuste de redondeo ({roundingAdjustment:F2}) excede el límite operacional de ±1000.");
                }

                sale.RoundingAdjustment = roundingAdjustment;

            decimal existingPaidUsd = sale.Payments.Sum(p => p.Amount);
            decimal newPaymentsPaidUsd = payments != null 
                ? payments.Sum(p => p.Amount > 0 ? p.Amount : (p.AmountLocal > 0 && exchangeRate > 0 ? Math.Round(p.AmountLocal / exchangeRate, 2, MidpointRounding.AwayFromZero) : 0m)) 
                : 0m;
            decimal totalPaidUsd = Math.Round(existingPaidUsd + newPaymentsPaidUsd, 2, MidpointRounding.AwayFromZero);
            decimal remainingBalanceUsd = Math.Round(sale.TotalUSD - totalPaidUsd, 2, MidpointRounding.AwayFromZero);

            if (isPendingPickup)
            {
                if (remainingBalanceUsd > 0.05m)
                {
                    throw new ArgumentException("Para registrar un apartado en custodia (Mercancía Pendiente por Retirar), la venta debe estar pagada al 100% (saldo restante $0.00).");
                }

                bool isDefaultCust = sale.CustomerId == null || sale.Customer == null || sale.Customer.IsDefault || (sale.CustomerName != null && sale.CustomerName.ToLower().Contains("consumidor final"));
                if (isDefaultCust)
                {
                    throw new ArgumentException("Para registrar un apartado en custodia (Mercancía Pendiente por Retirar), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono).");
                }
            }

            var activeSession = await _cashDrawerService.GetOrCreateActiveSessionAsync(exchangeRate);

            if (!sale.InvoiceNumber.HasValue)
            {
                sale.InvoiceNumber = await GenerateNextInvoiceNumberAsync();
            }

            var paymentMethodsDict = new Dictionary<int, PaymentMethod>();
            if (payments != null && payments.Any())
            {
                var paymentMethodIds = payments.Select(p => p.PaymentMethodId).Distinct().ToList();
                paymentMethodsDict = await _context.PaymentMethods
                    .Where(pm => paymentMethodIds.Contains(pm.Id))
                    .ToDictionaryAsync(pm => pm.Id);

                foreach (var p in payments)
                {
                    decimal amountUsd = p.Amount;
                    decimal amountLocal = p.AmountLocal;

                    if (amountUsd <= 0 && amountLocal > 0 && exchangeRate > 0)
                    {
                        amountUsd = Math.Round(amountLocal / exchangeRate, 2, MidpointRounding.AwayFromZero);
                    }
                    else if (amountLocal <= 0 && amountUsd > 0 && exchangeRate > 0)
                    {
                        amountLocal = Math.Round(amountUsd * exchangeRate, 2, MidpointRounding.AwayFromZero);
                    }

                    paymentMethodsDict.TryGetValue(p.PaymentMethodId, out var paymentMethod);

                    // 8.6-B3: Validación pre-persistencia del método de pago: un PaymentMethodId inexistente
                    // o inactivo aborta el cobro (evita asociar pagos a configuraciones inválidas).
                    if (paymentMethod == null || !paymentMethod.IsActive)
                    {
                        throw new ArgumentException($"Método de pago inválido o inactivo: PaymentMethodId={p.PaymentMethodId}. Verifique la configuración de métodos de pago.");
                    }

                    // 8.7-B2: Rechazo de montos NEGATIVOS por método. Sin esta validación, un pago con
                    // Amount <= 0 Y AmountLocal <= 0 se persistiría tal cual y distorsionaría los totales
                    // liquidados y el arqueo diario (suma de AmountBsS). Los ceros absolutos se purgan en la
                    // sanitización pre-persistencia (:592-596) y los montos mixtos (una sola moneda) se
                    // convierten arriba.
                    if (amountUsd < 0m || amountLocal < 0m)
                    {
                        throw new ArgumentException($"La validación del método de pago (PaymentMethodId={p.PaymentMethodId}) rechaza montos negativos. Monto USD={p.Amount}, Monto Bs.S={p.AmountLocal}.");
                    }

                    // Validación de integridad: el efectivo solo acepta montos enteros (sin centavos).
                    if (paymentMethod != null && paymentMethod.IsCash && amountLocal % 1 != 0)
                    {
                        throw new ArgumentException("El método de pago en efectivo solo acepta montos enteros.");
                    }

                    _logger?.LogDebug("[CURRENCY CONVERSION DEBUG] Método: {Method}, Monto Bs.S: {BsS}, Tasa AppliedRate: {Rate}, Monto USD Calculado: {Usd}", p.PaymentMethodId, amountLocal, exchangeRate, amountUsd);

                    var paymentEntity = new SalePayment
                    {
                        SaleId = sale.Id,
                        PaymentMethodId = p.PaymentMethodId,
                        Amount = Math.Round(amountUsd, 2, MidpointRounding.AwayFromZero),
                        AmountBsS = Math.Round(amountLocal, 2, MidpointRounding.AwayFromZero),
                        ExchangeRate = exchangeRate,
                        ReferenceNumber = p.Reference,
                        CreatedAt = DateTime.UtcNow
                    };
                    sale.Payments.Add(paymentEntity);

                    if (amountUsd > 0 && paymentMethod != null && paymentMethod.IsCash)
                    {
                        var cashTx = new CashTransaction
                        {
                            SessionId = activeSession.Id,
                            Type = CashTransactionType.Income,
                            Source = CashTransactionSource.SalePayment,
                            AmountUsd = amountUsd,
                            ExchangeRate = exchangeRate,
                            AmountLocal = amountLocal,
                            IsPhysicalCash = true,
                            Description = $"Factura N° {sale.InvoiceNumber}",
                            TransactionTime = DateTime.UtcNow,
                            SaleId = sale.Id,
                            PaymentMethodId = p.PaymentMethodId
                        };
                        _context.CashTransactions.Add(cashTx);
                    }
                }
            }

            // H-SAL-3: Registro de vuelto como egreso (Expense) y validación de límites de sobrepago
            // 8.6-C1: El vuelto pasa por CashDrawerService.RecordSaleChangeAsync, que aplica advisory lock
            // y verifica que la caja tenga saldo para cubrirlo (evita saldo negativo).
            if (remainingBalanceUsd < -0.05m)
            {
                decimal changeUsd = Math.Abs(remainingBalanceUsd);
                if (changeUsd > 100m && changeUsd > sale.TotalUSD)
                {
                    throw new ArgumentException($"El sobrepago o vuelto requerido (${changeUsd:F2} USD) excede los límites operacionales de seguridad.");
                }

                int? cashMethodId = sale.Payments.FirstOrDefault(p => paymentMethodsDict.TryGetValue(p.PaymentMethodId, out var pm) && pm.IsCash)?.PaymentMethodId;

                decimal changeBsS = Math.Round(changeUsd * exchangeRate, 2, MidpointRounding.AwayFromZero);

                // Ingresos cash de la venta aún en el tracker (persistidos junto con todo el cobro): se informan
                // al chequeo de saldo para que el vuelto NO se rechace por no verlos aún en la BD.
                decimal pendingCashIncomeBsS = _context.CashTransactions.Local
                    .Where(t => t.SessionId == activeSession.Id
                             && t.Type == CashTransactionType.Income
                             && t.Source == CashTransactionSource.SalePayment
                             && t.IsPhysicalCash)
                    .Sum(t => t.AmountLocal);

                await _cashDrawerService.RecordSaleChangeAsync(
                    sessionId: activeSession.Id,
                    changeUsd: changeUsd,
                    changeBsS: changeBsS,
                    exchangeRate: exchangeRate,
                    description: $"Vuelto Factura N° {sale.InvoiceNumber}",
                    saleId: sale.Id,
                    cashPaymentMethodId: cashMethodId,
                    pendingCashIncomeBsS: pendingCashIncomeBsS);
                _logger?.LogInformation("[SalesService] Vuelto registrado en caja: ${ChangeUsd} USD / Bs. {ChangeBsS} para Factura N° {InvoiceNumber}",
                    changeUsd, changeBsS, sale.InvoiceNumber);
            }

            // 1. Defensive Aggregated Total Validation:
            if (sale.Payments.Sum(p => p.Amount) <= 0 && !sale.IsZeroAmountOrder)
            {
                throw new InvalidOperationException("Rechazo Defensivo: El total acumulado de los métodos de pago es <= 0. Se aborta el guardado local.");
            }

            if (sale.AppliedRate <= 0)
            {
                throw new InvalidOperationException("Rechazo Defensivo: Tasa de cambio AppliedRate inválida o no inicializada (<= 0).");
            }

            // 2. Pre-Persistence Sanitation compatible with EF Core Change Tracker:
            var paymentsToRemove = sale.Payments.Where(p => p.Amount == 0).ToList();
            foreach (var payment in paymentsToRemove)
            {
                sale.Payments.Remove(payment);
            }

            if (remainingBalanceUsd > 0.05m)
            {
                throw new ArgumentException("El monto ingresado no cubre la totalidad de la venta. El flujo de cobro requiere liquidación al 100%. Para abonos parciales o guardar pedidos en espera, utilice la opción 'Guardar en Espera'.");
            }

            // Es liquidación total
            sale.Status = SaleStatus.Completed;
            sale.DeliveryStatus = isPendingPickup ? SaleDeliveryStatus.PendingPickup : SaleDeliveryStatus.Delivered;
            sale.Date = DateTime.UtcNow;
            sale.AppliedRate = exchangeRate;
            await RecalculateTotalAsync(sale);
            sale.FinalPaidAmountBsS = sale.Payments.Sum(p => p.AmountBsS);
            sale.RoundingAdjustment = roundingAdjustment;

            // Synchronous Stock Deduction inside Transaction (H-SAL-2 / H-INV-1 / A1)
            var productsDict = new Dictionary<int, Product>();
            if (_inventoryService != null && sale.Items != null)
            {
                var productIds = sale.Items.Select(i => i.ProductId).Distinct().ToList();
                var fetched = await _inventoryService.GetProductsByIdsAsync(productIds);
                if (fetched != null)
                {
                    productsDict = fetched.ToDictionary(p => p.Id);
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
                        userId: cashierId?.ToString(),
                        allowNegativeStock: allowNegativeStock);
                }
            }

            // Transactional Outbox Message creation (A1 / H-SAL-2)
            var outboxPayload = JsonSerializer.Serialize(new
            {
                SaleId = sale.Id,
                InvoiceNumber = sale.InvoiceNumber.Value,
                Date = sale.Date,
                TotalUSD = sale.TotalUSD,
                TotalBsS = sale.TotalBsS,
                CashierId = sale.CashierId,
                IdempotencyKey = idempotencyKey,
                Items = sale.Items?.Select(i => new { i.ProductId, i.Quantity, i.UnitPrice, i.Subtotal }).ToList()
            });

            _context.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                EventType = "SaleCompleted",
                Payload = outboxPayload,
                CreatedAtUtc = DateTime.UtcNow,
                NextRetryUtc = DateTime.UtcNow,
                Status = "Pending",
                RetryCount = 0
            });

            // Persistencia de IdempotentRequest dentro de la transacción compartida si se proveyó clave y hash
            if (!string.IsNullOrWhiteSpace(idempotencyKey) && idempotencyPayloadHash != null)
            {
                _context.IdempotentRequests.Add(new IdempotentRequest
                {
                    Key = idempotencyKey,
                    RequestPath = $"/api/sales/{saleId}/complete",
                    PayloadHash = idempotencyPayloadHash,
                    StatusCode = 200,
                    ResponseBody = JsonSerializer.Serialize(sale.InvoiceNumber.Value),
                    CreatedAtUtc = DateTime.UtcNow,
                    ExpiresAtUtc = DateTime.UtcNow.AddHours(24)
                });
            }

            _logger?.LogInformation("[EF CORE ENTITY DEBUG] Persistiendo Sale ID: {SaleId}. Entidades SalePayment reales: {@Payments}", sale.Id, sale.Payments);

            await _context.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
                if (_inventoryService != null)
                {
                    await _inventoryService.DetachFromTransactionAsync(cancellationToken);
                }
            }

            _receiptPrintQueue?.Enqueue(Sales.Module.Receipts.SaleReceiptContext.CreateFrom(sale));

            _logger?.LogInformation("[TX_COMMIT] CorrelationId={CorrelationId}, SaleId={SaleId}, InvoiceNumber={InvoiceNumber}", correlationId, saleId, sale.InvoiceNumber.Value);

            try
            {
                var itemsSnapshot = (sale.Items ?? Enumerable.Empty<SaleItem>())
                    .Where(i => !productsDict.TryGetValue(i.ProductId, out var prod) || !prod.IsCashAdvance)
                    .Select(i => new SaleItemSnapshot(i.ProductId, i.Quantity))
                    .ToList();
                var saleMadeEvent = new SaleMadeEvent(sale.Id, sale.Date, itemsSnapshot, sale.InvoiceNumber.Value);
                await _mediator.Publish(saleMadeEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[SalesService] Publicación de evento secundario SaleMadeEvent falló, pero la venta y el Outbox están garantizados en base de datos.");
            }

            return sale.InvoiceNumber.Value;
        }
        catch (OperationCanceledException opEx)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(System.Threading.CancellationToken.None);
                if (_inventoryService != null)
                {
                    await _inventoryService.DetachFromTransactionAsync(System.Threading.CancellationToken.None);
                }
            }
            _logger?.LogWarning(opEx, "[TX_ROLLBACK] CorrelationId={CorrelationId}, SaleId={SaleId}, Reason=OperationCanceled", correlationId, saleId);
            throw;
        }
        catch (Exception ex)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(System.Threading.CancellationToken.None);
                if (_inventoryService != null)
                {
                    await _inventoryService.DetachFromTransactionAsync(System.Threading.CancellationToken.None);
                }
            }
            _logger?.LogError(ex, "[TX_ROLLBACK] CorrelationId={CorrelationId}, SaleId={SaleId}, Reason={Reason}", correlationId, saleId, ex.Message);
            throw;
        }
        });
    }

    private async Task PopulateItemsMetadataAsync(IEnumerable<Sale> sales)
    {
        if (_inventoryService == null || sales == null) return;

        var allItems = sales.Where(s => s.Items != null).SelectMany(s => s.Items).ToList();
        if (!allItems.Any()) return;

        var productIds = allItems.Select(i => i.ProductId).Distinct().ToList();
        var productsDict = new Dictionary<int, Product>();

        try
        {
            var fetched = await _inventoryService.GetProductsByIdsAsync(productIds);
            if (fetched != null && fetched.Count > 0)
            {
                productsDict = fetched.ToDictionary(p => p.Id);
            }
            else
            {
                foreach (var id in productIds)
                {
                    var p = await _inventoryService.GetProductByIdAsync(id);
                    if (p != null) productsDict[p.Id] = p;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error populating items metadata from inventory service.");
        }

        foreach (var item in allItems)
        {
            if (productsDict.TryGetValue(item.ProductId, out var prod) && prod != null)
            {
                item.IsFractional = prod.IsFractional;
                item.UnitOfMeasure = prod.UnitOfMeasure;
            }
        }
    }

    private async Task PopulateItemsMetadataAsync(Sale sale)
    {
        if (sale != null)
        {
            await PopulateItemsMetadataAsync(new[] { sale });
        }
    }
}

