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
    public async Task<CustomerDto> GetDefaultCustomerAsync()
    {
        try
        {
            if (_cache != null && _cache.TryGetValue(DefaultCustomerCacheKey, out CustomerDto? cachedCustomer) && cachedCustomer != null)
            {
                return cachedCustomer;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SalesService] Fallo al leer el cliente por defecto desde la caché; se consulta a la base de datos.");
        }

        var defaultCustomer = await _context.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IsDefault)
            ?? await _context.Customers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == 1);

        if (defaultCustomer == null) throw new KeyNotFoundException("Cliente por defecto no encontrado.");
        
        var dto = new CustomerDto
        {
            Id = defaultCustomer.Id,
            CedulaOrRif = defaultCustomer.CedulaOrRif,
            Name = defaultCustomer.Name,
            Phone = defaultCustomer.Phone,
            CreditLimitUSD = defaultCustomer.CreditLimitUSD,
            IsActive = defaultCustomer.IsActive,
            IsDefault = defaultCustomer.IsDefault
        };

        try
        {
            _cache?.Set(DefaultCustomerCacheKey, dto, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
                Size = 1
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SalesService] Fallo al escribir el cliente por defecto en la caché.");
        }

        return dto;
    }

    public async Task<SaleDto> UpdateSaleCustomerAsync(int saleId, int customerId)
    {
        var sale = await GetSaleEntityAsync(saleId);
        
        if (sale.Status == SaleStatus.Completed || sale.Status == SaleStatus.Cancelled)
            throw new InvalidOperationException("No se puede modificar el cliente de una venta finalizada.");
            
        if (sale.Status == SaleStatus.OnHold && sale.Payments.Any())
            throw new InvalidOperationException("Una cuenta abierta con pagos registrados no permite cambio de titular.");

        var customer = await _context.Customers.FindAsync(customerId);
        if (customer == null) throw new KeyNotFoundException($"Cliente con ID {customerId} no encontrado.");

        sale.CustomerId = customer.Id;
        sale.CustomerName = customer.Name;
        sale.CustomerCedula = customer.CedulaOrRif;

        await _context.SaveChangesAsync();
        return MapToDto(sale);
    }

    public async Task<SaleDto> HoldSaleAsync(int saleId, HoldSaleRequestDto request, string? idempotencyKey = null, byte[]? idempotencyPayloadHash = null)
    {
        var sale = await GetSaleEntityAsync(saleId);
        if (sale.Status != SaleStatus.Pending && sale.Status != SaleStatus.OnHold)
            throw new InvalidOperationException("Solo se pueden poner en espera ventas pendientes o abiertas.");

        var customer = await _context.Customers.FindAsync(request.CustomerId);
        if (customer == null) throw new KeyNotFoundException($"Cliente con ID {request.CustomerId} no encontrado.");
        
        if (customer.IsDefault || customer.CedulaOrRif == "V-00000000")
            throw new InvalidOperationException("Las ventas en espera requieren un cliente real identificable. Asigne un cliente distinto al Consumidor Final.");

        // 8.6-B3: La tasa del HOLD se ancla a la tasa BCV del día (misma política que CompleteSale).
        sale.AppliedRate = await ResolveAnchoredRateAsync(request.ExchangeRate, contextLabel: "HoldSale", referenceId: sale.Id);
        await RecalculateTotalAsync(sale);

        var paymentsToProcess = new List<AddPaymentRequestDto>();
        if (request.InitialPayment != null) paymentsToProcess.Add(request.InitialPayment);
        if (request.InitialPayments != null && request.InitialPayments.Any()) paymentsToProcess.AddRange(request.InitialPayments);

        if (paymentsToProcess.Any())
        {
            var pMethodIds = paymentsToProcess.Select(p => p.PaymentMethodId).Distinct().ToList();
            var pMethodsDict = await _context.PaymentMethods.Where(pm => pMethodIds.Contains(pm.Id)).ToDictionaryAsync(pm => pm.Id);

            foreach (var payment in paymentsToProcess)
            {
                // 8.7-B2: Rechazo de montos negativos en abonos (misma política que CompleteSale).
                if (payment.AmountUSD < 0m || payment.AmountBsS < 0m)
                {
                    throw new InvalidOperationException($"La validación del abono (PaymentMethodId={payment.PaymentMethodId}) rechaza montos negativos.");
                }

                decimal rate = payment.ExchangeRate > 0 ? payment.ExchangeRate : sale.AppliedRate;
                decimal amountUsd = payment.AmountUSD > 0 
                    ? Math.Round(payment.AmountUSD, 2, MidpointRounding.AwayFromZero) 
                    : (rate > 0 ? Math.Round(payment.AmountBsS / rate, 2, MidpointRounding.AwayFromZero) : 0m);

                var initialPaymentEntity = new SalePayment
                {
                    SaleId = sale.Id,
                    PaymentMethodId = payment.PaymentMethodId,
                    Amount = Math.Round(amountUsd, 2, MidpointRounding.AwayFromZero),
                    AmountBsS = PricingCalculator.RoundToDigital(payment.AmountBsS > 0 ? payment.AmountBsS : (amountUsd * rate)),
                    ExchangeRate = rate,
                    ReferenceNumber = payment.ReferenceNumber,
                    CreatedAt = DateTime.UtcNow
                };

                sale.Payments.Add(initialPaymentEntity);

                // H-SAL-5: Asentar ingresos físicos en sesión activa de caja
                if (amountUsd > 0 && pMethodsDict.TryGetValue(payment.PaymentMethodId, out var pMethod) && pMethod.IsCash && _cashDrawerService != null)
                {
                    var activeSession = await _cashDrawerService.GetOrCreateActiveSessionAsync(rate);
                    if (activeSession != null)
                    {
                        _context.CashTransactions.Add(new CashTransaction
                        {
                            SessionId = activeSession.Id,
                            Type = CashTransactionType.Income,
                            Source = CashTransactionSource.SalePayment,
                            AmountUsd = amountUsd,
                            ExchangeRate = rate,
                            AmountLocal = Math.Round(payment.AmountBsS, 2, MidpointRounding.AwayFromZero),
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
            await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
            IDbContextTransaction? txn = null;
            if (_context.Database.ProviderName != null && !_context.Database.ProviderName.Contains("InMemory"))
            {
                txn = await _context.Database.BeginTransactionAsync();
                if (txn != null && _inventoryService != null)
                {
                    var rawDbTx = txn.GetDbTransaction();
                    await _inventoryService.EnrollInTransactionAsync(rawDbTx);
                }
            }
            try
            {
                sale.InvoiceNumber = await GenerateNextInvoiceNumberAsync();
                sale.Status = SaleStatus.Completed;
                sale.Date = DateTime.UtcNow;
                sale.FinalPaidAmountBsS = sale.Payments.Sum(p => p.AmountBsS);

                // Synchronous stock deduction
                if (_inventoryService != null && sale.Items != null)
                {
                    var productIds = sale.Items.Select(i => i.ProductId).Distinct().ToList();
                    var productsDict = new Dictionary<int, Product>();
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
                        await _inventoryService.UpdateStockBatchAsync(
                            stockDeductions,
                            allowNegativeStock: false);
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

                RegisterIdempotencyRecord(idempotencyKey, idempotencyPayloadHash, $"/api/sales/{saleId}/hold", System.Text.Json.JsonSerializer.Serialize(MapToDto(sale)));

                await _context.SaveChangesAsync();

                if (txn != null)
                {
                    await txn.CommitAsync();
                    // 8.7-B7: devolver al InventoryDbContext su conexión propia tras el commit.
                    if (_inventoryService != null)
                    {
                        await _inventoryService.DetachFromTransactionAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                if (txn != null)
                {
                    await txn.RollbackAsync();
                    if (_inventoryService != null)
                    {
                        await _inventoryService.DetachFromTransactionAsync();
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
                await _mediator.Publish(saleMadeEvent);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[SalesService] Publicación secundaria de SaleMadeEvent falló para Venta #{SaleId}, pero está respaldada en Outbox.", sale.Id);
            }
            });
        }
        else
        {
            sale.Status = SaleStatus.OnHold;
            sale.DeliveryStatus = SaleDeliveryStatus.PendingPickup;
            RegisterIdempotencyRecord(idempotencyKey, idempotencyPayloadHash, $"/api/sales/{saleId}/hold", System.Text.Json.JsonSerializer.Serialize(MapToDto(sale)));
            await _context.SaveChangesAsync();
        }

        await PopulateItemsMetadataAsync(sale);
        return MapToDto(sale);
    }

    public async Task<SaleDto> UpdateSaleItemsAsync(int saleId, UpdateSaleItemsRequestDto request, bool isPriceOverrideAuthorized = false)
    {
        var sale = await GetSaleEntityAsync(saleId);
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

            foreach (var reqItem in request.Items)
            {
                if (reqItem.Quantity <= 0m) continue;

                productsDict.TryGetValue(reqItem.ProductId, out var product);
                decimal adjustedQty = ValidateAndAdjustQuantity(product, reqItem.Quantity);

                string productName = product != null ? product.Name : $"Producto #{reqItem.ProductId}";
                decimal catalogPrice = product != null ? product.PriceUSD : 0m;

                if (product != null && !product.IsCashAdvance && reqItem.UnitPrice > 0 && reqItem.UnitPrice != catalogPrice && !isPriceOverrideAuthorized)
                {
                    throw new UnauthorizedAccessException($"Modificación de precio no autorizada para el producto '{productName}'. Se requiere autorización de Administrador o Supervisor.");
                }

                decimal unitPriceUsd = reqItem.UnitPrice > 0 ? reqItem.UnitPrice : catalogPrice;
                decimal subtotalUsd = Math.Round(unitPriceUsd * adjustedQty, 2, MidpointRounding.AwayFromZero);
                decimal unitPriceBsS = Math.Round(unitPriceUsd * sale.AppliedRate, 2, MidpointRounding.AwayFromZero);
                decimal subtotalBsS = Math.Round(subtotalUsd * sale.AppliedRate, 2, MidpointRounding.AwayFromZero);

                newTotalUsd += subtotalUsd;

                newItemsList.Add(new SaleItem
                {
                    SaleId = sale.Id,
                    ProductId = reqItem.ProductId,
                    ProductName = productName,
                    Quantity = adjustedQty,
                    UnitPrice = unitPriceUsd,
                    UnitPriceBsS = unitPriceBsS,
                    Subtotal = subtotalUsd,
                    SubtotalBsS = subtotalBsS
                });
            }
        }

        newTotalUsd = Math.Round(newTotalUsd, 2, MidpointRounding.AwayFromZero);

        // 1. Validar que el nuevo total no sea menor a lo ya abonado por el cliente
        if (newTotalUsd < totalPaidUsd)
        {
            throw new InvalidOperationException($"El nuevo total del pedido (${newTotalUsd:F2} USD) no puede ser menor al monto total ya abonado por el cliente (${totalPaidUsd:F2} USD).");
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
        await _context.SaveChangesAsync();

        return MapToDto(sale);
    }

    public async Task<SaleDto> AddPaymentToHoldSaleAsync(int saleId, AddPaymentRequestDto request, string? idempotencyKey = null, byte[]? idempotencyPayloadHash = null)
    {
        var sale = await GetSaleEntityAsync(saleId);
        if (sale.Status != SaleStatus.OnHold)
            throw new InvalidOperationException("Solo se pueden agregar abonos a ventas en estado en espera.");

        var input = await ComputePaymentInputsAsync(sale, request, sale.Payments.Sum(p => p.Amount), "AddPaymentToHoldSale");

        // 8.9-B4: envolver en execution strategy (reintento completo ante fallos transitorios).
        return await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
        IDbContextTransaction? dbTransaction = null;
        if (_context.Database.ProviderName != null && !_context.Database.ProviderName.Contains("InMemory"))
        {
            dbTransaction = await _context.Database.BeginTransactionAsync();
        }

        try
        {
            var paymentEntity = new SalePayment
            {
                SaleId = sale.Id,
                PaymentMethodId = request.PaymentMethodId,
                Amount = Math.Round(input.AmountUsd, 2, MidpointRounding.AwayFromZero),
                AmountBsS = Math.Round(request.AmountBsS, 2, MidpointRounding.AwayFromZero),
                ExchangeRate = input.Rate,
                ReferenceNumber = request.ReferenceNumber,
                CreatedAt = DateTime.UtcNow
            };

            _context.SalePayments.Add(paymentEntity);

            // Si es efectivo y monto positivo, registrar en sesión activa de caja
            if (input.Method != null && input.Method.IsCash && input.AmountUsd > 0 && _cashDrawerService != null)
            {
                var activeSession = await _cashDrawerService.GetOrCreateActiveSessionAsync(input.Rate);
                var cashTx = new CashTransaction
                {
                    SessionId = activeSession.Id,
                    Type = CashTransactionType.Income,
                    Source = CashTransactionSource.SalePayment,
                    AmountUsd = input.AmountUsd,
                    ExchangeRate = input.Rate,
                    AmountLocal = Math.Round(request.AmountBsS, 2, MidpointRounding.AwayFromZero),
                    IsPhysicalCash = true,
                    Description = $"Abono Venta #{saleId}",
                    TransactionTime = DateTime.UtcNow,
                    SaleId = sale.Id,
                    PaymentMethodId = request.PaymentMethodId
                };
                _context.CashTransactions.Add(cashTx);
            }

            RegisterIdempotencyRecord(idempotencyKey, idempotencyPayloadHash, $"/api/sales/{saleId}/payments", System.Text.Json.JsonSerializer.Serialize(MapToDto(sale)));

            await _context.SaveChangesAsync();

            if (dbTransaction != null)
            {
                await dbTransaction.CommitAsync();
            }

            return MapToDto(sale);
        }
        catch (Exception ex)
        {
            if (dbTransaction != null)
            {
                await dbTransaction.RollbackAsync();
            }
            _logger?.LogError(ex, "[SalesService] Error al registrar abono en venta #{SaleId}. Transacción revertida.", saleId);
            throw;
        }
        finally
        {
            dbTransaction?.Dispose();
        }
        });
    }

    // 8.29-A05: abonos atómicos por lote. Todas las validaciones ocurren ANTES de abrir la
    // transacción; si alguna falla, se lanza sin persistir NADA. La persistencia de todos los
    // abonos del lote comparte UNA sola transacción (rollback conjunto) y UNA sola
    // SaveChanges. Idempotency: un único Idempotency-Key por lote.
    public async Task<SaleDto> AddPaymentsBatchToHoldSaleAsync(int saleId, List<AddPaymentRequestDto> payments, string? idempotencyKey = null, byte[]? idempotencyPayloadHash = null)
    {
        if (payments == null || payments.Count == 0)
            throw new ArgumentException("Debe enviar al menos un abono.");

        if (payments.Count > 50)
            throw new ArgumentException("El lote de abonos supera el máximo permitido (50).");

        var sale = await GetSaleEntityAsync(saleId);
        if (sale.Status != SaleStatus.OnHold)
            throw new InvalidOperationException("Solo se pueden agregar abonos a ventas en estado en espera.");

        var computedInputs = new List<(AddPaymentRequestDto Request, ComputedPaymentInput Input)>(payments.Count);
        decimal runningPaidUsd = sale.Payments.Sum(p => p.Amount);
        foreach (var request in payments)
        {
            var input = await ComputePaymentInputsAsync(sale, request, runningPaidUsd, "AddPaymentsBatchToHoldSale");
            runningPaidUsd += input.AmountUsd;
            computedInputs.Add((request, input));
        }

        // 8.9-B4: execution strategy para reintentos completos del lote ante fallos transitorios.
        return await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            IDbContextTransaction? dbTransaction = null;
            if (_context.Database.ProviderName != null && !_context.Database.ProviderName.Contains("InMemory"))
            {
                dbTransaction = await _context.Database.BeginTransactionAsync();
            }

            try
            {
                foreach (var (request, input) in computedInputs)
                {
                    var paymentEntity = new SalePayment
                    {
                        SaleId = sale.Id,
                        PaymentMethodId = request.PaymentMethodId,
                        Amount = Math.Round(input.AmountUsd, 2, MidpointRounding.AwayFromZero),
                        AmountBsS = Math.Round(request.AmountBsS, 2, MidpointRounding.AwayFromZero),
                        ExchangeRate = input.Rate,
                        ReferenceNumber = request.ReferenceNumber,
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.SalePayments.Add(paymentEntity);

                    if (input.Method != null && input.Method.IsCash && input.AmountUsd > 0 && _cashDrawerService != null)
                    {
                        var activeSession = await _cashDrawerService.GetOrCreateActiveSessionAsync(input.Rate);
                        var cashTx = new CashTransaction
                        {
                            SessionId = activeSession.Id,
                            Type = CashTransactionType.Income,
                            Source = CashTransactionSource.SalePayment,
                            AmountUsd = input.AmountUsd,
                            ExchangeRate = input.Rate,
                            AmountLocal = Math.Round(request.AmountBsS, 2, MidpointRounding.AwayFromZero),
                            IsPhysicalCash = true,
                            Description = $"Abono Venta #{saleId}",
                            TransactionTime = DateTime.UtcNow,
                            SaleId = sale.Id,
                            PaymentMethodId = request.PaymentMethodId
                        };
                        _context.CashTransactions.Add(cashTx);
                    }
                }

                RegisterIdempotencyRecord(idempotencyKey, idempotencyPayloadHash, $"/api/sales/{saleId}/payments/batch", System.Text.Json.JsonSerializer.Serialize(MapToDto(sale)));

                await _context.SaveChangesAsync();

                if (dbTransaction != null)
                {
                    await dbTransaction.CommitAsync();
                }

                return MapToDto(sale);
            }
            catch (Exception ex)
            {
                if (dbTransaction != null)
                {
                    await dbTransaction.RollbackAsync();
                }
                _logger?.LogError(ex, "[SalesService] Error al registrar abonos por lote en venta #{SaleId}. Transacción revertida.", saleId);
                throw;
            }
            finally
            {
                dbTransaction?.Dispose();
            }
        });
    }

    // Cálculo y validación compartidos de un abono individual (tasa anclada BCV, montos,
    // límite acumulado y regla de efectivo a montos enteros). Se usa tanto en el flujo de
    // abono simple como en el batch; `runningPaidUsd` es lo ya abonado + abonos del lote.
    private async Task<ComputedPaymentInput> ComputePaymentInputsAsync(Sale sale, AddPaymentRequestDto request, decimal runningPaidUsd, string contextLabel)
    {
        // 8.6-B3/8.5-A5: Tasa del abono anclada a la BCV del día cuando el cliente la envía.
        decimal rate = request.ExchangeRate > 0
            ? await ResolveAnchoredRateAsync(request.ExchangeRate, contextLabel: contextLabel, referenceId: sale.Id)
            : sale.AppliedRate;
        decimal amountUsd = request.AmountUSD > 0
            ? Math.Round(request.AmountUSD, 2, MidpointRounding.AwayFromZero)
            : (rate > 0 ? Math.Round(request.AmountBsS / rate, 2, MidpointRounding.AwayFromZero) : 0m);

        if (amountUsd <= 0 && request.AmountBsS <= 0)
        {
            throw new ArgumentException("El monto del abono debe ser mayor a cero.");
        }

        if (runningPaidUsd + amountUsd > sale.TotalUSD + 0.05m)
        {
            throw new InvalidOperationException("El monto del abono excede el total pendiente de la venta.");
        }

        // Validación de integridad: el efectivo solo acepta montos enteros (sin centavos).
        var method = await _context.PaymentMethods.FindAsync(request.PaymentMethodId);
        if (method != null && method.IsCash && request.AmountBsS % 1 != 0)
        {
            throw new InvalidOperationException("El método de pago en efectivo solo acepta montos enteros.");
        }

        return new ComputedPaymentInput(rate, amountUsd, request.AmountBsS, method);
    }

    private sealed record ComputedPaymentInput(decimal Rate, decimal AmountUsd, decimal AmountBsS, PaymentMethod? Method);

    public async Task<IEnumerable<SaleDto>> GetPendingSalesAsync(int? cashierId = null, int limit = 200, int offset = 0)
    {
        // 8.7-B6: los GET no escriben. El recálculo masivo de OnHold ocurre en el POST de tasa
        // (ExchangeRateController → RecalculateOnHoldSalesAsync) e invalida/redifunde por SignalR.
        // 8.9-B2: scope por cajero — un cajero solo vio sus propias ventas OnHold; Admin/Manager todo.
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
            .ToListAsync();

        await PopulateItemsMetadataAsync(sales);

        return sales.Select(s => MapToDto(s));
    }

    public async Task<int> CountPendingSalesAsync(int? cashierId = null)
    {
        return await _context.Sales
            .AsNoTracking()
            .CountAsync(s => s.Status == SaleStatus.OnHold
                && (!cashierId.HasValue || s.CashierId == cashierId.Value));
    }

    public async Task<(IEnumerable<CustomerDto> Items, int TotalCount)> GetCustomersAsync(
        string? query = null,
        int page = 1,
        int pageSize = 20,
        bool recentOnly = false)
    {
        if (recentOnly)
        {
            var recentCustomerIds = await _context.Sales
                .AsNoTracking()
                .Where(s => s.CustomerId.HasValue)
                .GroupBy(s => s.CustomerId!.Value)
                .OrderByDescending(g => g.Max(s => s.Date))
                .Select(g => g.Key)
                .Take(3)
                .ToListAsync();

            var recentCustomers = await _context.Customers
                .AsNoTracking()
                .Where(c => recentCustomerIds.Contains(c.Id))
                .ToListAsync();

            if (recentCustomers.Count < 3)
            {
                var existingIds = recentCustomers.Select(c => c.Id).ToList();
                var additional = await _context.Customers
                    .AsNoTracking()
                    .Where(c => !existingIds.Contains(c.Id))
                    .OrderByDescending(c => c.Id)
                    .Take(3 - recentCustomers.Count)
                    .ToListAsync();
                recentCustomers.AddRange(additional);
            }

            var ordered = recentCustomerIds
                .Select(id => recentCustomers.FirstOrDefault(c => c.Id == id))
                .Where(c => c != null)
                .Concat(recentCustomers.Where(c => !recentCustomerIds.Contains(c.Id)))
                .DistinctBy(c => c!.Id)
                .Take(3)
                .Select(c => new CustomerDto
                {
                    Id = c!.Id,
                    CedulaOrRif = c.CedulaOrRif,
                    Name = c.Name,
                    Phone = c.Phone,
                    CreditLimitUSD = c.CreditLimitUSD,
                    IsActive = c.IsActive,
                    IsDefault = c.IsDefault
                })
                .ToList();

            return (ordered, ordered.Count);
        }

        var q = _context.Customers.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var lower = query.Trim().ToLower();
            q = q.Where(c => c.Name.ToLower().Contains(lower) || c.CedulaOrRif.ToLower().Contains(lower));
        }

        int totalCount = await q.CountAsync();
        page = Math.Max(1, page);
        pageSize = Math.Max(1, pageSize);

        var customers = await q
            .OrderBy(c => c.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CustomerDto
            {
                Id = c.Id,
                CedulaOrRif = c.CedulaOrRif,
                Name = c.Name,
                Phone = c.Phone,
                CreditLimitUSD = c.CreditLimitUSD,
                IsActive = c.IsActive,
                IsDefault = c.IsDefault
            })
            .ToListAsync();

        return (customers, totalCount);
    }

    public async Task<CustomerDto> CreateCustomerAsync(CreateCustomerDto request)
    {
        if (string.IsNullOrWhiteSpace(request.CedulaOrRif) || string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Cédula/RIF y Nombre son campos obligatorios.");

        var normalizedCedula = request.CedulaOrRif.Trim().ToUpperInvariant();
        var exists = await _context.Customers.AnyAsync(c => c.CedulaOrRif.ToUpper() == normalizedCedula);
        if (exists)
            throw new InvalidOperationException($"Ya existe un cliente registrado con la Cédula/RIF '{request.CedulaOrRif}'.");

        var customer = new Customer
        {
            CedulaOrRif = normalizedCedula,
            Name = request.Name.Trim(),
            Phone = request.Phone?.Trim() ?? string.Empty,
            CreditLimitUSD = request.CreditLimitUSD >= 0 ? request.CreditLimitUSD : 0m,
            IsActive = true
        };

        _context.Customers.Add(customer);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException($"Ya existe un cliente registrado con la Cédula/RIF '{request.CedulaOrRif}'.", ex);
        }

        return new CustomerDto
        {
            Id = customer.Id,
            CedulaOrRif = customer.CedulaOrRif,
            Name = customer.Name,
            Phone = customer.Phone,
            CreditLimitUSD = customer.CreditLimitUSD,
            IsActive = customer.IsActive
        };
    }

    public async Task<CustomerDto> UpdateCustomerAsync(int id, UpdateCustomerDto request)
    {
        var customer = await _context.Customers.FindAsync(id);
        if (customer == null)
            throw new KeyNotFoundException($"No se encontró el cliente con ID {id}.");

        if (customer.IsDefault && customer.CedulaOrRif.Equals("V-00000000", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(customer.CedulaOrRif, request.CedulaOrRif?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("No se permite cambiar la Cédula/RIF del cliente Consumidor Final.");
            }
        }

        if (string.IsNullOrWhiteSpace(request.CedulaOrRif) || string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Cédula/RIF y Nombre son campos obligatorios.");

        var normalizedCedula = request.CedulaOrRif.Trim().ToUpperInvariant();
        var exists = await _context.Customers.AnyAsync(c => c.Id != id && c.CedulaOrRif.ToUpper() == normalizedCedula);
        if (exists)
            throw new InvalidOperationException($"Ya existe otro cliente registrado con la Cédula/RIF '{request.CedulaOrRif}'.");

        customer.CedulaOrRif = normalizedCedula;
        customer.Name = request.Name.Trim();
        customer.Phone = request.Phone?.Trim() ?? string.Empty;
        customer.CreditLimitUSD = request.CreditLimitUSD >= 0 ? request.CreditLimitUSD : 0m;
        customer.IsActive = request.IsActive;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException($"Ya existe otro cliente registrado con la Cédula/RIF '{request.CedulaOrRif}'.", ex);
        }

        if (customer.IsDefault || id == 1)
        {
            _cache?.Remove(DefaultCustomerCacheKey);
        }

        return new CustomerDto
        {
            Id = customer.Id,
            CedulaOrRif = customer.CedulaOrRif,
            Name = customer.Name,
            Phone = customer.Phone,
            CreditLimitUSD = customer.CreditLimitUSD,
            IsActive = customer.IsActive,
            IsDefault = customer.IsDefault
        };
    }

    public async Task DeleteCustomerAsync(int id)
    {
        var customer = await _context.Customers.FindAsync(id);
        if (customer == null)
            throw new KeyNotFoundException($"No se encontró el cliente con ID {id}.");

        if (customer.IsDefault || customer.CedulaOrRif.Equals("V-00000000", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("No se permite eliminar el cliente Consumidor Final predeterminado del sistema.");
        }

        bool hasSales = await _context.Sales.AnyAsync(s => s.CustomerId == id);
        if (hasSales)
        {
            throw new InvalidOperationException("No se puede eliminar el cliente porque tiene ventas o transacciones asociadas.");
        }

        _context.Customers.Remove(customer);
        await _context.SaveChangesAsync();
        _cache?.Remove(DefaultCustomerCacheKey);
    }

    private void RegisterIdempotencyRecord(string? key, byte[]? payloadHash, string requestPath, string responseBody)
    {
        if (string.IsNullOrWhiteSpace(key) || payloadHash == null) return;

        _context.IdempotentRequests.Add(new IdempotentRequest
        {
            Key = key,
            RequestPath = requestPath,
            PayloadHash = payloadHash,
            StatusCode = 200,
            ResponseBody = responseBody,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(24)
        });
    }
}
