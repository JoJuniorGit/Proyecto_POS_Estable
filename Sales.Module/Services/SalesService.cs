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
    private readonly Sales.Module.Receipts.IReceiptPrintQueue? _receiptPrintQueue;
    private const string DefaultCustomerCacheKey = "default_customer_cache";

    public SalesService(
        SalesDbContext context,
        IInventoryService inventoryService,
        IMediator mediator,
        ICashDrawerService cashDrawerService,
        ISystemSettingsService settingsService,
        Microsoft.Extensions.Logging.ILogger<SalesService>? logger = null,
        IMemoryCache? cache = null,
        Sales.Module.Receipts.IReceiptPrintQueue? receiptPrintQueue = null)
    {
        _context = context;
        _inventoryService = inventoryService;
        _mediator = mediator;
        _cashDrawerService = cashDrawerService;
        _settingsService = settingsService;
        _logger = logger;
        _cache = cache;
        _receiptPrintQueue = receiptPrintQueue;
    }

    private async Task<bool> IsAllowNegativeStockEnabledAsync()
    {
        var value = await _settingsService.GetSettingAsync(Core.Constants.SettingKeys.AllowNegativeStock);
        return bool.TryParse(value, out var allowed) && allowed;
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

    public async Task<SaleDto> GetSaleAsync(int saleId)
    {
        var sale = await GetSaleEntityAsync(saleId, includeCashier: true);

        // 8.7-B6: los GET no escriben. La tasa de las OnHold se recalcula en el POST de tasa
        // (RecalculateOnHoldSalesAsync) y se re-difunde por SignalR; aquí solo se lee.

        await PopulateItemsMetadataAsync(sale);
        return MapToDto(sale);
    }

    private async Task<Sale> GetSaleEntityAsync(int saleId, bool includeCashier = false)
    {
        var query = _context.Sales
            .AsSplitQuery()
            .Include(s => s.Items)
            .Include(s => s.Customer)
            .Include(s => s.Payments)
                .ThenInclude(p => p.PaymentMethod);

        var sale = includeCashier
            ? await query.Include(s => s.Cashier).FirstOrDefaultAsync(s => s.Id == saleId)
            : await query.FirstOrDefaultAsync(s => s.Id == saleId);

        if (sale == null) throw new KeyNotFoundException($"Sale {saleId} not found.");
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

    public async Task<SaleDto> AddItemAsync(int saleId, int productId, decimal quantity, decimal exchangeRate, decimal? customUnitPriceUsd = null, decimal? customUnitPriceLocal = null, bool isPriceOverrideAuthorized = false)
    {
        var sale = await GetSaleEntityAsync(saleId);
        if (sale.Status != SaleStatus.Pending && sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("No se puede modificar una venta ya finalizada.");

        Product? product = null;
        if (_inventoryService != null)
        {
            product = await _inventoryService.GetProductByIdAsync(productId);
        }

        if (product != null && (product.IsDeleted || !product.IsActive))
        {
            throw new InvalidOperationException($"El producto '{product.Name}' no está disponible para la venta.");
        }

        bool isCashAdvance = product?.IsCashAdvance == true;
        if ((customUnitPriceUsd.HasValue || customUnitPriceLocal.HasValue) && !isPriceOverrideAuthorized && !isCashAdvance)
        {
            throw new UnauthorizedAccessException("Modificación de precios no autorizada. Se requiere rol de Administrador o Supervisor.");
        }

        if (customUnitPriceUsd.HasValue && customUnitPriceUsd.Value < 0m)
        {
            throw new ArgumentException("El precio no puede ser negativo.", nameof(customUnitPriceUsd));
        }
        if (customUnitPriceLocal.HasValue && customUnitPriceLocal.Value < 0m)
        {
            throw new ArgumentException("El precio en moneda local no puede ser negativo.", nameof(customUnitPriceLocal));
        }

        quantity = ValidateAndAdjustQuantity(product, quantity);

        // 8.6-B2: La tasa de cambio debe ser > 0 para persistir montos Bs.S consistentes durante la edición del carrito.
        if (exchangeRate <= 0)
        {
            throw new ArgumentException("La tasa de cambio debe ser mayor a cero.", nameof(exchangeRate));
        }

        sale.AppliedRate = exchangeRate;

        var existingItem = sale.Items.FirstOrDefault(i => i.ProductId == productId);
        if (existingItem != null)
        {
            if (customUnitPriceUsd.HasValue && existingItem.UnitPrice != customUnitPriceUsd.Value)
            {
                 var item = new SaleItem
                {
                    SaleId = saleId,
                    ProductId = productId,
                    ProductName = existingItem.ProductName,
                    UnitPrice = Math.Round(customUnitPriceUsd.Value, 4),
                    UnitPriceBsS = customUnitPriceLocal.HasValue ? Math.Round(customUnitPriceLocal.Value, 4) : 0,
                    Quantity = quantity,
                    IsCustomPrice = true
                };
                sale.Items.Add(item);
            }
            else
            {
                existingItem.Quantity += quantity;
                var productInfo = (customUnitPriceUsd.HasValue && customUnitPriceLocal.HasValue)
                    ? null
                    : await _inventoryService!.GetProductByIdAsync(productId);

                decimal grossPrice = customUnitPriceUsd ?? productInfo?.PriceUSD ?? existingItem.UnitPrice;
                decimal grossPriceBsS = customUnitPriceLocal ?? productInfo?.PriceBsS ?? existingItem.UnitPriceBsS;
                
                existingItem.UnitPrice = Math.Round(grossPrice, 4);
                existingItem.UnitPriceBsS = Math.Round(grossPriceBsS, 4);
                existingItem.IsCustomPrice = existingItem.IsCustomPrice || customUnitPriceUsd.HasValue || customUnitPriceLocal.HasValue;
            }
        }
        else
        {
            var fetchedProduct = await _inventoryService!.GetProductByIdAsync(productId);
            if (fetchedProduct == null) throw new KeyNotFoundException($"Product {productId} not found.");

            if (fetchedProduct.IsGroupHeader)
            {
                throw new ArgumentException($"El producto '{fetchedProduct.Name}' es un grupo de variantes. Debe seleccionar una variante específica para la venta.");
            }

            decimal grossPrice = customUnitPriceUsd ?? fetchedProduct.PriceUSD;
            decimal grossPriceBsS = customUnitPriceLocal ?? fetchedProduct.PriceBsS;

            var item = new SaleItem
            {
                SaleId = saleId,
                ProductId = productId,
                ProductName = fetchedProduct.Name,
                UnitPrice = Math.Round(grossPrice, 4),
                UnitPriceBsS = Math.Round(grossPriceBsS, 4),
                Quantity = quantity,
                IsCustomPrice = customUnitPriceUsd.HasValue || customUnitPriceLocal.HasValue
            };
            sale.Items.Add(item);
        }

        await RecalculateTotalAsync(sale);
        ValidateHoldSaleTotal(sale);

        await _context.SaveChangesAsync();
        return MapToDto(sale);
    }

    public async Task<SaleDto> RemoveItemAsync(int saleId, int itemId, decimal exchangeRate)
    {
        var sale = await GetSaleEntityAsync(saleId);
        if (sale.Status != SaleStatus.Pending && sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("No se puede modificar una venta ya finalizada.");

        sale.AppliedRate = exchangeRate;

        var item = sale.Items.FirstOrDefault(i => i.Id == itemId);
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

    public async Task<SaleDto> UpdateItemQuantityAsync(int saleId, int itemId, decimal quantity, decimal exchangeRate)
    {
        var sale = await GetSaleEntityAsync(saleId);
        if (sale.Status != SaleStatus.Pending && sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("No se puede modificar una venta ya finalizada.");

        sale.AppliedRate = exchangeRate;

        var item = sale.Items.FirstOrDefault(i => i.Id == itemId);
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

    public async Task CancelSaleAsync(int saleId)
    {
        var sale = await GetSaleEntityAsync(saleId);
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
        _logger?.LogInformation("Pedido #{SaleId} fue anulado exitosamente.", saleId);
    }

}
