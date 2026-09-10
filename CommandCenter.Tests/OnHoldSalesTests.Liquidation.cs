using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using System;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests;

public partial class OnHoldSalesTests
{
    [Fact]
    public async Task LiquidateOnHoldSale_WithPendingPickup_RejectsDefaultCustomer()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var defaultCustomer = new Customer { Id = 1, CedulaOrRif = "V-00000000", Name = "Consumidor Final", IsDefault = true };
        context.Customers.Add(defaultCustomer);

        var sale = new Sale { Id = 1, TotalUSD = 100m, AppliedRate = 40m, Status = SaleStatus.OnHold, CustomerId = 1 };
        sale.Payments.Add(new SalePayment { Id = 1, Amount = 100m, AmountBsS = 4000m, ExchangeRate = 40m });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteSaleAsync(1, 40m, System.Linq.Enumerable.Empty<PaymentInfo>(), 0m, 1, isPendingPickup: true));
        Assert.Contains("se requiere seleccionar o crear un cliente real", ex.Message);
    }

    [Fact]
    public async Task RecalculateOnHoldSalesAsync_UpdatesAppliedRateAndBsSTotals_ForOnHoldSalesOnly()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var onHoldSale = new Sale
        {
            Id = 1,
            Status = SaleStatus.OnHold,
            AppliedRate = 50m,
            TotalUSD = 100m,
            TotalBsS = 5000m,
            Items = new System.Collections.Generic.List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 1, ProductName = "Product A", Quantity = 2m, UnitPrice = 50m, Subtotal = 100m, UnitPriceBsS = 2500m, SubtotalBsS = 5000m }
            }
        };

        var completedSale = new Sale
        {
            Id = 2,
            Status = SaleStatus.Completed,
            AppliedRate = 50m,
            TotalUSD = 100m,
            TotalBsS = 5000m
        };

        context.Sales.AddRange(onHoldSale, completedSale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        // Recalculate with new rate = 60m
        int count = await service.RecalculateOnHoldSalesAsync(60m);

        Assert.Equal(1, count);

        var updatedOnHold = await context.Sales.Include(s => s.Items).FirstOrDefaultAsync(s => s.Id == 1);
        Assert.NotNull(updatedOnHold);
        Assert.Equal(60m, updatedOnHold.AppliedRate);
        Assert.Equal(6000m, updatedOnHold.TotalBsS);
        Assert.Equal(3000m, updatedOnHold.Items[0].UnitPriceBsS);
        Assert.Equal(6000m, updatedOnHold.Items[0].SubtotalBsS);

        var updatedCompleted = await context.Sales.FindAsync(2);
        Assert.NotNull(updatedCompleted);
        Assert.Equal(50m, updatedCompleted.AppliedRate);
        Assert.Equal(5000m, updatedCompleted.TotalBsS);
    }

    [Fact]
    public async Task RecalculateOnHoldSalesAsync_PreservesExistingPayments()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var onHoldSale = new Sale
        {
            Id = 1,
            Status = SaleStatus.OnHold,
            AppliedRate = 50m,
            TotalUSD = 100m,
            TotalBsS = 5000m,
            Payments = new System.Collections.Generic.List<SalePayment>
            {
                new SalePayment { Id = 1, Amount = 20m, AmountBsS = 1000m, ExchangeRate = 50m }
            }
        };

        context.Sales.Add(onHoldSale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        await service.RecalculateOnHoldSalesAsync(60m);

        var updatedSale = await context.Sales.Include(s => s.Payments).FirstOrDefaultAsync(s => s.Id == 1);
        Assert.NotNull(updatedSale);
        Assert.Equal(60m, updatedSale.AppliedRate);
        Assert.Equal(6000m, updatedSale.TotalBsS);

        // Previous payment retains its original rate and amounts
        var payment = updatedSale.Payments[0];
        Assert.Equal(20m, payment.Amount);
        Assert.Equal(1000m, payment.AmountBsS);
        Assert.Equal(50m, payment.ExchangeRate);
    }

    [Fact]
    public async Task RecalculateOnHoldSalesAsync_Keyset_ProcessesAllBatches_WithoutSkipOrDuplicate()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        // 450 ventas OnHold: cruza 3 lotes de 200 sin dejar ninguna sin recalcular
        // (8.16-H14: keyset WHERE Id > lastId, no Skip/Take).
        var sales = System.Linq.Enumerable.Range(1, 450).Select(i => new Sale
        {
            Id = i,
            Status = SaleStatus.OnHold,
            AppliedRate = 50m,
            TotalUSD = 100m,
            TotalBsS = 5000m
        }).ToList();

        context.Sales.AddRange(sales);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        int count = await service.RecalculateOnHoldSalesAsync(60m);

        Assert.Equal(450, count);
        var updated = await context.Sales.Where(s => s.Status == SaleStatus.OnHold).ToListAsync();
        Assert.Equal(450, updated.Count);
        Assert.All(updated, s => Assert.Equal(60m, s.AppliedRate));
        Assert.All(updated, s => Assert.Equal(6000m, s.TotalBsS));
    }

    [Fact]
    public async Task GetPendingSalesAsync_IsReadOnly_RecalcHappensAtRateUpsert()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockInventory.Setup(i => i.GetTodayExchangeRateAsync()).ReturnsAsync(65m);

        var oldOnHoldSale = new Sale
        {
            Id = 1,
            Status = SaleStatus.OnHold,
            AppliedRate = 50m,
            TotalUSD = 100m,
            TotalBsS = 5000m
        };

        context.Sales.Add(oldOnHoldSale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        // 8.7-B6: GET no escribe. El recálculo masivo ocurre SOLO en el upsert de tasa
        // (RecalculateOnHoldSalesAsync), no al leer.
        await service.RecalculateOnHoldSalesAsync(65m);

        var pendingSales = (await service.GetPendingSalesAsync()).ToList();

        Assert.Single(pendingSales);
        Assert.Equal(65m, pendingSales[0].AppliedRate);
        Assert.Equal(6500m, pendingSales[0].TotalBsS);

        // Escribir una tasa divergente y NADA de re-calcular: GET debe devolver lo persistido
        // sin mutar la BD (sin write-on-read).
        var tracked = await context.Sales.FirstAsync(s => s.Id == 1);
        tracked.AppliedRate = 99m;
        await context.SaveChangesAsync();

        var before = await context.Sales.AsNoTracking().Select(s => s.AppliedRate).FirstAsync();
        var readBack = (await service.GetPendingSalesAsync()).ToList();
        var after = await context.Sales.AsNoTracking().Select(s => s.AppliedRate).FirstAsync();

        Assert.Equal(before, readBack[0].AppliedRate);
        Assert.Equal(after, before); // GET no persiste cambios
    }

    [Fact]
    public async Task AddItemAsync_TruncatesDecimalQuantityForNonFractionalProducts()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockInventory.Setup(i => i.GetProductByIdAsync(10)).ReturnsAsync(new Product
        {
            Id = 10,
            Name = "Acondicionador Drene Brillo 200ml",
            PriceUSD = 15.69m / 60m,
            IsFractional = false
        });

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var updatedSale = await service.AddItemAsync(1, 10, 1.5m, 60m);

        Assert.NotNull(updatedSale);
        Assert.Single(updatedSale.Items);
        Assert.Equal(1m, updatedSale.Items[0].Quantity);
    }

    [Fact]
    public async Task AddItemAsync_WithDeletedProduct_ThrowsInvalidOperationException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockInventory.Setup(i => i.GetProductByIdAsync(20)).ReturnsAsync(new Product
        {
            Id = 20,
            Name = "Producto Eliminado",
            PriceUSD = 5.00m,
            IsActive = true,
            IsDeleted = true
        });

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddItemAsync(1, 20, 1m, 60m));
        Assert.Contains("no está disponible", ex.Message);
    }

    [Fact]
    public async Task AddItemAsync_WithInactiveProduct_ThrowsInvalidOperationException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockInventory.Setup(i => i.GetProductByIdAsync(21)).ReturnsAsync(new Product
        {
            Id = 21,
            Name = "Producto Inactivo",
            PriceUSD = 5.00m,
            IsActive = false
        });

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddItemAsync(1, 21, 1m, 60m));
        Assert.Contains("no está disponible", ex.Message);
    }

    [Fact]
    public async Task HoldSaleAsync_SmallAmountFractionalProduct_StaysOnHoldWhenUnpaid()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var customer = new Customer { Id = 5, Name = "Carlos Sanchez", CedulaOrRif = "V-20111222" };
        context.Customers.Add(customer);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.Pending,
            TotalUSD = 0.03m,
            AppliedRate = 784.67m,
            TotalBsS = 20.87m
        };
        sale.Items.Add(new SaleItem
        {
            Id = 1,
            ProductId = 10,
            ProductName = "Acondicionador Drene Brillo 200ml",
            Quantity = 1.33m,
            UnitPrice = 0.026m,
            Subtotal = 0.03m,
            UnitPriceBsS = 15.69m,
            SubtotalBsS = 20.87m
        });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var heldSale = await service.HoldSaleAsync(1, new HoldSaleRequestDto
        {
            CustomerId = 5,
            ExchangeRate = 784.67m
        });

        Assert.Equal("OnHold", heldSale.Status);
        Assert.Null(heldSale.InvoiceNumber);

        var pendingSales = (await service.GetPendingSalesAsync()).ToList();
        Assert.Single(pendingSales);
        Assert.Equal(1, pendingSales[0].Id);
        Assert.Single(pendingSales[0].Items);
        Assert.Equal(1.33m, pendingSales[0].Items[0].Quantity);
    }

    [Fact]
    public async Task CancelSaleAsync_WithoutPayments_ChangesStatusToCancelled()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale { Id = 1, TotalUSD = 50m, Status = SaleStatus.OnHold, DeliveryStatus = SaleDeliveryStatus.PendingPickup };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        await service.CancelSaleAsync(1);

        var updatedSale = await context.Sales.FindAsync(1);
        Assert.NotNull(updatedSale);
        Assert.Equal(SaleStatus.Cancelled, updatedSale.Status);
    }

    [Fact]
    public async Task CancelSaleAsync_WithPayments_ThrowsInvalidOperationException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var paymentMethod = new PaymentMethod { Id = 1, Name = "Efectivo USD", IsActive = true };
        context.PaymentMethods.Add(paymentMethod);

        var sale = new Sale { Id = 1, TotalUSD = 50m, Status = SaleStatus.OnHold, DeliveryStatus = SaleDeliveryStatus.PendingPickup };
        sale.Payments.Add(new SalePayment { Id = 1, SaleId = 1, PaymentMethodId = 1, Amount = 10m, AmountBsS = 7800m });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelSaleAsync(1));
        Assert.Contains("abonos acumulados", ex.Message);
    }

    [Fact]
    public async Task CancelSaleAsync_DeliveredOrder_ThrowsInvalidOperationException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale { Id = 1, TotalUSD = 50m, Status = SaleStatus.OnHold, DeliveryStatus = SaleDeliveryStatus.Delivered, PickupDate = DateTime.UtcNow };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelSaleAsync(1));
        Assert.Contains("entregado al cliente", ex.Message);
    }

    [Fact]
    public async Task GetPendingSalesAsync_ExcludesCancelledSales()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var saleOnHold = new Sale { Id = 1, TotalUSD = 50m, Status = SaleStatus.OnHold };
        var saleCancelled = new Sale { Id = 2, TotalUSD = 30m, Status = SaleStatus.Cancelled };
        context.Sales.AddRange(saleOnHold, saleCancelled);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var pendingSales = (await service.GetPendingSalesAsync()).ToList();

        Assert.Single(pendingSales);
        Assert.Equal(1, pendingSales[0].Id);
    }
}


