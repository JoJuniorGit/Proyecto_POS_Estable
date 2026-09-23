using Core.DTOs;
using Core.Entities;
using Core.Events;
using Core.Helpers;
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
    private readonly Sales.Module.Interfaces.IHoldOrderNotifier? _holdOrderNotifier;
    private const string DefaultCustomerCacheKey = "default_customer_cache";

    public SalesService(
        SalesDbContext context,
        IInventoryService inventoryService,
        IMediator mediator,
        ICashDrawerService cashDrawerService,
        ISystemSettingsService settingsService,
        Sales.Module.Interfaces.IHoldOrderNotifier? holdOrderNotifier = null,
        Microsoft.Extensions.Logging.ILogger<SalesService>? logger = null,
        IMemoryCache? cache = null,
        Sales.Module.Receipts.IReceiptPrintQueue? receiptPrintQueue = null)
    {
        _context = context;
        _inventoryService = inventoryService;
        _mediator = mediator;
        _cashDrawerService = cashDrawerService;
        _settingsService = settingsService;
        _holdOrderNotifier = holdOrderNotifier;
        _logger = logger;
        _cache = cache;
        _receiptPrintQueue = receiptPrintQueue;
    }

    private async Task NotifyHoldOrdersChangedAsync()
    {
        if (_holdOrderNotifier == null) return;

        try
        {
            await _holdOrderNotifier.NotifyHoldOrdersChangedAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SalesService] Notificación de pedidos en espera falló.");
        }
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

    public async Task<SaleDto> StartSaleAsync(int? cashierId = null, System.Threading.CancellationToken cancellationToken = default)
    {
        var defaultCustomer = await _context.Customers.FirstOrDefaultAsync(c => c.IsDefault, cancellationToken) 
                           ?? await _context.Customers.FirstOrDefaultAsync(c => c.Id == 1, cancellationToken);
        
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
        await _context.SaveChangesAsync(cancellationToken);

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

    public async Task<SaleDto> GetSaleAsync(int saleId, System.Threading.CancellationToken cancellationToken = default)
    {
        var sale = await GetSaleEntityAsync(saleId, includeCashier: true, asNoTracking: true);

        await PopulateItemsMetadataAsync(sale);
        return MapToDto(sale);
    }

    private async Task<Sale> GetSaleEntityAsync(int saleId, bool includeCashier = false, bool asNoTracking = false)
    {
        var query = _context.Sales
            .AsSplitQuery();

        if (asNoTracking && _context.Database.IsRelational())
        {
            query = query.AsNoTracking();
        }

        query = query
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

    private static decimal ValidateAndAdjustQuantity(SaleProductInfoDto? product, decimal quantity)
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
        SaleProductInfoDto? product = null;
        if (_inventoryService != null)
        {
            product = await _inventoryService.GetSaleProductByIdAsync(productId);
        }

        return ValidateAndAdjustQuantity(product, quantity);
    }

    public async Task<SaleDto> AddItemAsync(int saleId, int productId, decimal quantity, decimal exchangeRate, decimal? customUnitPriceUsd = null, decimal? customUnitPriceLocal = null, bool isPriceOverrideAuthorized = false, int? actingUserId = null, System.Threading.CancellationToken cancellationToken = default)
    {
        var sale = await GetSaleEntityAsync(saleId);
        EnsureHoldClaimAccess(sale, actingUserId);
        if (sale.Status != SaleStatus.Pending && sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("No se puede modificar una venta ya finalizada.");

        SaleProductInfoDto? product = null;
        if (_inventoryService != null)
        {
            product = await _inventoryService.GetSaleProductByIdAsync(productId, cancellationToken);
        }

        EnsureProductAvailableForSale(product);
        EnsurePriceOverrideAllowed(product, customUnitPriceUsd, customUnitPriceLocal, isPriceOverrideAuthorized);
        EnsureCustomPricesNonNegative(customUnitPriceUsd, customUnitPriceLocal);

        quantity = ValidateAndAdjustQuantity(product, quantity);

        if (exchangeRate <= 0)
        {
            throw new ArgumentException("La tasa de cambio debe ser mayor a cero.", nameof(exchangeRate));
        }

        sale.AppliedRate = await ResolveAnchoredRateAsync(exchangeRate, contextLabel: "AddItem", referenceId: sale.Id);

        var existingItem = sale.Items.FirstOrDefault(i => i.ProductId == productId);
        if (existingItem != null)
        {
            await UpdateOrSplitExistingItemAsync(sale, saleId, productId, product, existingItem, quantity, customUnitPriceUsd, customUnitPriceLocal, cancellationToken);
        }
        else
        {
            sale.Items.Add(await BuildNewSaleItemAsync(saleId, productId, quantity, customUnitPriceUsd, customUnitPriceLocal, cancellationToken));
        }

        await RecalculateTotalAsync(sale);
        ValidateHoldSaleTotal(sale);

        await _context.SaveChangesAsync(cancellationToken);
        return MapToDto(sale);
    }

    private static void EnsureProductAvailableForSale(SaleProductInfoDto? product)
    {
        if (product != null && (product.IsDeleted || !product.IsActive))
        {
            throw new InvalidOperationException($"El producto '{product.Name}' no está disponible para la venta.");
        }
    }

    private static void EnsurePriceOverrideAllowed(SaleProductInfoDto? product, decimal? customUnitPriceUsd, decimal? customUnitPriceLocal, bool isPriceOverrideAuthorized)
    {
        bool isCashAdvance = product?.IsCashAdvance == true;
        if ((customUnitPriceUsd.HasValue || customUnitPriceLocal.HasValue) && !isPriceOverrideAuthorized && !isCashAdvance)
        {
            throw new UnauthorizedAccessException("Modificación de precios no autorizada. Se requiere rol de Administrador o Supervisor.");
        }
    }

    private static void EnsureCustomPricesNonNegative(decimal? customUnitPriceUsd, decimal? customUnitPriceLocal)
    {
        if (customUnitPriceUsd.HasValue && customUnitPriceUsd.Value < 0m)
        {
            throw new ArgumentException("El precio no puede ser negativo.", nameof(customUnitPriceUsd));
        }
        if (customUnitPriceLocal.HasValue && customUnitPriceLocal.Value < 0m)
        {
            throw new ArgumentException("El precio en moneda local no puede ser negativo.", nameof(customUnitPriceLocal));
        }
    }

    private async Task UpdateOrSplitExistingItemAsync(Sale sale, int saleId, int productId, SaleProductInfoDto? product, SaleItem existingItem, decimal quantity, decimal? customUnitPriceUsd, decimal? customUnitPriceLocal, System.Threading.CancellationToken cancellationToken)
    {
        if (customUnitPriceUsd.HasValue && existingItem.UnitPrice != customUnitPriceUsd.Value)
        {
            sale.Items.Add(new SaleItem
            {
                SaleId = saleId,
                ProductId = productId,
                ProductName = existingItem.ProductName,
                UnitPrice = Math.Round(customUnitPriceUsd.Value, 4),
                UnitPriceBsS = customUnitPriceLocal.HasValue ? Math.Round(customUnitPriceLocal.Value, 4) : 0,
                Quantity = quantity,
                UnitCostUSD = product?.CostPriceUSD,
                IsCustomPrice = true
            });
            return;
        }

        await MergeExistingItemAsync(existingItem, productId, quantity, customUnitPriceUsd, customUnitPriceLocal, cancellationToken);
    }

    private async Task MergeExistingItemAsync(SaleItem existingItem, int productId, decimal quantity, decimal? customUnitPriceUsd, decimal? customUnitPriceLocal, System.Threading.CancellationToken cancellationToken)
    {
        existingItem.Quantity += quantity;
        var productInfo = (customUnitPriceUsd.HasValue && customUnitPriceLocal.HasValue)
            ? null
            : await _inventoryService!.GetSaleProductByIdAsync(productId, cancellationToken);

        decimal grossPrice = customUnitPriceUsd ?? productInfo?.PriceUSD ?? existingItem.UnitPrice;
        decimal grossPriceBsS = customUnitPriceLocal ?? productInfo?.PriceBsS ?? existingItem.UnitPriceBsS;

        existingItem.UnitPrice = Math.Round(grossPrice, 4);
        existingItem.UnitPriceBsS = Math.Round(grossPriceBsS, 4);
        existingItem.IsCustomPrice = existingItem.IsCustomPrice || customUnitPriceUsd.HasValue || customUnitPriceLocal.HasValue;
    }

    private async Task<SaleItem> BuildNewSaleItemAsync(int saleId, int productId, decimal quantity, decimal? customUnitPriceUsd, decimal? customUnitPriceLocal, System.Threading.CancellationToken cancellationToken)
    {
        var fetchedProduct = await _inventoryService!.GetSaleProductByIdAsync(productId, cancellationToken);
        if (fetchedProduct == null) throw new KeyNotFoundException($"Product {productId} not found.");

        if (fetchedProduct.IsGroupHeader)
        {
            throw new ArgumentException($"El producto '{fetchedProduct.Name}' es un grupo de variantes. Debe seleccionar una variante específica para la venta.");
        }

        decimal grossPrice = customUnitPriceUsd ?? fetchedProduct.PriceUSD;
        decimal grossPriceBsS = customUnitPriceLocal ?? fetchedProduct.PriceBsS;

        return new SaleItem
        {
            SaleId = saleId,
            ProductId = productId,
            ProductName = fetchedProduct.Name,
            UnitPrice = Math.Round(grossPrice, 4),
            UnitPriceBsS = Math.Round(grossPriceBsS, 4),
            Quantity = quantity,
            UnitCostUSD = fetchedProduct.CostPriceUSD,
            IsCustomPrice = customUnitPriceUsd.HasValue || customUnitPriceLocal.HasValue
        };
    }

    public async Task<SaleDto> RemoveItemAsync(int saleId, int itemId, decimal exchangeRate, int? actingUserId = null, System.Threading.CancellationToken cancellationToken = default)
    {
        var sale = await GetSaleEntityAsync(saleId);
        EnsureHoldClaimAccess(sale, actingUserId);
        if (sale.Status != SaleStatus.Pending && sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("No se puede modificar una venta ya finalizada.");

        sale.AppliedRate = await ResolveAnchoredRateAsync(exchangeRate, contextLabel: "RemoveItem", referenceId: sale.Id);

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
            await _context.SaveChangesAsync(cancellationToken);
        }

        return MapToDto(sale);
    }

    public async Task<SaleDto> UpdateItemQuantityAsync(int saleId, int itemId, decimal quantity, decimal exchangeRate, int? actingUserId = null, System.Threading.CancellationToken cancellationToken = default)
    {
        var sale = await GetSaleEntityAsync(saleId);
        EnsureHoldClaimAccess(sale, actingUserId);
        if (sale.Status != SaleStatus.Pending && sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("No se puede modificar una venta ya finalizada.");

        sale.AppliedRate = await ResolveAnchoredRateAsync(exchangeRate, contextLabel: "UpdateItemQuantity", referenceId: sale.Id);

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
            await _context.SaveChangesAsync(cancellationToken);
        }
        return MapToDto(sale);
    }

    public async Task CancelSaleAsync(int saleId, int? actingUserId = null, System.Threading.CancellationToken cancellationToken = default)
    {
        var sale = await GetSaleEntityAsync(saleId);
        EnsureHoldClaimAccess(sale, actingUserId);
        if (sale.Status == SaleStatus.Completed) 
            throw new InvalidOperationException("No se puede anular una venta que ya ha sido completada.");
        if (sale.Status == SaleStatus.Cancelled) 
            throw new InvalidOperationException("La venta ya se encuentra anulada.");
        if (sale.Payments != null && sale.Payments.Any()) 
            throw new InvalidOperationException("No se puede anular un pedido que posee abonos acumulados. Reembolse o reversa los abonos antes de anular.");
        if (sale.DeliveryStatus == SaleDeliveryStatus.Delivered && sale.PickupDate.HasValue) 
            throw new InvalidOperationException("No se puede anular un pedido que ya ha sido entregado al cliente.");

        sale.Status = SaleStatus.Cancelled;
        ClearHoldClaim(sale);
        await _context.SaveChangesAsync(cancellationToken);
        _logger?.LogInformation("Pedido #{SaleId} fue anulado exitosamente.", saleId);
        await NotifyHoldOrdersChangedAsync();
    }

}
