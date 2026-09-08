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
            throw new InvalidOperationException("Cliente por defecto no encontrado en la configuración del sistema. Verifique que la base de datos esté correctamente sembrada.");

        var sale = new Sale
        {
            Date = DateTime.UtcNow,
            Status = SaleStatus.Pending,
            CashierId = cashierId,
            CustomerId = defaultCustomer.Id,
            CustomerName = defaultCustomer.Name,
            CustomerCedula = defaultCustomer.CedulaOrRif,
            DeliveryStatus = SaleDeliveryStatus.PendingPickup
        };

        _context.Sales.Add(sale);
        await _context.SaveChangesAsync();

        // 8.5-M2: Evitar write-on-read (GetSaleAsync re-recarga/recalcula la venta con side effects).
        // La venta recién creada no tiene items ni pagos; se mapea directamente sin round-trip.
        return new SaleDto
        {
            Id = sale.Id,
            Date = sale.Date,
            Status = sale.Status.ToString(),
            CashierId = sale.CashierId,
            CustomerName = defaultCustomer.Name,
            CustomerCedula = defaultCustomer.CedulaOrRif,
            DeliveryStatus = sale.DeliveryStatus.ToString(),
            PriceListType = "Retail",
            Items = new System.Collections.Generic.List<SaleItemDto>(),
            Payments = new System.Collections.Generic.List<SalePaymentDto>()
        };
    }

    public async Task<SaleDto> GetSaleAsync(int sale_id)
    {
        var sale = await GetSaleEntityAsync(sale_id, includeCashier: true);

        // 8.7-B6: los GET no escriben. La tasa de las OnHold se recalcula en el POST de tasa
        // (RecalculateOnHoldSalesAsync) y se re-difunde por SignalR; aquí solo se lee.

        await PopulateItemsMetadataAsync(sale);
        return MapToDto(sale);
    }

    private async Task<Sale> GetSaleEntityAsync(int sale_id, bool includeCashier = false)
    {
        var query = _context.Sales
            .AsSplitQuery()
            .Include(s => s.Items)
            .Include(s => s.Customer)
            .Include(s => s.Payments)
                .ThenInclude(p => p.PaymentMethod);

        var sale = includeCashier
            ? await query.Include(s => s.Cashier).FirstOrDefaultAsync(s => s.Id == sale_id)
            : await query.FirstOrDefaultAsync(s => s.Id == sale_id);

        if (sale == null) throw new KeyNotFoundException($"Sale {sale_id} not found.");
        return sale;
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
        var sale = await GetSaleEntityAsync(sale_id);
        if (sale.Status != SaleStatus.Pending && sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("No se puede modificar una venta ya finalizada.");

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

        // 8.6-B2: La tasa de cambio debe ser > 0 para persistir montos Bs.S consistentes durante la edición del carrito.
        if (exchange_rate <= 0)
        {
            throw new ArgumentException("La tasa de cambio debe ser mayor a cero.", nameof(exchange_rate));
        }

        sale.AppliedRate = exchange_rate;

        var existingItem = sale.Items.FirstOrDefault(i => i.ProductId == product_id);
        if (existingItem != null)
        {
            if (custom_unit_price_usd.HasValue && existingItem.UnitPrice != custom_unit_price_usd.Value)
            {
                 var item = new SaleItem
                {
                    SaleId = sale_id,
                    ProductId = product_id,
                    ProductName = existingItem.ProductName,
                    UnitPrice = Math.Round(custom_unit_price_usd.Value, 4),
                    UnitPriceBsS = custom_unit_price_local.HasValue ? Math.Round(custom_unit_price_local.Value, 4) : 0,
                    Quantity = quantity
                };
                sale.Items.Add(item);
            }
            else
            {
                existingItem.Quantity += quantity;
                var productInfo = (custom_unit_price_usd.HasValue && custom_unit_price_local.HasValue)
                    ? null
                    : await _inventoryService!.GetProductByIdAsync(product_id);

                decimal grossPrice = custom_unit_price_usd ?? productInfo?.PriceUSD ?? existingItem.UnitPrice;
                decimal grossPriceBsS = custom_unit_price_local ?? productInfo?.PriceBsS ?? existingItem.UnitPriceBsS;
                
                existingItem.UnitPrice = Math.Round(grossPrice, 4);
                existingItem.UnitPriceBsS = Math.Round(grossPriceBsS, 4);
            }
        }
        else
        {
            var fetchedProduct = await _inventoryService!.GetProductByIdAsync(product_id);
            if (fetchedProduct == null) throw new KeyNotFoundException($"Product {product_id} not found.");

            if (fetchedProduct.IsGroupHeader)
            {
                throw new InvalidOperationException($"El producto '{fetchedProduct.Name}' es un grupo de variantes. Debe seleccionar una variante específica para la venta.");
            }

            decimal grossPrice = custom_unit_price_usd ?? fetchedProduct.PriceUSD;
            decimal grossPriceBsS = custom_unit_price_local ?? fetchedProduct.PriceBsS;

            var item = new SaleItem
            {
                SaleId = sale_id,
                ProductId = product_id,
                ProductName = fetchedProduct.Name,
                UnitPrice = Math.Round(grossPrice, 4),
                UnitPriceBsS = Math.Round(grossPriceBsS, 4),
                Quantity = quantity
            };
            sale.Items.Add(item);
        }

        await RecalculateTotalAsync(sale);
        ValidateHoldSaleTotal(sale);

        await _context.SaveChangesAsync();
        return MapToDto(sale);
    }

    public async Task<SaleDto> RemoveItemAsync(int sale_id, int item_id, decimal exchange_rate)
    {
        var sale = await GetSaleEntityAsync(sale_id);
        if (sale.Status != SaleStatus.Pending && sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("No se puede modificar una venta ya finalizada.");

        sale.AppliedRate = exchange_rate;

        var item = sale.Items.FirstOrDefault(i => i.Id == item_id);
        if (item != null)
        {
            sale.Items.Remove(item);
            _context.SaleItems.Remove(item);
            if (sale.Items.Count == 0)
            {
                sale.Subtotal = 0;
                sale.SubtotalBsS = 0;
                sale.TotalUSD = 0;
                sale.TotalBsS = 0;
            }
            else
            {
                await RecalculateTotalAsync(sale);
            }
            ValidateHoldSaleTotal(sale);
            await _context.SaveChangesAsync();
        }

        return MapToDto(sale);
    }

    public async Task<SaleDto> UpdateItemQuantityAsync(int sale_id, int item_id, decimal quantity, decimal exchange_rate)
    {
        var sale = await GetSaleEntityAsync(sale_id);
        if (sale.Status != SaleStatus.Pending && sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("No se puede modificar una venta ya finalizada.");

        sale.AppliedRate = exchange_rate;

        var item = sale.Items.FirstOrDefault(i => i.Id == item_id);
        if (item != null)
        {
            if (quantity <= 0m)
            {
                sale.Items.Remove(item);
                _context.SaleItems.Remove(item);
                if (sale.Items.Count == 0)
                {
                    sale.Subtotal = 0;
                    sale.SubtotalBsS = 0;
                    sale.TotalUSD = 0;
                    sale.TotalBsS = 0;
                }
                else
                {
                    await RecalculateTotalAsync(sale);
                }
            }
            else
            {
                quantity = await ValidateAndAdjustQuantityForProductAsync(item.ProductId, quantity);
                item.Quantity = quantity;
                await RecalculateTotalAsync(sale);
            }
            ValidateHoldSaleTotal(sale);
            await _context.SaveChangesAsync();
        }
        return MapToDto(sale);
    }

    public async Task CancelSaleAsync(int sale_id)
    {
        var sale = await GetSaleEntityAsync(sale_id);
        if (sale.Status == SaleStatus.Completed) 
            throw new InvalidOperationException("No se puede anular una venta que ya ha sido completada.");
        if (sale.Status == SaleStatus.Cancelled) 
            throw new InvalidOperationException("La venta ya se encuentra anulada.");
        if (sale.Payments != null && sale.Payments.Any()) 
            throw new InvalidOperationException("No se puede anular un pedido que posee abonos acumulados. Reembolse o reversa los abonos antes de anular.");
        if (sale.DeliveryStatus == SaleDeliveryStatus.Delivered && sale.PickupDate.HasValue) 
            throw new InvalidOperationException("No se puede anular un pedido que ya ha sido entregado al cliente.");

        sale.Status = SaleStatus.Cancelled;
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
            var sale = await GetSaleEntityAsync(sale_id);

            // Idempotency check: if sale is already completed, return existing InvoiceNumber immediately
            if (sale.Status == SaleStatus.Completed && sale.InvoiceNumber.HasValue)
            {
                _logger?.LogInformation("[SalesService] Idempotency: Venta #{SaleId} ya se encontraba completada con Factura N° {InvoiceNumber}. Retornando consecutivo.", sale_id, sale.InvoiceNumber.Value);
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
                    throw new InvalidOperationException("Para registrar un apartado pagado (mercancía en custodia), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono).");
                }

                var cust = await _context.Customers.FindAsync(sale.CustomerId.Value);
                if (cust == null || cust.IsDefault || cust.CedulaOrRif == "V-00000000" || cust.Name.StartsWith("Consumidor Final", StringComparison.OrdinalIgnoreCase) || cust.Name.StartsWith("Cliente General", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Para registrar un apartado pagado (mercancía en custodia), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono).");
                }
            }

            if (cashierId.HasValue)
            {
                sale.CashierId = cashierId.Value;
            }

            if (exchange_rate <= 0)
            {
                throw new InvalidOperationException("Rechazo Defensivo: Tasa de cambio AppliedRate inválida o no inicializada (<= 0).");
            }

            // 8.5-A5/8.6-B3: Anclaje de tasa BCV del día vs. tasa recibida del cliente.
            // Si el desvío supera la tolerancia configurable (>10% por defecto) la tasa BCEV del día
            // se ANCLA como tasa efectiva (evita manipulación del AppliedRate / shortage enmascarado);
            // desvíos ≥ ±100% se rechazan. Sin catch-swallow: los errores del BCV se auditan.
            exchange_rate = await ResolveAnchoredRateAsync(
                exchange_rate,
                contextLabel: "CompleteSale",
                referenceId: sale_id);

            sale.AppliedRate = exchange_rate;
            await RecalculateTotalAsync(sale);

            // 8.7-B2: Acotar el ajuste de redondeo a un límite operacional (refuerzo del [Range]).
            if (Math.Abs(roundingAdjustment) > 1000m)
            {
                throw new InvalidOperationException($"Rechazo Defensivo: el ajuste de redondeo ({roundingAdjustment:F2}) excede el límite operacional de ±1000.");
            }

            sale.RoundingAdjustment = roundingAdjustment;

            decimal existingPaidUsd = sale.Payments.Sum(p => p.Amount);
            decimal newPaymentsPaidUsd = payments != null 
                ? payments.Sum(p => p.Amount > 0 ? p.Amount : (p.AmountLocal > 0 && exchange_rate > 0 ? Math.Round(p.AmountLocal / exchange_rate, 2, MidpointRounding.AwayFromZero) : 0m)) 
                : 0m;
            decimal totalPaidUsd = Math.Round(existingPaidUsd + newPaymentsPaidUsd, 2, MidpointRounding.AwayFromZero);
            decimal remainingBalanceUsd = Math.Round(sale.TotalUSD - totalPaidUsd, 2, MidpointRounding.AwayFromZero);

            if (isPendingPickup)
            {
                if (remainingBalanceUsd > 0.05m)
                {
                    throw new InvalidOperationException("Para registrar un apartado en custodia (Mercancía Pendiente por Retirar), la venta debe estar pagada al 100% (saldo restante $0.00).");
                }

                bool isDefaultCust = sale.CustomerId == null || sale.Customer == null || sale.Customer.IsDefault || (sale.CustomerName != null && sale.CustomerName.ToLower().Contains("consumidor final"));
                if (isDefaultCust)
                {
                    throw new InvalidOperationException("Para registrar un apartado en custodia (Mercancía Pendiente por Retirar), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono).");
                }
            }

            var activeSession = await _cashDrawerService.GetOrCreateActiveSessionAsync(exchange_rate);

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

                    if (amountUsd <= 0 && amountLocal > 0 && exchange_rate > 0)
                    {
                        amountUsd = Math.Round(amountLocal / exchange_rate, 2, MidpointRounding.AwayFromZero);
                    }
                    else if (amountLocal <= 0 && amountUsd > 0 && exchange_rate > 0)
                    {
                        amountLocal = Math.Round(amountUsd * exchange_rate, 2, MidpointRounding.AwayFromZero);
                    }

                    paymentMethodsDict.TryGetValue(p.PaymentMethodId, out var paymentMethod);

                    // 8.6-B3: Validación pre-persistencia del método de pago: un PaymentMethodId inexistente
                    // o inactivo aborta el cobro (evita asociar pagos a configuraciones inválidas).
                    if (paymentMethod == null || !paymentMethod.IsActive)
                    {
                        throw new InvalidOperationException($"Método de pago inválido o inactivo: PaymentMethodId={p.PaymentMethodId}. Verifique la configuración de métodos de pago.");
                    }

                    // 8.7-B2: Rechazo de montos NEGATIVOS por método. Sin esta validación, un pago con
                    // Amount <= 0 Y AmountLocal <= 0 se persistiría tal cual y distorsionaría los totales
                    // liquidados y el arqueo diario (suma de AmountBsS). Los ceros absolutos se purgan en la
                    // sanitización pre-persistencia (:592-596) y los montos mixtos (una sola moneda) se
                    // convierten arriba.
                    if (amountUsd < 0m || amountLocal < 0m)
                    {
                        throw new InvalidOperationException($"La validación del método de pago (PaymentMethodId={p.PaymentMethodId}) rechaza montos negativos. Monto USD={p.Amount}, Monto Bs.S={p.AmountLocal}.");
                    }

                    // Validación de integridad: el efectivo solo acepta montos enteros (sin centavos).
                    if (paymentMethod != null && paymentMethod.IsCash && amountLocal % 1 != 0)
                    {
                        throw new InvalidOperationException("El método de pago en efectivo solo acepta montos enteros.");
                    }

                    _logger?.LogDebug("[CURRENCY CONVERSION DEBUG] Método: {Method}, Monto Bs.S: {BsS}, Tasa AppliedRate: {Rate}, Monto USD Calculado: {Usd}", p.PaymentMethodId, amountLocal, exchange_rate, amountUsd);

                    var paymentEntity = new SalePayment
                    {
                        SaleId = sale.Id,
                        PaymentMethodId = p.PaymentMethodId,
                        Amount = Math.Round(amountUsd, 2, MidpointRounding.AwayFromZero),
                        AmountBsS = Math.Round(amountLocal, 2, MidpointRounding.AwayFromZero),
                        ExchangeRate = exchange_rate,
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
                            ExchangeRate = exchange_rate,
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
                    throw new InvalidOperationException($"El sobrepago o vuelto requerido (${changeUsd:F2} USD) excede los límites operacionales de seguridad.");
                }

                int? cashMethodId = sale.Payments.FirstOrDefault(p => paymentMethodsDict.TryGetValue(p.PaymentMethodId, out var pm) && pm.IsCash)?.PaymentMethodId;

                decimal changeBsS = Math.Round(changeUsd * exchange_rate, 2, MidpointRounding.AwayFromZero);

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
                    exchangeRate: exchange_rate,
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
                throw new InvalidOperationException("El monto ingresado no cubre la totalidad de la venta. El flujo de cobro requiere liquidación al 100%. Para abonos parciales o guardar pedidos en espera, utilice la opción 'Guardar en Espera'.");
            }

            // Es liquidación total
            sale.Status = SaleStatus.Completed;
            sale.DeliveryStatus = isPendingPickup ? SaleDeliveryStatus.PendingPickup : SaleDeliveryStatus.Delivered;
            sale.Date = DateTime.UtcNow;
            sale.AppliedRate = exchange_rate;
            await RecalculateTotalAsync(sale);
            sale.FinalPaidAmountBsS = sale.Payments.Sum(p => p.AmountBsS);
            sale.RoundingAdjustment = roundingAdjustment;

            // Synchronous Stock Deduction inside Transaction (H-SAL-2 / H-INV-1 / A1)
            var productsDict = new Dictionary<int, Product>();
            if (_inventoryService != null && sale.Items != null)
            {
                var productIds = sale.Items.Select(i => i.ProductId).Distinct().ToList();
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
                        $"Sale #{sale.InvoiceNumber.Value}"));
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
                    RequestPath = $"/api/sales/{sale_id}/complete",
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
                // 8.7-B7: ya committeado, el InventoryDbContext vuelve a su propia conexión
                // (el handler de evento y las operaciones posteriores no comparten la ajena).
                if (_inventoryService != null)
                {
                    await _inventoryService.DetachFromTransactionAsync(cancellationToken);
                }
            }

            _logger?.LogInformation("[TX_COMMIT] CorrelationId={CorrelationId}, SaleId={SaleId}, InvoiceNumber={InvoiceNumber}", correlationId, sale_id, sale.InvoiceNumber.Value);

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
            _logger?.LogWarning(opEx, "[TX_ROLLBACK] CorrelationId={CorrelationId}, SaleId={SaleId}, Reason=OperationCanceled", correlationId, sale_id);
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

    /// <summary>
    /// Ancla la tasa de cambio recibida del cliente a la tasa BCV del día (8.5-A5/8.6-B3).
    /// - Desvío ≤ tolerancia configurable (default 10%): se acepta la tasa recibida.
    /// - Desvío > tolerancia: se ANCLA la tasa BCV del día como tasa efectiva (audit).
    /// - Desvío ≥ ±100%: rechazo por posible manipulación.
    /// - Sin BCV del día disponible (0/no registrado): se continúa con la tasa recibida (fail-open auditable,
    ///   sin catch-swallow: los fallos del servicio BCV se registran y NO se ignoran silenciosamente).
    /// </summary>
    private async Task<decimal> ResolveAnchoredRateAsync(decimal clientRate, string contextLabel, int referenceId)
    {
        if (clientRate <= 0m)
        {
            throw new InvalidOperationException("Rechazo Defensivo: Tasa de cambio AppliedRate inválida o no inicializada (<= 0).");
        }

        if (_inventoryService == null)
        {
            _logger?.LogWarning("[A5-AUDIT] Servicio de inventario/BCV no disponible en {Context} #{Ref}. Se usa la tasa recibida: {Rate}", contextLabel, referenceId, clientRate);
            return clientRate;
        }

        decimal officialRate = 0m;
        try
        {
            officialRate = await _inventoryService.GetTodayExchangeRateAsync();
        }
        catch (System.Exception ex)
        {
            // 8.5-A5: SIN catch-swallow. Se audita el fallo y se continúa con fail-open ordenado.
            _logger?.LogError(ex, "[A5-AUDIT] Error obteniendo la tasa BCV del día en {Context} #{Ref}. Fail-open: se usa la tasa recibida {Rate}.", contextLabel, referenceId, clientRate);
            return clientRate;
        }

        if (officialRate <= 0m)
        {
            _logger?.LogWarning("[A5-AUDIT] Sin tasa BCV del día registrada en {Context} #{Ref}. Fail-open: se usa la tasa recibida {Rate}.", contextLabel, referenceId, clientRate);
            return clientRate;
        }

        decimal deviationPct = Math.Abs(clientRate - officialRate) / officialRate;

        if (deviationPct >= 1.0m)
        {
            _logger?.LogError("[A5-AUDIT] Tasa rechazada por posible manipulación. {Context} #{Ref}, TasaRecibida={Received}, TasaBCV={Official}, Desvío={Deviation:P2}", contextLabel, referenceId, clientRate, officialRate, deviationPct);
            throw new InvalidOperationException($"La tasa de cambio {clientRate} fue rechazada: excede ±100% de la tasa BCV oficial ({officialRate}). Contacte al supervisor.");
        }

        decimal tolerancePct = 0.10m;
        if (_settingsService != null)
        {
            try
            {
                var toleranceSetting = await _settingsService.GetSettingAsync("RateDeviationTolerancePct");
                if (decimal.TryParse(toleranceSetting, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                    && parsed > 0m && parsed < 1.0m)
                {
                    tolerancePct = parsed;
                }
            }
            catch (System.Exception ex)
            {
                _logger?.LogWarning(ex, "[A5-AUDIT] No se pudo leer la tolerancia configurada en {Context} #{Ref}; se usa el default {Tolerance:P2}.", contextLabel, referenceId, tolerancePct);
            }
        }

        if (deviationPct > tolerancePct)
        {
            _logger?.LogWarning("[A5-AUDIT] Desvío de tasa significativo ({Deviation:P2} > {Tolerance:P2}) en {Context} #{Ref}. Se ANCLA a la tasa BCV del día: {Received} -> {Official}", deviationPct, tolerancePct, contextLabel, referenceId, clientRate, officialRate);
            return officialRate;
        }

        return clientRate;
    }
}

