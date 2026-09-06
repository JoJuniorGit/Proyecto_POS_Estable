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

public partial class SalesService : ISalesService
{
    private readonly SalesDbContext _context;
    private readonly IInventoryService _inventoryService;
    private readonly IMediator _mediator;
    private readonly ICashDrawerService _cashDrawerService;
    private readonly ISystemSettingsService _settingsService;
    private readonly Microsoft.Extensions.Logging.ILogger<SalesService>? _logger;
    private readonly IMemoryCache? _cache;
    private const string DefaultCustomerCacheKey = "default_customer_cache";

    public SalesService(
        SalesDbContext context,
        IInventoryService inventoryService,
        IMediator mediator,
        ICashDrawerService cashDrawerService,
        ISystemSettingsService settingsService,
        Microsoft.Extensions.Logging.ILogger<SalesService>? logger = null,
        IMemoryCache? cache = null)
    {
        _context = context;
        _inventoryService = inventoryService;
        _mediator = mediator;
        _cashDrawerService = cashDrawerService;
        _settingsService = settingsService;
        _logger = logger;
        _cache = cache;
    }

    private async Task<int> GenerateNextInvoiceNumberAsync()
    {
        if (_context.Database.IsNpgsql())
        {
            var nextVal = await _context.Database
                .SqlQueryRaw<long>("SELECT nextval('factura_number_seq') AS \"Value\"")
                .FirstOrDefaultAsync();
            return (int)nextVal;
        }
        else
        {
            var lastInvoice = await _context.Sales
                .Where(s => s.InvoiceNumber.HasValue)
                .MaxAsync(s => (int?)s.InvoiceNumber);
            return (lastInvoice ?? 0) + 1;
        }
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
            DeliveryStatus = SaleDeliveryStatus.PendingPickup
        };

        _context.Sales.Add(_sale);
        await _context.SaveChangesAsync();

        // 8.5-M2: Evitar write-on-read (GetSaleAsync re-recarga/recalcula la venta con side effects).
        // La venta recién creada no tiene items ni pagos; se mapea directamente sin round-trip.
        return new SaleDto
        {
            Id = _sale.Id,
            Date = _sale.Date,
            Status = _sale.Status.ToString(),
            CashierId = _sale.CashierId,
            CustomerName = defaultCustomer.Name,
            CustomerCedula = defaultCustomer.CedulaOrRif,
            DeliveryStatus = _sale.DeliveryStatus.ToString(),
            PriceListType = "Retail",
            Items = new System.Collections.Generic.List<SaleItemDto>(),
            Payments = new System.Collections.Generic.List<SalePaymentDto>()
        };
    }

    public async Task<SaleDto> GetSaleAsync(int sale_id)
    {
        var _sale = await GetSaleEntityAsync(sale_id, includeCashier: true);

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

        await PopulateItemsMetadataAsync(_sale);
        return MapToDto(_sale);
    }

    private async Task<Sale> GetSaleEntityAsync(int sale_id, bool includeCashier = false)
    {
        var query = _context.Sales
            .AsSplitQuery()
            .Include(s => s.Items)
            .Include(s => s.Customer)
            .Include(s => s.Payments)
                .ThenInclude(p => p.PaymentMethod);

        var _sale = includeCashier
            ? await query.Include(s => s.Cashier).FirstOrDefaultAsync(s => s.Id == sale_id)
            : await query.FirstOrDefaultAsync(s => s.Id == sale_id);

        if (_sale == null) throw new KeyNotFoundException($"Sale {sale_id} not found.");
        return _sale;
    }

    private static decimal ValidateAndAdjustQuantity(Product? product, decimal quantity)
    {
        if (quantity <= 0m)
        {
            throw new ArgumentException("La cantidad debe ser mayor a cero.", nameof(quantity));
        }

        if (product != null && !product.IsFractional)
        {
            return Math.Max(1m, Math.Truncate(quantity));
        }

        return Math.Round(quantity, 3, MidpointRounding.AwayFromZero);
    }

    private async Task<decimal> ValidateAndAdjustQuantityForProductAsync(int productId, decimal quantity)
    {
        Product? product = null;
        if (_inventoryService != null)
        {
            product = await _inventoryService.GetProductByIdAsync(productId);
        }

        return ValidateAndAdjustQuantity(product, quantity);
    }

    public async Task<SaleDto> AddItemAsync(int sale_id, int product_id, decimal quantity, decimal exchange_rate, decimal? custom_unit_price_usd = null, decimal? custom_unit_price_local = null, bool isPriceOverrideAuthorized = false)
    {
        var _sale = await GetSaleEntityAsync(sale_id);
        if (_sale.Status != SaleStatus.Pending && _sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("Cannot modify a completed sale.");

        Product? product = null;
        if (_inventoryService != null)
        {
            product = await _inventoryService.GetProductByIdAsync(product_id);
        }

        bool isCashAdvance = product?.IsCashAdvance == true;
        if ((custom_unit_price_usd.HasValue || custom_unit_price_local.HasValue) && !isPriceOverrideAuthorized && !isCashAdvance)
        {
            throw new UnauthorizedAccessException("Modificación de precios no autorizada. Se requiere rol de Administrador o Supervisor.");
        }

        if (custom_unit_price_usd.HasValue && custom_unit_price_usd.Value < 0m)
        {
            throw new ArgumentException("El precio no puede ser negativo.", nameof(custom_unit_price_usd));
        }
        if (custom_unit_price_local.HasValue && custom_unit_price_local.Value < 0m)
        {
            throw new ArgumentException("El precio en moneda local no puede ser negativo.", nameof(custom_unit_price_local));
        }

        quantity = ValidateAndAdjustQuantity(product, quantity);

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
                    : await _inventoryService!.GetProductByIdAsync(product_id);

                decimal _gross_price = custom_unit_price_usd ?? _product_info?.PriceUSD ?? _existing_item.UnitPrice;
                decimal _gross_price_bs_s = custom_unit_price_local ?? _product_info?.PriceBsS ?? _existing_item.UnitPriceBsS;
                
                _existing_item.UnitPrice = Math.Round(_gross_price, 4);
                _existing_item.UnitPriceBsS = Math.Round(_gross_price_bs_s, 4);
            }
        }
        else
        {
            var _product = await _inventoryService!.GetProductByIdAsync(product_id);
            if (_product == null) throw new KeyNotFoundException($"Product {product_id} not found.");

            if (_product.IsGroupHeader)
            {
                throw new InvalidOperationException($"El producto '{_product.Name}' es un grupo de variantes. Debe seleccionar una variante específica para la venta.");
            }

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
            if (_sale.Items.Count == 0)
            {
                _sale.Subtotal = 0;
                _sale.SubtotalBsS = 0;
                _sale.TotalUSD = 0;
                _sale.TotalBsS = 0;
            }
            else
            {
                await RecalculateTotalAsync(_sale);
            }
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
                if (_sale.Items.Count == 0)
                {
                    _sale.Subtotal = 0;
                    _sale.SubtotalBsS = 0;
                    _sale.TotalUSD = 0;
                    _sale.TotalBsS = 0;
                }
                else
                {
                    await RecalculateTotalAsync(_sale);
                }
            }
            else
            {
                quantity = await ValidateAndAdjustQuantityForProductAsync(_item.ProductId, quantity);
                _item.Quantity = quantity;
                await RecalculateTotalAsync(_sale);
            }
            ValidateHoldSaleTotal(_sale);
            await _context.SaveChangesAsync();
        }
        return MapToDto(_sale);
    }

    public async Task CancelSaleAsync(int sale_id)
    {
        var _sale = await GetSaleEntityAsync(sale_id);
        if (_sale.Status == SaleStatus.Completed) 
            throw new InvalidOperationException("No se puede anular una venta que ya ha sido completada.");
        if (_sale.Status == SaleStatus.Cancelled) 
            throw new InvalidOperationException("La venta ya se encuentra anulada.");
        if (_sale.Payments != null && _sale.Payments.Any()) 
            throw new InvalidOperationException("No se puede anular un pedido que posee abonos acumulados. Reembolse o reversa los abonos antes de anular.");
        if (_sale.DeliveryStatus == SaleDeliveryStatus.Delivered && _sale.PickupDate.HasValue) 
            throw new InvalidOperationException("No se puede anular un pedido que ya ha sido entregado al cliente.");

        _sale.Status = SaleStatus.Cancelled;
        await _context.SaveChangesAsync();
        _logger?.LogInformation("Pedido #{SaleId} fue anulado exitosamente.", sale_id);
    }

    public async Task<int> CompleteSaleAsync(
        int sale_id, 
        decimal exchange_rate, 
        IEnumerable<PaymentInfo> payments, 
        decimal roundingAdjustment = 0, 
        int? cashierId = null, 
        bool isPendingPickup = false, 
        string? idempotencyKey = null,
        byte[]? idempotencyPayloadHash = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        _logger?.LogInformation("[TX_START] CorrelationId={CorrelationId}, SaleId={SaleId}, IsolationLevel=ReadCommitted", correlationId, sale_id);

        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var _sale = await GetSaleEntityAsync(sale_id);

            // Idempotency check: if sale is already completed, return existing InvoiceNumber immediately
            if (_sale.Status == SaleStatus.Completed && _sale.InvoiceNumber.HasValue)
            {
                _logger?.LogInformation("[SalesService] Idempotency: Venta #{SaleId} ya se encontraba completada con Factura N° {InvoiceNumber}. Retornando consecutivo.", sale_id, _sale.InvoiceNumber.Value);
                return _sale.InvoiceNumber.Value;
            }

            await using var _transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken)
                : null;

            if (_transaction != null && _inventoryService != null)
            {
                var rawDbTx = _transaction.GetDbTransaction();
                await _inventoryService.EnrollInTransactionAsync(rawDbTx, cancellationToken);
            }

            try
            {
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

            // 8.5-A5: Auditoría de tasa BCV del día vs. tasa recibida del cliente.
            // Previene manipulación extrema (rechazo > ±100%) y genera trazabilidad para desvíos moderados (>10%).
            if (_inventoryService != null)
            {
                decimal officialRate;
                try
                {
                    officialRate = await _inventoryService.GetTodayExchangeRateAsync();
                }
                catch { officialRate = 0m; }

                if (officialRate > 0m)
                {
                    decimal deviationPct = Math.Abs(exchange_rate - officialRate) / officialRate;

                    if (deviationPct > 1.0m)
                    {
                        _logger?.LogError("[A5-AUDIT] Tasa rechazada por posible manipulación. SaleId={SaleId}, TasaRecibida={Received}, TasaBCV={Official}, Desvío={Deviation:P2}", sale_id, exchange_rate, officialRate, deviationPct);
                        throw new InvalidOperationException($"La tasa de cambio {exchange_rate} fue rechazada: excede ±100% de la tasa BCV oficial ({officialRate}). Contacte al supervisor.");
                    }

                    if (deviationPct > 0.10m)
                    {
                        _logger?.LogWarning("[A5-AUDIT] Desvío de tasa significativo (>10%) en cierre. SaleId={SaleId}, TasaRecibida={Received}, TasaBCV={Official}, Desvío={Deviation:P2}", sale_id, exchange_rate, officialRate, deviationPct);
                    }
                }
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

            if (isPendingPickup)
            {
                if (remainingBalanceUsd > 0.05m)
                {
                    throw new InvalidOperationException("Para registrar un apartado en custodia (Mercancía Pendiente por Retirar), la venta debe estar pagada al 100% (saldo restante $0.00).");
                }

                bool isDefaultCust = _sale.CustomerId == null || _sale.Customer == null || _sale.Customer.IsDefault || (_sale.CustomerName != null && _sale.CustomerName.ToLower().Contains("consumidor final"));
                if (isDefaultCust)
                {
                    throw new InvalidOperationException("Para registrar un apartado en custodia (Mercancía Pendiente por Retirar), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono).");
                }
            }

            var _active_session = await _cashDrawerService.GetOrCreateActiveSessionAsync(exchange_rate);

            if (!_sale.InvoiceNumber.HasValue)
            {
                _sale.InvoiceNumber = await GenerateNextInvoiceNumberAsync();
            }

            var paymentMethodsDict = new Dictionary<int, PaymentMethod>();
            if (payments != null && payments.Any())
            {
                var paymentMethodIds = payments.Select(p => p.PaymentMethodId).Distinct().ToList();
                paymentMethodsDict = await _context.PaymentMethods
                    .Where(pm => paymentMethodIds.Contains(pm.Id))
                    .ToDictionaryAsync(pm => pm.Id);

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

                    paymentMethodsDict.TryGetValue(_p.PaymentMethodId, out var _payment_method);

                    // Validación de integridad: el efectivo solo acepta montos enteros (sin centavos).
                    if (_payment_method != null && _payment_method.IsCash && amountLocal % 1 != 0)
                    {
                        throw new InvalidOperationException("El método de pago en efectivo solo acepta montos enteros.");
                    }

                    _logger?.LogDebug("[CURRENCY CONVERSION DEBUG] Método: {Method}, Monto Bs.S: {BsS}, Tasa AppliedRate: {Rate}, Monto USD Calculado: {Usd}", _p.PaymentMethodId, amountLocal, exchange_rate, amountUsd);

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
                            IsPhysicalCash = true,
                            Description = $"Factura N° {_sale.InvoiceNumber}",
                            TransactionTime = DateTime.UtcNow,
                            SaleId = _sale.Id,
                            PaymentMethodId = _p.PaymentMethodId
                        };
                        _context.CashTransactions.Add(_cash_tx);
                    }
                }
            }

            // H-SAL-3: Registro de vuelto como egreso (Expense) y validación de límites de sobrepago
            if (remainingBalanceUsd < -0.05m)
            {
                decimal changeUsd = Math.Abs(remainingBalanceUsd);
                if (changeUsd > 100m && changeUsd > _sale.TotalUSD)
                {
                    throw new InvalidOperationException($"El sobrepago o vuelto requerido (${changeUsd:F2} USD) excede los límites operacionales de seguridad.");
                }

                int? cashMethodId = _sale.Payments.FirstOrDefault(p => paymentMethodsDict.TryGetValue(p.PaymentMethodId, out var pm) && pm.IsCash)?.PaymentMethodId;

                decimal changeBsS = Math.Round(changeUsd * exchange_rate, 2, MidpointRounding.AwayFromZero);
                var _change_tx = new CashTransaction
                {
                    SessionId = _active_session.Id,
                    Type = CashTransactionType.Expense,
                    Source = CashTransactionSource.SalePayment,
                    AmountUsd = changeUsd,
                    ExchangeRate = exchange_rate,
                    AmountLocal = changeBsS,
                    IsPhysicalCash = true,
                    Description = $"Vuelto Factura N° {_sale.InvoiceNumber}",
                    TransactionTime = DateTime.UtcNow,
                    SaleId = _sale.Id,
                    PaymentMethodId = cashMethodId
                };
                _context.CashTransactions.Add(_change_tx);
                _logger?.LogInformation("[SalesService] Vuelto registrado en caja: ${ChangeUsd} USD / Bs. {ChangeBsS} para Factura N° {InvoiceNumber}",
                    changeUsd, changeBsS, _sale.InvoiceNumber);
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
            _sale.RoundingAdjustment = roundingAdjustment;

            // Synchronous Stock Deduction inside Transaction (H-SAL-2 / H-INV-1 / A1)
            var productsDict = new Dictionary<int, Product>();
            if (_inventoryService != null && _sale.Items != null)
            {
                var productIds = _sale.Items.Select(i => i.ProductId).Distinct().ToList();
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
                        userId: cashierId?.ToString(),
                        allowNegativeStock: false);
                }
            }

            // Transactional Outbox Message creation (A1 / H-SAL-2)
            var outboxPayload = JsonSerializer.Serialize(new
            {
                SaleId = _sale.Id,
                InvoiceNumber = _sale.InvoiceNumber.Value,
                Date = _sale.Date,
                TotalUSD = _sale.TotalUSD,
                TotalBsS = _sale.TotalBsS,
                CashierId = _sale.CashierId,
                IdempotencyKey = idempotencyKey,
                Items = _sale.Items?.Select(i => new { i.ProductId, i.Quantity, i.UnitPrice, i.Subtotal }).ToList()
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
                    RequestPath = $"/api/sales/{sale_id}/complete",
                    PayloadHash = idempotencyPayloadHash,
                    StatusCode = 200,
                    ResponseBody = JsonSerializer.Serialize(_sale.InvoiceNumber.Value),
                    CreatedAtUtc = DateTime.UtcNow,
                    ExpiresAtUtc = DateTime.UtcNow.AddHours(24)
                });
            }

            _logger?.LogInformation("[EF CORE ENTITY DEBUG] Persistiendo Sale ID: {SaleId}. Entidades SalePayment reales: {@Payments}", _sale.Id, _sale.Payments);

            await _context.SaveChangesAsync(cancellationToken);
            if (_transaction != null)
            {
                await _transaction.CommitAsync(cancellationToken);
            }

            _logger?.LogInformation("[TX_COMMIT] CorrelationId={CorrelationId}, SaleId={SaleId}, InvoiceNumber={InvoiceNumber}", correlationId, sale_id, _sale.InvoiceNumber.Value);

            try
            {
                var _items_snapshot = (_sale.Items ?? Enumerable.Empty<SaleItem>())
                    .Where(i => !productsDict.TryGetValue(i.ProductId, out var prod) || !prod.IsCashAdvance)
                    .Select(i => new SaleItemSnapshot(i.ProductId, i.Quantity))
                    .ToList();
                var _sale_made_event = new SaleMadeEvent(_sale.Id, _sale.Date, _items_snapshot, _sale.InvoiceNumber.Value);
                await _mediator.Publish(_sale_made_event, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[SalesService] Publicación de evento secundario SaleMadeEvent falló, pero la venta y el Outbox están garantizados en base de datos.");
            }

            return _sale.InvoiceNumber.Value;
        }
        catch (OperationCanceledException opEx)
        {
            if (_transaction != null)
            {
                await _transaction.RollbackAsync(System.Threading.CancellationToken.None);
            }
            _logger?.LogWarning(opEx, "[TX_ROLLBACK] CorrelationId={CorrelationId}, SaleId={SaleId}, Reason=OperationCanceled", correlationId, sale_id);
            throw;
        }
        catch (Exception ex)
        {
            if (_transaction != null)
            {
                await _transaction.RollbackAsync(System.Threading.CancellationToken.None);
            }
            _logger?.LogError(ex, "[TX_ROLLBACK] CorrelationId={CorrelationId}, SaleId={SaleId}, Reason={Reason}", correlationId, sale_id, ex.Message);
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
}

