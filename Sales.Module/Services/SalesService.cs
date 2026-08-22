using Core.DTOs;
using Core.Entities;
using Core.Events;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public class SalesService : ISalesService
{
    private readonly SalesDbContext _context;
    private readonly IInventoryService _inventoryService;
    private readonly IMediator _mediator;
    private readonly ICashDrawerService _cashDrawerService;
    private readonly ISystemSettingsService _settingsService;
    private readonly Microsoft.Extensions.Logging.ILogger<SalesService>? _logger;

    public SalesService(SalesDbContext context, IInventoryService inventoryService, IMediator mediator, ICashDrawerService cashDrawerService, ISystemSettingsService settingsService, Microsoft.Extensions.Logging.ILogger<SalesService>? logger = null)
    {
        _context = context;
        _inventoryService = inventoryService;
        _mediator = mediator;
        _cashDrawerService = cashDrawerService;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<SaleDto> StartSaleAsync(int? cashierId = null)
    {
        var defaultCustomer = await _context.Customers.FirstOrDefaultAsync(c => c.IsDefault) 
                           ?? await _context.Customers.FirstOrDefaultAsync(c => c.Id == 1);
        
        if (defaultCustomer == null)
            throw new InvalidOperationException("Default customer not found in system configuration. Ensure database is properly seeded.");

        var _sale = new Sale
        {
            Date = DateTime.UtcNow,
            Status = SaleStatus.Pending,
            CashierId = cashierId,
            CustomerId = defaultCustomer.Id,
            CustomerName = defaultCustomer.Name,
            CustomerCedula = defaultCustomer.CedulaOrRif,
            DeliveryStatus = SaleDeliveryStatus.Delivered
        };

        _context.Sales.Add(_sale);
        await _context.SaveChangesAsync();
        return await GetSaleAsync(_sale.Id);
    }

    public async Task<SaleDto> GetSaleAsync(int sale_id)
    {
        var _sale = await _context.Sales
            .Include(s => s.Items)
            .Include(s => s.Customer)
            .Include(s => s.Payments)
                .ThenInclude(p => p.PaymentMethod)
            .Include(s => s.Cashier)
            .FirstOrDefaultAsync(s => s.Id == sale_id);

        if (_sale == null) throw new KeyNotFoundException($"Sale {sale_id} not found.");

        if (_sale.Status == SaleStatus.OnHold && _inventoryService != null)
        {
            try
            {
                var todayRate = await _inventoryService.GetTodayExchangeRateAsync();
                if (todayRate > 0 && _sale.AppliedRate != todayRate)
                {
                    _sale.AppliedRate = todayRate;
                    await RecalculateTotalAsync(_sale);
                    await _context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to auto-recalculate OnHold sale {SaleId} in GetSaleAsync.", sale_id);
            }
        }

        return MapToDto(_sale);
    }

    private async Task<Sale> GetSaleEntityAsync(int sale_id)
    {
        var _sale = await _context.Sales
            .Include(s => s.Items)
            .Include(s => s.Customer)
            .Include(s => s.Payments)
                .ThenInclude(p => p.PaymentMethod)
            .FirstOrDefaultAsync(s => s.Id == sale_id);

        if (_sale == null) throw new KeyNotFoundException($"Sale {sale_id} not found.");
        return _sale;
    }

    private async Task<decimal> ValidateAndAdjustQuantityForProductAsync(int productId, decimal quantity)
    {
        if (quantity <= 0m)
        {
            throw new ArgumentException("La cantidad debe ser mayor a cero.", nameof(quantity));
        }

        return Math.Round(quantity, 3, MidpointRounding.AwayFromZero);
    }

    public async Task<SaleDto> AddItemAsync(int sale_id, int product_id, decimal quantity, decimal exchange_rate, decimal? custom_unit_price_usd = null, decimal? custom_unit_price_local = null)
    {
        var _sale = await GetSaleEntityAsync(sale_id);
        if (_sale.Status != SaleStatus.Pending && _sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("Cannot modify a completed sale.");

        if (custom_unit_price_usd.HasValue && custom_unit_price_usd.Value < 0m)
        {
            throw new ArgumentException("El precio no puede ser negativo.", nameof(custom_unit_price_usd));
        }
        if (custom_unit_price_local.HasValue && custom_unit_price_local.Value < 0m)
        {
            throw new ArgumentException("El precio en moneda local no puede ser negativo.", nameof(custom_unit_price_local));
        }

        quantity = await ValidateAndAdjustQuantityForProductAsync(product_id, quantity);

        _sale.AppliedRate = exchange_rate;

        var _existing_item = _sale.Items.FirstOrDefault(i => i.ProductId == product_id);
        if (_existing_item != null)
        {
            if (custom_unit_price_usd.HasValue && _existing_item.UnitPrice != custom_unit_price_usd.Value)
            {
                 var _item = new SaleItem
                {
                    SaleId = sale_id,
                    ProductId = product_id,
                    ProductName = _existing_item.ProductName,
                    UnitPrice = Math.Round(custom_unit_price_usd.Value, 4),
                    UnitPriceBsS = custom_unit_price_local.HasValue ? Math.Round(custom_unit_price_local.Value, 4) : 0,
                    Quantity = quantity
                };
                _sale.Items.Add(_item);
            }
            else
            {
                _existing_item.Quantity += quantity;
                var _product_info = (custom_unit_price_usd.HasValue && custom_unit_price_local.HasValue)
                    ? null
                    : await _inventoryService.GetProductByIdAsync(product_id);

                decimal _gross_price = custom_unit_price_usd ?? _product_info?.PriceUSD ?? _existing_item.UnitPrice;
                decimal _gross_price_bs_s = custom_unit_price_local ?? _product_info?.PriceBsS ?? _existing_item.UnitPriceBsS;
                
                _existing_item.UnitPrice = Math.Round(_gross_price, 4);
                _existing_item.UnitPriceBsS = Math.Round(_gross_price_bs_s, 4);
            }
        }
        else
        {
            var _product = await _inventoryService.GetProductByIdAsync(product_id);
            if (_product == null) throw new KeyNotFoundException($"Product {product_id} not found.");

            decimal _gross_price = custom_unit_price_usd ?? _product.PriceUSD;
            decimal _gross_price_bs_s = custom_unit_price_local ?? _product.PriceBsS;

            var _item = new SaleItem
            {
                SaleId = sale_id,
                ProductId = product_id,
                ProductName = _product.Name,
                UnitPrice = Math.Round(_gross_price, 4),
                UnitPriceBsS = Math.Round(_gross_price_bs_s, 4),
                Quantity = quantity
            };
            _sale.Items.Add(_item);
        }

        await RecalculateTotalAsync(_sale);
        ValidateHoldSaleTotal(_sale);

        await _context.SaveChangesAsync();
        return MapToDto(_sale);
    }

    public async Task<SaleDto> RemoveItemAsync(int sale_id, int item_id, decimal exchange_rate)
    {
        var _sale = await GetSaleEntityAsync(sale_id);
        if (_sale.Status != SaleStatus.Pending && _sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("Cannot modify a completed sale.");

        _sale.AppliedRate = exchange_rate;

        var _item = _sale.Items.FirstOrDefault(i => i.Id == item_id);
        if (_item != null)
        {
            _sale.Items.Remove(_item);
            _context.SaleItems.Remove(_item);
            await RecalculateTotalAsync(_sale);
            ValidateHoldSaleTotal(_sale);
            await _context.SaveChangesAsync();
        }

        return MapToDto(_sale);
    }

    public async Task<SaleDto> UpdateItemQuantityAsync(int sale_id, int item_id, decimal quantity, decimal exchange_rate)
    {
        var _sale = await GetSaleEntityAsync(sale_id);
        if (_sale.Status != SaleStatus.Pending && _sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("Cannot modify a completed sale.");

        _sale.AppliedRate = exchange_rate;

        var _item = _sale.Items.FirstOrDefault(i => i.Id == item_id);
        if (_item != null)
        {
            if (quantity <= 0m)
            {
                _sale.Items.Remove(_item);
                _context.SaleItems.Remove(_item);
            }
            else
            {
                quantity = await ValidateAndAdjustQuantityForProductAsync(_item.ProductId, quantity);
                _item.Quantity = quantity;
            }
            await RecalculateTotalAsync(_sale);
            ValidateHoldSaleTotal(_sale);
            await _context.SaveChangesAsync();
        }
        return MapToDto(_sale);
    }

    public async Task<SaleDto> UpdateExchangeRateAsync(int sale_id, decimal exchange_rate)
    {
        var _sale = await GetSaleEntityAsync(sale_id);
        if (_sale.Status != SaleStatus.Pending && _sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("Cannot modify a completed sale.");

        _sale.AppliedRate = exchange_rate;
        await RecalculateTotalAsync(_sale);
        await _context.SaveChangesAsync();
        return MapToDto(_sale);
    }

    public async Task CancelSaleAsync(int sale_id)
    {
        var _sale = await GetSaleEntityAsync(sale_id);
        if (_sale.Status == SaleStatus.Completed) throw new InvalidOperationException("Cannot cancel a completed sale.");

        _sale.Status = SaleStatus.Cancelled;
        await _context.SaveChangesAsync();
    }

    public async Task<int> CompleteSaleAsync(int sale_id, decimal exchange_rate, IEnumerable<PaymentInfo> payments, decimal roundingAdjustment = 0, int? cashierId = null, bool isPendingPickup = false)
    {
        using var _transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            var _sale = await GetSaleEntityAsync(sale_id);
            if (_sale.Status != SaleStatus.Pending && _sale.Status != SaleStatus.OnHold) 
                throw new InvalidOperationException("Sale is not pending or on hold.");

            if (isPendingPickup)
            {
                if (!_sale.CustomerId.HasValue)
                {
                    throw new InvalidOperationException("Para registrar un apartado pagado (mercancía en custodia), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono).");
                }

                var cust = await _context.Customers.FindAsync(_sale.CustomerId.Value);
                if (cust == null || cust.IsDefault || cust.CedulaOrRif == "V-00000000" || cust.Name.StartsWith("Consumidor Final", StringComparison.OrdinalIgnoreCase) || cust.Name.StartsWith("Cliente General", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Para registrar un apartado pagado (mercancía en custodia), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono).");
                }
            }

            if (cashierId.HasValue)
            {
                _sale.CashierId = cashierId.Value;
            }

            if (exchange_rate <= 0)
            {
                throw new InvalidOperationException("Rechazo Defensivo: Tasa de cambio AppliedRate inválida o no inicializada (<= 0).");
            }

            _sale.AppliedRate = exchange_rate;
            await RecalculateTotalAsync(_sale);

            _sale.RoundingAdjustment = roundingAdjustment;

            decimal existingPaidUsd = _sale.Payments.Sum(p => p.Amount);
            decimal newPaymentsPaidUsd = payments != null 
                ? payments.Sum(p => p.Amount > 0 ? p.Amount : (p.AmountLocal > 0 && exchange_rate > 0 ? Math.Round(p.AmountLocal / exchange_rate, 2, MidpointRounding.AwayFromZero) : 0m)) 
                : 0m;
            decimal totalPaidUsd = Math.Round(existingPaidUsd + newPaymentsPaidUsd, 2, MidpointRounding.AwayFromZero);
            decimal remainingBalanceUsd = Math.Round(_sale.TotalUSD - totalPaidUsd, 2, MidpointRounding.AwayFromZero);

            if (remainingBalanceUsd < -0.05m)
                throw new InvalidOperationException($"El abono (${totalPaidUsd:F2}) supera el total de la factura (${_sale.TotalUSD:F2}). Ajuste el cobro.");

            if (isPendingPickup && remainingBalanceUsd > 0.05m)
            {
                throw new InvalidOperationException("Para registrar un apartado en custodia (Mercancía Pendiente por Retirar), la venta debe estar pagada al 100% (saldo restante $0.00).");
            }

            var _active_session = await _cashDrawerService.GetOrCreateActiveSessionAsync(exchange_rate);

            if (!_sale.InvoiceNumber.HasValue)
            {
                var _last_invoice = await _context.Sales
                    .Where(s => s.InvoiceNumber.HasValue)
                    .MaxAsync(s => (int?)s.InvoiceNumber);

                _sale.InvoiceNumber = (_last_invoice ?? 0) + 1;
            }

            if (payments != null)
            {
                foreach (var _p in payments)
                {
                    decimal amountUsd = _p.Amount;
                    decimal amountLocal = _p.AmountLocal;

                    if (amountUsd <= 0 && amountLocal > 0 && exchange_rate > 0)
                    {
                        amountUsd = Math.Round(amountLocal / exchange_rate, 2, MidpointRounding.AwayFromZero);
                    }
                    else if (amountLocal <= 0 && amountUsd > 0 && exchange_rate > 0)
                    {
                        amountLocal = Math.Round(amountUsd * exchange_rate, 2, MidpointRounding.AwayFromZero);
                    }

                    var _payment_method = await _context.PaymentMethods.FindAsync(_p.PaymentMethodId);

                    // Validación de integridad: el efectivo solo acepta montos enteros (sin centavos).
                    if (_payment_method != null && _payment_method.IsCash && amountLocal % 1 != 0)
                    {
                        throw new InvalidOperationException("El método de pago en efectivo solo acepta montos enteros.");
                    }

                    _logger?.LogInformation("[CURRENCY CONVERSION DEBUG] Método: {Method}, Monto Bs.S: {BsS}, Tasa AppliedRate: {Rate}, Monto USD Calculado: {Usd}", _p.PaymentMethodId, amountLocal, exchange_rate, amountUsd);

                    var _payment_entity = new SalePayment
                    {
                        SaleId = _sale.Id,
                        PaymentMethodId = _p.PaymentMethodId,
                        Amount = Math.Round(amountUsd, 2, MidpointRounding.AwayFromZero),
                        AmountBsS = Math.Round(amountLocal, 2, MidpointRounding.AwayFromZero),
                        ExchangeRate = exchange_rate,
                        ReferenceNumber = _p.Reference,
                        CreatedAt = DateTime.UtcNow
                    };
                    _sale.Payments.Add(_payment_entity);

                    if (amountUsd > 0 && _payment_method != null && _payment_method.IsCash)
                    {
                        var _cash_tx = new CashTransaction
                            {
                                SessionId = _active_session.Id,
                                Type = CashTransactionType.Income,
                                Source = CashTransactionSource.SalePayment,
                                AmountUsd = amountUsd,
                                ExchangeRate = exchange_rate,
                                AmountLocal = amountLocal,
                                Description = $"Factura N° {_sale.InvoiceNumber}",
                                TransactionTime = DateTime.UtcNow,
                                SaleId = _sale.Id
                            };
                            _context.CashTransactions.Add(_cash_tx);
                        }
                    }
                }

            // 1. Defensive Aggregated Total Validation:
            if (_sale.Payments.Sum(p => p.Amount) <= 0 && !_sale.IsZeroAmountOrder)
            {
                throw new InvalidOperationException("Rechazo Defensivo: El total acumulado de los métodos de pago es <= 0. Se aborta el guardado local.");
            }

            if (_sale.AppliedRate <= 0)
            {
                throw new InvalidOperationException("Rechazo Defensivo: Tasa de cambio AppliedRate inválida o no inicializada (<= 0).");
            }

            // 2. Pre-Persistence Sanitation compatible with EF Core Change Tracker:
            var paymentsToRemove = _sale.Payments.Where(p => p.Amount == 0).ToList();
            foreach (var payment in paymentsToRemove)
            {
                _sale.Payments.Remove(payment);
            }

            if (remainingBalanceUsd > 0.05m)
            {
                throw new InvalidOperationException("El monto ingresado no cubre la totalidad de la venta. El flujo de cobro requiere liquidación al 100%. Para abonos parciales o guardar pedidos en espera, utilice la opción 'Guardar en Espera'.");
            }

            // Es liquidación total
            _sale.Status = SaleStatus.Completed;
            _sale.DeliveryStatus = isPendingPickup ? SaleDeliveryStatus.PendingPickup : SaleDeliveryStatus.Delivered;
            _sale.Date = DateTime.UtcNow;
            _sale.AppliedRate = exchange_rate;
            await RecalculateTotalAsync(_sale);
            _sale.FinalPaidAmountBsS = _sale.Payments.Sum(p => p.AmountBsS);

            _logger?.LogInformation("[EF CORE ENTITY DEBUG] Persistiendo Sale ID: {SaleId}. Entidades SalePayment reales: {@Payments}", _sale.Id, _sale.Payments);

            await _context.SaveChangesAsync();
            await _transaction.CommitAsync();

            var _items_snapshot = _sale.Items.Select(i => new SaleItemSnapshot(i.ProductId, i.Quantity)).ToList();
            var _sale_made_event = new SaleMadeEvent(_sale.Id, _sale.Date, _items_snapshot);
            await _mediator.Publish(_sale_made_event);

            return _sale.InvoiceNumber.Value;
        }
        catch
        {
            await _transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<SaleHistoryDto> ConfirmPickupAsync(int saleId)
    {
        var _sale = await _context.Sales
            .Include(s => s.Customer)
            .Include(s => s.Items)
            .Include(s => s.Payments).ThenInclude(p => p.PaymentMethod)
            .FirstOrDefaultAsync(s => s.Id == saleId);

        if (_sale == null) throw new KeyNotFoundException("Sale not found.");

        if (_sale.DeliveryStatus != SaleDeliveryStatus.PendingPickup)
            throw new InvalidOperationException($"El pedido #{_sale.InvoiceNumber ?? _sale.Id} no se encuentra en estado Pendiente por Retirar.");

        _sale.DeliveryStatus = SaleDeliveryStatus.Delivered;
        _sale.PickupDate = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return await GetSaleHistoryDetailAsync(saleId);
    }

    public async Task<IEnumerable<PendingPickupDto>> GetPendingPickupsAsync()
    {
        var _sales = await _context.Sales
            .Include(s => s.Customer)
            .Include(s => s.Items)
            .Where(s => s.Status == SaleStatus.Completed && s.DeliveryStatus == SaleDeliveryStatus.PendingPickup)
            .OrderByDescending(s => s.Date)
            .ToListAsync();

        return _sales.Select(s => new PendingPickupDto
        {
            SaleId = s.Id,
            InvoiceNumber = s.InvoiceNumber,
            Date = s.Date,
            CustomerId = s.CustomerId,
            CustomerName = s.CustomerName ?? s.Customer?.Name ?? "Cliente Desconocido",
            CustomerCedula = s.CustomerCedula ?? s.Customer?.CedulaOrRif ?? "V-00000000",
            CustomerPhone = s.Customer?.Phone ?? string.Empty,
            TotalUSD = s.TotalUSD,
            TotalBsS = s.TotalBsS,
            DeliveryStatus = s.DeliveryStatus.ToString(),
            PickupDate = s.PickupDate,
            Items = s.Items.Select(i => new SaleItemHistoryDto
            {
                Id = i.Id,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                UnitPriceBsS = i.UnitPriceBsS,
                SubtotalBsS = i.SubtotalBsS
            }).ToList()
        });
    }
    public async Task<CustomerDto> GetDefaultCustomerAsync()
    {
        var defaultCustomer = await _context.Customers.FirstOrDefaultAsync(c => c.IsDefault)
                           ?? await _context.Customers.FirstOrDefaultAsync(c => c.Id == 1);
        if (defaultCustomer == null) throw new KeyNotFoundException("Cliente por defecto no encontrado.");
        
        return new CustomerDto
        {
            Id = defaultCustomer.Id,
            CedulaOrRif = defaultCustomer.CedulaOrRif,
            Name = defaultCustomer.Name,
            Phone = defaultCustomer.Phone,
            CreditLimitUSD = defaultCustomer.CreditLimitUSD,
            IsActive = defaultCustomer.IsActive,
            IsDefault = defaultCustomer.IsDefault
        };
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

        foreach (var payment in paymentsToProcess)
        {
            decimal rate = payment.ExchangeRate > 0 ? payment.ExchangeRate : _sale.AppliedRate;
            decimal amountUsd = payment.AmountUSD > 0 
                ? payment.AmountUSD 
                : (rate > 0 ? payment.AmountBsS / rate : 0);

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
        }

        decimal totalPaidUsd = Math.Round(_sale.Payments.Sum(p => p.Amount), 2, MidpointRounding.AwayFromZero);
        decimal remainingBalanceUsd = Math.Round(_sale.TotalUSD - totalPaidUsd, 2, MidpointRounding.AwayFromZero);

        _sale.CustomerId = customer.Id;
        _sale.CustomerName = customer.Name;
        _sale.CustomerCedula = customer.CedulaOrRif;

        if (totalPaidUsd > 0 && _sale.TotalUSD > 0 && remainingBalanceUsd <= 0.05m && totalPaidUsd >= (_sale.TotalUSD - 0.05m))
        {
            // Se cubrió el 100% mediante los pagos iniciales -> Completar y generar factura
            var lastInvoice = await _context.Sales
                .Where(s => s.InvoiceNumber.HasValue)
                .MaxAsync(s => (int?)s.InvoiceNumber);

            _sale.InvoiceNumber = (lastInvoice ?? 0) + 1;
            _sale.Status = SaleStatus.Completed;
            _sale.Date = DateTime.UtcNow;
            _sale.FinalPaidAmountBsS = _sale.Payments.Sum(p => p.AmountBsS);

            await _context.SaveChangesAsync();

            var itemsSnapshot = _sale.Items.Select(i => new SaleItemSnapshot(i.ProductId, i.Quantity)).ToList();
            var saleMadeEvent = new SaleMadeEvent(_sale.Id, _sale.Date, itemsSnapshot);
            await _mediator.Publish(saleMadeEvent);
        }
        else
        {
            _sale.Status = SaleStatus.OnHold;
            await _context.SaveChangesAsync();
        }

        return MapToDto(_sale);
    }

    public async Task<SaleDto> UpdateSaleItemsAsync(int saleId, UpdateSaleItemsRequestDto request)
    {
        var _sale = await GetSaleEntityAsync(saleId);
        if (_sale.Status != SaleStatus.OnHold)
            throw new InvalidOperationException("Solo se pueden modificar productos en ventas que estén en estado en espera (OnHold).");

        decimal totalPaidUsd = _sale.Payments != null ? _sale.Payments.Sum(p => p.Amount) : 0;

        // Calcular nuevo total USD a partir de la lista de ítems enviada
        decimal newTotalUsd = 0;
        var newItemsList = new List<SaleItem>();

        if (request?.Items != null)
        {
            foreach (var reqItem in request.Items)
            {
                if (reqItem.Quantity <= 0m) continue;

                decimal adjustedQty = await ValidateAndAdjustQuantityForProductAsync(reqItem.ProductId, reqItem.Quantity);

                var product = await _inventoryService.GetProductByIdAsync(reqItem.ProductId);
                string productName = product != null ? product.Name : $"Producto #{reqItem.ProductId}";
                decimal unitPriceUsd = reqItem.UnitPrice > 0 ? reqItem.UnitPrice : (product != null ? product.PriceUSD : 0);
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
            ? request.AmountUSD 
            : (rate > 0 ? request.AmountBsS / rate : 0);

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

        await _context.SaveChangesAsync();
        return MapToDto(_sale);
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
            .Include(s => s.Customer)
            .Include(s => s.Items)
            .Include(s => s.Payments)
                .ThenInclude(p => p.PaymentMethod)
            .Include(s => s.Cashier)
            .Where(s => s.Status == SaleStatus.OnHold)
            .OrderByDescending(s => s.Date)
            .ToListAsync();

        return sales.Select(s => MapToDto(s));
    }

    /// <inheritdoc />
    public async Task<int> RecalculateOnHoldSalesAsync(decimal newExchangeRate)
    {
        if (newExchangeRate <= 0)
            return 0;

        var onHoldSales = await _context.Sales
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .Where(s => s.Status == SaleStatus.OnHold)
            .ToListAsync();

        if (!onHoldSales.Any())
            return 0;

        foreach (var sale in onHoldSales)
        {
            sale.AppliedRate = newExchangeRate;
            await RecalculateTotalAsync(sale);
        }

        await _context.SaveChangesAsync();
        return onHoldSales.Count;
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
                .Where(s => s.CustomerId.HasValue)
                .GroupBy(s => s.CustomerId!.Value)
                .OrderByDescending(g => g.Max(s => s.Date))
                .Select(g => g.Key)
                .Take(3)
                .ToListAsync();

            var recentCustomers = await _context.Customers
                .Where(c => recentCustomerIds.Contains(c.Id))
                .ToListAsync();

            if (recentCustomers.Count < 3)
            {
                var existingIds = recentCustomers.Select(c => c.Id).ToList();
                var additional = await _context.Customers
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

        var q = _context.Customers.AsQueryable();

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
            throw new InvalidOperationException($"El cliente '{customer.Name}' ({customer.CedulaOrRif}) tiene ventas registradas en el sistema. No se puede eliminar físicamente; utilice la opción de desactivar (poner Inactivo).");
        }

        _context.Customers.Remove(customer);
        await _context.SaveChangesAsync();
    }


    public async Task<(IEnumerable<SaleHistoryDto> Items, int TotalCount)> GetSalesHistoryAsync(int page, int pageSize, DateTime? startDate, DateTime? endDate, string? search = null)
    {
        var query = _context.Sales
            .Include(s => s.Cashier)
            .Include(s => s.Customer)
            .Where(s => s.Status == SaleStatus.Completed);

        // La columna Date es "timestamp with time zone" (UTC): Npgsql rechaza parámetros
        // DateTime con Kind != Utc. Los clientes envían fechas sin zona horaria (YYYY-MM-DD
        // o medianoche local), por lo que se interpretan como hora local del servidor y se
        // convierten a UTC antes de comparar.
        DateTime ToUtc(DateTime value)
            => value.Kind == DateTimeKind.Utc
                ? value
                : TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(value, DateTimeKind.Unspecified), TimeZoneInfo.Local);

        // Se calculan ANTES del árbol de expresión (una función local no puede
        // referenciarse dentro de la lambda que EF Core traduce a SQL).
        DateTime? startUtc = startDate.HasValue ? ToUtc(startDate.Value) : null;
        // La fecha fin es inclusiva de TODO el día seleccionado: la venta pertenece al día
        // si su fecha es anterior a la medianoche local del día siguiente (convertida a UTC).
        DateTime? endExclusiveUtc = endDate.HasValue ? ToUtc(endDate.Value.Date.AddDays(1)) : null;

        if (startUtc.HasValue) query = query.Where(s => s.Date >= startUtc.Value);
        if (endExclusiveUtc.HasValue) query = query.Where(s => s.Date < endExclusiveUtc.Value);

        // Búsqueda multicampo: coincidencia de texto (insensible a mayúsculas) simultánea
        // en N° de factura, cliente (nombre o cédula) y cajero (nombre, nombre completo o cédula).
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var lowerTerm = term.ToLower();
            var isNumericTerm = int.TryParse(term, out var invoiceMatch);

            query = query.Where(s =>
                (s.CustomerName != null && s.CustomerName.ToLower().Contains(lowerTerm)) ||
                (s.CustomerCedula != null && s.CustomerCedula.ToLower().Contains(lowerTerm)) ||
                (s.Cashier != null &&
                 ((s.Cashier.Name != null && s.Cashier.Name.ToLower().Contains(lowerTerm)) ||
                  (s.Cashier.FullName != null && s.Cashier.FullName.ToLower().Contains(lowerTerm)) ||
                  (s.Cashier.Cedula != null && s.Cashier.Cedula.ToLower().Contains(lowerTerm)))) ||
                (s.InvoiceNumber != null && s.InvoiceNumber.Value.ToString().Contains(term)) ||
                (isNumericTerm && s.InvoiceNumber == invoiceMatch));
        }

        int totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(s => s.Date)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SaleHistoryDto
            {
                Id = s.Id,
                InvoiceNumber = s.InvoiceNumber,
                Date = s.Date,
                TotalUSD = s.TotalUSD,
                AppliedRate = s.AppliedRate,
                TotalBsS = s.TotalBsS,
                Status = s.Status.ToString(),
                FinalPaidAmountBsS = s.FinalPaidAmountBsS,
                CashierName = s.Cashier != null ? (!string.IsNullOrWhiteSpace(s.Cashier.Name) ? s.Cashier.Name : (!string.IsNullOrWhiteSpace(s.Cashier.FullName) ? s.Cashier.FullName : s.Cashier.Cedula)) : "Usuario Desconocido",
                CustomerName = s.CustomerName ?? (s.Customer != null ? s.Customer.Name : "Consumidor Final"),
                CustomerCedula = s.CustomerCedula ?? (s.Customer != null ? s.Customer.CedulaOrRif : "V-00000000"),
                DeliveryStatus = s.DeliveryStatus.ToString(),
                PickupDate = s.PickupDate
            })
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task<SaleHistoryDto> GetSaleHistoryDetailAsync(int saleId)
    {
        var _sale = await _context.Sales
            .Include(s => s.Items)
            .Include(s => s.Payments)
                .ThenInclude(p => p.PaymentMethod)
            .Include(s => s.Cashier)
            .Include(s => s.Customer)
            .FirstOrDefaultAsync(s => s.Id == saleId);

        if (_sale == null) throw new KeyNotFoundException($"Sale {saleId} not found.");

        return new SaleHistoryDto
        {
            Id = _sale.Id,
            InvoiceNumber = _sale.InvoiceNumber,
            Date = _sale.Date,
            TotalUSD = _sale.TotalUSD,
            AppliedRate = _sale.AppliedRate,
            TotalBsS = _sale.TotalBsS,
            Status = _sale.Status.ToString(),
            FinalPaidAmountBsS = _sale.FinalPaidAmountBsS,
            CashierName = _sale.Cashier != null ? (!string.IsNullOrWhiteSpace(_sale.Cashier.Name) ? _sale.Cashier.Name : (!string.IsNullOrWhiteSpace(_sale.Cashier.FullName) ? _sale.Cashier.FullName : _sale.Cashier.Cedula)) : "Usuario Desconocido",
            CustomerName = _sale.CustomerName ?? (_sale.Customer != null ? _sale.Customer.Name : "Consumidor Final"),
            CustomerCedula = _sale.CustomerCedula ?? (_sale.Customer != null ? _sale.Customer.CedulaOrRif : "V-00000000"),
            DeliveryStatus = _sale.DeliveryStatus.ToString(),
            PickupDate = _sale.PickupDate,
            Items = _sale.Items.Select(i => new SaleItemHistoryDto
            {
                Id = i.Id,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                UnitPriceBsS = i.UnitPriceBsS,
                SubtotalBsS = i.SubtotalBsS
            }).ToList(),
            Payments = _sale.Payments.Select(p => new PaymentDetailDto
            {
                MethodName = p.PaymentMethod != null ? p.PaymentMethod.Name : "Desconocido",
                AmountBsS = p.AmountBsS,
                Reference = p.ReferenceNumber
            }).ToList()
        };
    }

    public async Task<SaleDto> UpdatePriceListAsync(int saleId, string priceListType)
    {
        if (string.IsNullOrWhiteSpace(priceListType) || (priceListType != "Retail" && priceListType != "Wholesale"))
        {
            throw new ArgumentException("Tipo de lista de precios no válido. Debe ser 'Retail' o 'Wholesale'.");
        }

        var sale = await _context.Sales
            .Include(s => s.Items)
            .Include(s => s.Payments)
                .ThenInclude(p => p.PaymentMethod)
            .Include(s => s.Customer)
            .FirstOrDefaultAsync(s => s.Id == saleId);

        if (sale == null)
        {
            throw new KeyNotFoundException($"Venta #{saleId} no encontrada.");
        }

        if (sale.Status == SaleStatus.Completed)
        {
            throw new InvalidOperationException("No se puede modificar la lista de precios de una venta ya finalizada.");
        }

        sale.PriceListType = priceListType;
        await RecalculateTotalAsync(sale);

        if (sale.Status == SaleStatus.OnHold && sale.Payments.Any())
        {
            decimal totalPaidUsd = sale.Payments.Sum(p => p.Amount);
            if (sale.TotalUSD < totalPaidUsd)
            {
                throw new InvalidOperationException("No se puede cambiar la lista de precios: el nuevo total en USD es menor al monto ya abonado por el cliente.");
            }
        }

        await _context.SaveChangesAsync();
        return MapToDto(sale);
    }

    private async Task RecalculateTotalAsync(Sale sale)
    {
        if (sale.Items != null && sale.Items.Any())
        {
            var productIds = sale.Items.Select(i => i.ProductId).Distinct().ToList();
            var products = new Dictionary<int, Product>();

            if (_inventoryService != null)
            {
                var fetched = await _inventoryService.GetProductsByIdsAsync(productIds);
                if (fetched != null && fetched.Count > 0)
                {
                    products = fetched.ToDictionary(p => p.Id);
                }
                else
                {
                    // Fallback para stubs/mocks en pruebas que únicamente configuran GetProductByIdAsync
                    foreach (var id in productIds)
                    {
                        var p = await _inventoryService.GetProductByIdAsync(id);
                        if (p != null)
                        {
                            products[p.Id] = p;
                        }
                    }
                }
            }

            foreach (var item in sale.Items)
            {
                // Regla 1: Lookup O(1) desde el batch fetch
                products.TryGetValue(item.ProductId, out var product);
                if (product != null)
                {
                    item.IsFractional = product.IsFractional;
                    item.UnitOfMeasure = product.UnitOfMeasure;

                    // Regla 2: Fallback anti-precio cero y umbral mayorista
                    var wholesalePrice = (product.PriceWholesaleUSD > 0) 
                        ? product.PriceWholesaleUSD 
                        : (product.PriceRetailUSD > 0 ? product.PriceRetailUSD : product.PriceUSD);
                    var retailPrice = (product.PriceRetailUSD > 0) ? product.PriceRetailUSD : product.PriceUSD;

                    var minWholesaleQty = product.MinWholesaleQuantity > 0 ? product.MinWholesaleQuantity : 6m;

                    if (string.Equals(sale.PriceListType, "Wholesale", StringComparison.OrdinalIgnoreCase) && product.HasWholesale && item.Quantity >= minWholesaleQty)
                    {
                        item.UnitPrice = wholesalePrice;
                        item.IsWholesaleApplied = true;
                    }
                    else
                    {
                        item.UnitPrice = retailPrice;
                        item.IsWholesaleApplied = false;
                    }
                }
                else if (item.UnitPrice == 0)
                {
                    throw new KeyNotFoundException($"Producto #{item.ProductId} no encontrado en la base de datos.");
                }

                item.UnitPriceBsS = item.UnitPrice * sale.AppliedRate;
                item.Subtotal = item.Quantity * item.UnitPrice;
                item.SubtotalBsS = item.Subtotal * sale.AppliedRate;
            }

            sale.Subtotal = sale.Items.Sum(i => i.Subtotal);
            sale.SubtotalBsS = sale.Items.Sum(i => i.SubtotalBsS);

            sale.TotalUSD = Math.Round(sale.Subtotal, 2, MidpointRounding.AwayFromZero);
            sale.TotalBsS = Math.Round(sale.SubtotalBsS, 2, MidpointRounding.AwayFromZero);
        }
        else if (sale.AppliedRate > 0)
        {
            sale.TotalBsS = Math.Round(sale.TotalUSD * sale.AppliedRate, 2, MidpointRounding.AwayFromZero);
            sale.SubtotalBsS = sale.TotalBsS;
        }
    }

    private void ValidateHoldSaleTotal(Sale sale)
    {
        if (sale.Status == SaleStatus.OnHold)
        {
            decimal totalPaidUsd = sale.Payments.Sum(p => p.Amount);
            if (sale.TotalUSD < totalPaidUsd)
            {
                throw new InvalidOperationException($"El nuevo total de la venta (${sale.TotalUSD:F2}) no puede ser menor al monto que ya ha sido abonado por el cliente (${totalPaidUsd:F2}).");
            }
        }
    }

    private SaleDto MapToDto(Sale sale)
    {
        var totalPaidUsd = sale.Payments.Sum(p => p.Amount);
        var remainingBalanceUsd = Math.Max(0, sale.TotalUSD - totalPaidUsd);

        return new SaleDto
        {
            Id = sale.Id,
            InvoiceNumber = sale.InvoiceNumber,
            Date = sale.Date,
            Status = sale.Status.ToString(),
            Subtotal = sale.Subtotal,
            TotalUSD = sale.TotalUSD,
            AppliedRate = sale.AppliedRate,
            TotalBsS = sale.TotalBsS,
            FinalPaidAmountBsS = sale.FinalPaidAmountBsS,
            SubtotalBsS = sale.SubtotalBsS,
            CashierId = sale.CashierId,
            CashierName = sale.Cashier != null ? (string.IsNullOrWhiteSpace(sale.Cashier.Name) ? sale.Cashier.FullName : sale.Cashier.Name) : "Usuario Desconocido",
            CustomerName = sale.CustomerName,
            CustomerCedula = sale.CustomerCedula,
            DeliveryStatus = sale.DeliveryStatus.ToString(),
            PickupDate = sale.PickupDate,
            PriceListType = string.IsNullOrWhiteSpace(sale.PriceListType) ? "Retail" : sale.PriceListType,
            CustomerId = sale.CustomerId,
            Customer = sale.Customer != null ? new CustomerDto
            {
                Id = sale.Customer.Id,
                CedulaOrRif = sale.Customer.CedulaOrRif,
                Name = sale.Customer.Name,
                Phone = sale.Customer.Phone,
                CreditLimitUSD = sale.Customer.CreditLimitUSD,
                IsActive = sale.Customer.IsActive,
                IsDefault = sale.Customer.IsDefault
            } : null,
            TotalPaidUSD = totalPaidUsd,
            RemainingBalanceUSD = remainingBalanceUsd,
            Items = sale.Items.Select(i => new SaleItemDto
            {
                Id = i.Id,
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                IsFractional = i.IsFractional,
                UnitOfMeasure = i.UnitOfMeasure,
                UnitPrice = i.UnitPrice,
                Subtotal = i.Subtotal,
                UnitPriceBsS = i.UnitPriceBsS,
                SubtotalBsS = i.SubtotalBsS,
                IsWholesaleApplied = i.IsWholesaleApplied
            }).ToList(),
            Payments = sale.Payments.Select(p => new SalePaymentDto
            {
                Id = p.Id,
                PaymentMethodId = p.PaymentMethodId,
                PaymentMethodName = p.PaymentMethod != null ? p.PaymentMethod.Name : "Desconocido",
                Amount = p.Amount,
                AmountBsS = p.AmountBsS,
                ExchangeRate = p.ExchangeRate,
                ReferenceNumber = p.ReferenceNumber,
                CreatedAt = p.CreatedAt
            }).ToList()
        };
    }

    public async Task<Sale> CreateCashAdvanceSaleAsync(
        decimal requestedAmountLocal,
        decimal commissionAmountLocal,
        int paymentMethodId,
        string paymentMethodName,
        bool isTransfer,
        decimal exchangeRate,
        int? cashierId = null,
        string? userName = null,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? existingTransaction = null)
    {
        if (existingTransaction != null && _context.Database.ProviderName != null && !_context.Database.ProviderName.Contains("InMemory"))
        {
            var dbTx = existingTransaction.GetDbTransaction();
            if (dbTx != null)
            {
                _context.Database.UseTransaction(dbTx);
            }
        }

        // 1. Obtener o auto-garantizar el ID del producto especial IsCashAdvance
        int productId = 1;
        if (_inventoryService != null)
        {
            try
            {
                var p = await _inventoryService.GetCashAdvanceProductAsync();

                if (p != null)
                {
                    productId = p.Id;
                }
                else
                {
                    var newP = await _inventoryService.CreateProductAsync(new Product
                    {
                        Name = "Adelanto de Efectivo",
                        SKU = "ADV-001",
                        PriceRetailUSD = 0m,
                        StockQuantity = 999999,
                        IsCashAdvance = true,
                        IsActive = true
                    });
                    productId = newP.Id;
                }
            }
            catch
            {
                productId = 1;
            }
        }

        // 2. Resolver usuario / cajero
        int? resolvedCashierId = cashierId;
        if (!resolvedCashierId.HasValue && !string.IsNullOrWhiteSpace(userName))
        {
            var matchedUser = await _context.Users.FirstOrDefaultAsync(u => u.Name == userName || u.FullName == userName || u.Cedula == userName);
            resolvedCashierId = matchedUser?.Id;
        }

        if (!resolvedCashierId.HasValue)
        {
            var defaultUser = await _context.Users.FirstOrDefaultAsync(u => u.IsActive);
            resolvedCashierId = defaultUser?.Id;
        }

        // 3. Obtener cliente por defecto
        var defaultCustomer = await _context.Customers.FirstOrDefaultAsync(c => c.IsDefault)
                           ?? await _context.Customers.FirstOrDefaultAsync(c => c.Id == 1);

        int customerId = defaultCustomer?.Id ?? 1;
        string customerName = defaultCustomer?.Name ?? "CLIENTE CONTADO";
        string customerCedula = defaultCustomer?.CedulaOrRif ?? "V-00000000";

        // 4. Consecutivo de Facturación atómico en transacción
        int nextInvoice = (await _context.Sales.MaxAsync(s => (int?)s.InvoiceNumber) ?? 0) + 1;

        decimal totalChargedLocal = requestedAmountLocal + commissionAmountLocal;
        decimal totalChargedUSD = exchangeRate > 0 ? Math.Round(totalChargedLocal / exchangeRate, 4) : 0m;

        // 5. Crear Sale completado con el CashierId resuelto
        var sale = new Sale
        {
            Date = DateTime.UtcNow,
            Status = SaleStatus.Completed,
            DeliveryStatus = SaleDeliveryStatus.Delivered,
            InvoiceNumber = nextInvoice,
            CashierId = resolvedCashierId,
            CustomerId = customerId,
            CustomerName = customerName,
            CustomerCedula = customerCedula,
            AppliedRate = exchangeRate,
            Subtotal = totalChargedUSD,
            TotalUSD = totalChargedUSD,
            SubtotalBsS = totalChargedLocal,
            TotalBsS = totalChargedLocal,
            FinalPaidAmountBsS = totalChargedLocal
        };

        _context.Sales.Add(sale);
        await _context.SaveChangesAsync();

        // 6. Crear SaleItem asignando explícitamente el precio unitario y subtotal (sobreescribiendo precio base 0)
        var saleItem = new SaleItem
        {
            SaleId = sale.Id,
            ProductId = productId,
            ProductName = $"Adelanto de Efectivo ({paymentMethodName})",
            Quantity = 1m,
            UnitPrice = totalChargedUSD,
            Subtotal = totalChargedUSD,
            UnitPriceBsS = totalChargedLocal,
            SubtotalBsS = totalChargedLocal
        };

        _context.SaleItems.Add(saleItem);

        // 7. Crear SalePayment a nombre del método electrónico
        var payment = new SalePayment
        {
            SaleId = sale.Id,
            PaymentMethodId = paymentMethodId,
            Amount = totalChargedUSD,
            AmountBsS = totalChargedLocal,
            ExchangeRate = exchangeRate,
            CreatedAt = DateTime.UtcNow,
            ReferenceNumber = $"ADELANTO-{DateTime.UtcNow:yyyyMMddHHmmss}"
        };

        _context.SalePayments.Add(payment);
        await _context.SaveChangesAsync();

        return sale;
    }
}
