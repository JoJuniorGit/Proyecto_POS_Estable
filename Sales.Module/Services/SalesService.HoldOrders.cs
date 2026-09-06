using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Events;
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
        catch { }

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
        catch { }

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

    public async Task<SaleDto> HoldSaleAsync(int saleId, HoldSaleRequestDto request)
    {
        var _sale = await GetSaleEntityAsync(saleId);
        if (_sale.Status != SaleStatus.Pending && _sale.Status != SaleStatus.OnHold)
            throw new InvalidOperationException("Solo se pueden poner en espera ventas pendientes o abiertas.");

        var customer = await _context.Customers.FindAsync(request.CustomerId);
        if (customer == null) throw new KeyNotFoundException($"Cliente con ID {request.CustomerId} no encontrado.");
        
        if (customer.IsDefault || customer.CedulaOrRif == "V-00000000")
            throw new InvalidOperationException("Las ventas en espera requieren un cliente real identificable. Asigne un cliente distinto al Consumidor Final.");

        if (request.ExchangeRate > 0)
        {
            _sale.AppliedRate = request.ExchangeRate;
            await RecalculateTotalAsync(_sale);
        }

        var paymentsToProcess = new List<AddPaymentRequestDto>();
        if (request.InitialPayment != null) paymentsToProcess.Add(request.InitialPayment);
        if (request.InitialPayments != null && request.InitialPayments.Any()) paymentsToProcess.AddRange(request.InitialPayments);

        if (paymentsToProcess.Any())
        {
            var pMethodIds = paymentsToProcess.Select(p => p.PaymentMethodId).Distinct().ToList();
            var pMethodsDict = await _context.PaymentMethods.Where(pm => pMethodIds.Contains(pm.Id)).ToDictionaryAsync(pm => pm.Id);

            foreach (var payment in paymentsToProcess)
            {
                decimal rate = payment.ExchangeRate > 0 ? payment.ExchangeRate : _sale.AppliedRate;
                decimal amountUsd = payment.AmountUSD > 0 
                    ? Math.Round(payment.AmountUSD, 2, MidpointRounding.AwayFromZero) 
                    : (rate > 0 ? Math.Round(payment.AmountBsS / rate, 2, MidpointRounding.AwayFromZero) : 0m);

                var initialPaymentEntity = new SalePayment
                {
                    SaleId = _sale.Id,
                    PaymentMethodId = payment.PaymentMethodId,
                    Amount = Math.Round(amountUsd, 2, MidpointRounding.AwayFromZero),
                    AmountBsS = payment.AmountBsS,
                    ExchangeRate = rate,
                    ReferenceNumber = payment.ReferenceNumber,
                    CreatedAt = DateTime.UtcNow
                };

                _sale.Payments.Add(initialPaymentEntity);

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
                            Description = $"Abono Inicial Venta #{_sale.Id}",
                            TransactionTime = DateTime.UtcNow,
                            SaleId = _sale.Id,
                            PaymentMethodId = payment.PaymentMethodId
                        });
                    }
                }
            }
        }

        decimal totalPaidUsd = Math.Round(_sale.Payments.Sum(p => p.Amount), 2, MidpointRounding.AwayFromZero);
        decimal remainingBalanceUsd = Math.Round(_sale.TotalUSD - totalPaidUsd, 2, MidpointRounding.AwayFromZero);

        _sale.CustomerId = customer.Id;
        _sale.CustomerName = customer.Name;
        _sale.CustomerCedula = customer.CedulaOrRif;

        if (totalPaidUsd > 0 && _sale.TotalUSD > 0 && remainingBalanceUsd <= 0.05m && totalPaidUsd >= (_sale.TotalUSD - 0.05m))
        {
            // Se cubrió el 100% mediante los pagos iniciales -> Completar y generar factura
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
                _sale.InvoiceNumber = await GenerateNextInvoiceNumberAsync();
                _sale.Status = SaleStatus.Completed;
                _sale.Date = DateTime.UtcNow;
                _sale.FinalPaidAmountBsS = _sale.Payments.Sum(p => p.AmountBsS);

                // Synchronous stock deduction
                if (_inventoryService != null && _sale.Items != null)
                {
                    var productIds = _sale.Items.Select(i => i.ProductId).Distinct().ToList();
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
                    foreach (var item in _sale.Items)
                    {
                        if (productsDict.TryGetValue(item.ProductId, out var product) && product.IsCashAdvance)
                        {
                            continue;
                        }

                        stockDeductions.Add(new StockDeductionRequest(
                            item.ProductId,
                            -item.Quantity,
                            $"Sale #{_sale.InvoiceNumber.Value}"));
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
                        SaleId = _sale.Id,
                        InvoiceNumber = _sale.InvoiceNumber.Value,
                        Date = _sale.Date,
                        TotalUSD = _sale.TotalUSD,
                        TotalBsS = _sale.TotalBsS,
                        CashierId = _sale.CashierId
                    }),
                    CreatedAtUtc = DateTime.UtcNow,
                    NextRetryUtc = DateTime.UtcNow,
                    Status = "Pending"
                });

                await _context.SaveChangesAsync();

                if (txn != null)
                {
                    await txn.CommitAsync();
                }
            }
            catch (Exception ex)
            {
                if (txn != null)
                {
                    await txn.RollbackAsync();
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
                var itemsSnapshot = _sale.Items.Select(i => new SaleItemSnapshot(i.ProductId, i.Quantity)).ToList();
                var saleMadeEvent = new SaleMadeEvent(_sale.Id, _sale.Date, itemsSnapshot, _sale.InvoiceNumber.Value);
                await _mediator.Publish(saleMadeEvent);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[SalesService] Publicación secundaria de SaleMadeEvent falló para Venta #{SaleId}, pero está respaldada en Outbox.", _sale.Id);
            }
        }
        else
        {
            _sale.Status = SaleStatus.OnHold;
            _sale.DeliveryStatus = SaleDeliveryStatus.PendingPickup;
            await _context.SaveChangesAsync();
        }

        await PopulateItemsMetadataAsync(_sale);
        return MapToDto(_sale);
    }

    public async Task<SaleDto> UpdateSaleItemsAsync(int saleId, UpdateSaleItemsRequestDto request, bool isPriceOverrideAuthorized = false)
    {
        var _sale = await GetSaleEntityAsync(saleId);
        if (_sale.Status != SaleStatus.OnHold)
            throw new InvalidOperationException("Solo se pueden modificar productos en ventas que estén en estado en espera (OnHold).");

        decimal totalPaidUsd = _sale.Payments != null ? _sale.Payments.Sum(p => p.Amount) : 0;

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
                decimal unitPriceBsS = Math.Round(unitPriceUsd * _sale.AppliedRate, 2, MidpointRounding.AwayFromZero);
                decimal subtotalBsS = Math.Round(subtotalUsd * _sale.AppliedRate, 2, MidpointRounding.AwayFromZero);

                newTotalUsd += subtotalUsd;

                newItemsList.Add(new SaleItem
                {
                    SaleId = _sale.Id,
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
        if (_sale.Items != null && _sale.Items.Any())
        {
            _context.SaleItems.RemoveRange(_sale.Items);
            _sale.Items.Clear();
        }

        _sale.Items ??= new List<SaleItem>();
        foreach (var newItem in newItemsList)
        {
            _sale.Items.Add(newItem);
        }

        await RecalculateTotalAsync(_sale);
        await _context.SaveChangesAsync();

        return MapToDto(_sale);
    }

    public async Task<SaleDto> AddPaymentToHoldSaleAsync(int saleId, AddPaymentRequestDto request)
    {
        var _sale = await GetSaleEntityAsync(saleId);
        if (_sale.Status != SaleStatus.OnHold)
            throw new InvalidOperationException("Solo se pueden agregar abonos a ventas en estado en espera.");

        decimal rate = request.ExchangeRate > 0 ? request.ExchangeRate : _sale.AppliedRate;
        decimal amountUsd = request.AmountUSD > 0 
            ? Math.Round(request.AmountUSD, 2, MidpointRounding.AwayFromZero) 
            : (rate > 0 ? Math.Round(request.AmountBsS / rate, 2, MidpointRounding.AwayFromZero) : 0m);

        if (amountUsd <= 0 && request.AmountBsS <= 0)
        {
            throw new ArgumentException("El monto del abono debe ser mayor a cero.");
        }

        decimal currentPaidUsd = _sale.Payments.Sum(p => p.Amount);
        if (currentPaidUsd + amountUsd > _sale.TotalUSD + 0.05m)
        {
            throw new InvalidOperationException("El monto del abono excede el total pendiente de la venta.");
        }

        // Validación de integridad: el efectivo solo acepta montos enteros (sin centavos).
        var method = await _context.PaymentMethods.FindAsync(request.PaymentMethodId);
        if (method != null && method.IsCash && request.AmountBsS % 1 != 0)
        {
            throw new InvalidOperationException("El método de pago en efectivo solo acepta montos enteros.");
        }

        IDbContextTransaction? dbTransaction = null;
        if (_context.Database.ProviderName != null && !_context.Database.ProviderName.Contains("InMemory"))
        {
            dbTransaction = await _context.Database.BeginTransactionAsync();
        }

        try
        {
            var paymentEntity = new SalePayment
            {
                SaleId = _sale.Id,
                PaymentMethodId = request.PaymentMethodId,
                Amount = Math.Round(amountUsd, 2, MidpointRounding.AwayFromZero),
                AmountBsS = Math.Round(request.AmountBsS, 2, MidpointRounding.AwayFromZero),
                ExchangeRate = rate,
                ReferenceNumber = request.ReferenceNumber,
                CreatedAt = DateTime.UtcNow
            };

            _context.SalePayments.Add(paymentEntity);

            // Si es efectivo y monto positivo, registrar en sesión activa de caja
            if (method != null && method.IsCash && amountUsd > 0 && _cashDrawerService != null)
            {
                var activeSession = await _cashDrawerService.GetOrCreateActiveSessionAsync(rate);
                var cashTx = new CashTransaction
                {
                    SessionId = activeSession.Id,
                    Type = CashTransactionType.Income,
                    Source = CashTransactionSource.SalePayment,
                    AmountUsd = amountUsd,
                    ExchangeRate = rate,
                    AmountLocal = Math.Round(request.AmountBsS, 2, MidpointRounding.AwayFromZero),
                    IsPhysicalCash = true,
                    Description = $"Abono Venta #{saleId}",
                    TransactionTime = DateTime.UtcNow,
                    SaleId = _sale.Id,
                    PaymentMethodId = request.PaymentMethodId
                };
                _context.CashTransactions.Add(cashTx);
            }

            await _context.SaveChangesAsync();

            if (dbTransaction != null)
            {
                await dbTransaction.CommitAsync();
            }

            return MapToDto(_sale);
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
    }

    public async Task<IEnumerable<SaleDto>> GetPendingSalesAsync()
    {
        if (_inventoryService != null)
        {
            try
            {
                var todayRate = await _inventoryService.GetTodayExchangeRateAsync();
                if (todayRate > 0)
                {
                    await RecalculateOnHoldSalesAsync(todayRate);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to auto-recalculate OnHold sales in GetPendingSalesAsync.");
            }
        }

        var sales = await _context.Sales
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.Customer)
            .Include(s => s.Items)
            .Include(s => s.Payments)
                .ThenInclude(p => p.PaymentMethod)
            .Include(s => s.Cashier)
            .Where(s => s.Status == SaleStatus.OnHold)
            .OrderByDescending(s => s.Date)
            .ToListAsync();

        await PopulateItemsMetadataAsync(sales);

        return sales.Select(s => MapToDto(s));
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

        var exists = await _context.Customers.AnyAsync(c => c.CedulaOrRif.ToLower() == request.CedulaOrRif.Trim().ToLower());
        if (exists)
            throw new InvalidOperationException($"Ya existe un cliente registrado con la Cédula/RIF '{request.CedulaOrRif}'.");

        var customer = new Customer
        {
            CedulaOrRif = request.CedulaOrRif.Trim(),
            Name = request.Name.Trim(),
            Phone = request.Phone?.Trim() ?? string.Empty,
            CreditLimitUSD = request.CreditLimitUSD >= 0 ? request.CreditLimitUSD : 0m,
            IsActive = true
        };

        _context.Customers.Add(customer);
        await _context.SaveChangesAsync();

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

        var exists = await _context.Customers.AnyAsync(c => c.Id != id && c.CedulaOrRif.ToLower() == request.CedulaOrRif.Trim().ToLower());
        if (exists)
            throw new InvalidOperationException($"Ya existe otro cliente registrado con la Cédula/RIF '{request.CedulaOrRif}'.");

        customer.CedulaOrRif = request.CedulaOrRif.Trim();
        customer.Name = request.Name.Trim();
        customer.Phone = request.Phone?.Trim() ?? string.Empty;
        customer.CreditLimitUSD = request.CreditLimitUSD >= 0 ? request.CreditLimitUSD : 0m;
        customer.IsActive = request.IsActive;

        await _context.SaveChangesAsync();

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
}
