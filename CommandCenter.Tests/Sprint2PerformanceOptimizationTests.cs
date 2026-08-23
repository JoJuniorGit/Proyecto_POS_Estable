using Core.Constants;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests;

public class Sprint2PerformanceOptimizationTests
{
    private SalesDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    [Fact]
    public async Task PaymentMethodService_CachesActiveMethods_AndInvalidatesOnMutation()
    {
        using var context = GetInMemoryDbContext();
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var service = new PaymentMethodService(context, memoryCache);

        // 1. Add methods to DB directly
        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo USD", IsActive = true, DisplayOrder = 1, IsCash = true },
            new PaymentMethod { Id = 2, Name = "Punto de Venta", IsActive = true, DisplayOrder = 2, IsCash = false }
        );
        await context.SaveChangesAsync();

        // 2. First call populates cache
        var active1 = (await service.GetActiveMethodsAsync()).ToList();
        Assert.Equal(2, active1.Count);
        Assert.True(memoryCache.TryGetValue(CacheKeys.ActivePaymentMethods, out _));

        // 3. Modify DB directly without service (to test cache serves old data)
        context.PaymentMethods.Add(new PaymentMethod { Id = 3, Name = "Pago Movil", IsActive = true, DisplayOrder = 3 });
        await context.SaveChangesAsync();

        var activeCached = (await service.GetActiveMethodsAsync()).ToList();
        Assert.Equal(2, activeCached.Count); // Should still return cached 2 items

        // 4. Update via service -> must invalidate cache
        await service.UpdateAsync(new PaymentMethod { Id = 1, Name = "Efectivo Dolares", IsActive = true, DisplayOrder = 1, IsCash = true });
        Assert.False(memoryCache.TryGetValue(CacheKeys.ActivePaymentMethods, out _));

        // 5. Subsequent call returns refreshed data including the 3rd item
        var activeRefreshed = (await service.GetActiveMethodsAsync()).ToList();
        Assert.Equal(3, activeRefreshed.Count);

        // 6. Delete via service -> must invalidate cache
        await service.DeleteAsync(3);
        Assert.False(memoryCache.TryGetValue(CacheKeys.ActivePaymentMethods, out _));

        var activeAfterDelete = (await service.GetActiveMethodsAsync()).ToList();
        Assert.Equal(2, activeAfterDelete.Count);
    }

    [Fact]
    public async Task CompleteSaleAsync_WithMultiplePaymentMethods_ExecutesBatchLookupSuccessfully()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<MediatR.IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockCashDrawer
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSession { Id = 1, Status = CashDrawerStatus.Open });

        var salesService = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        // Seed customer & multiple payment methods
        context.Customers.Add(new Customer { Id = 1, Name = "Consumidor Final", CedulaOrRif = "V-00000000", IsDefault = true, IsActive = true });
        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo USD", IsActive = true, IsCash = true },
            new PaymentMethod { Id = 2, Name = "Pago Movil", IsActive = true, IsCash = false },
            new PaymentMethod { Id = 3, Name = "Zelle", IsActive = true, IsCash = false }
        );

        var sale = new Sale
        {
            Status = SaleStatus.Pending,
            Date = DateTime.UtcNow,
            CustomerId = 1,
            CustomerName = "Consumidor Final",
            CustomerCedula = "V-00000000",
            AppliedRate = 50m,
            TotalUSD = 100m,
            Subtotal = 100m,
            TotalBsS = 5000m,
            SubtotalBsS = 5000m,
            Items = new List<SaleItem>
            {
                new SaleItem { ProductId = 10, ProductName = "Articulo Test", Quantity = 1m, UnitPrice = 100m, Subtotal = 100m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 20m, 1000m, null),
            new PaymentInfo(2, 30m, 1500m, "REF-1234"),
            new PaymentInfo(3, 50m, 2500m, "ZELLE-5678")
        };

        int invoiceNumber = await salesService.CompleteSaleAsync(sale.Id, 50m, payments, 0m);

        Assert.True(invoiceNumber > 0);
        var completedSale = await context.Sales.Include(s => s.Payments).FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, completedSale.Status);
        Assert.Equal(3, completedSale.Payments.Count);
        Assert.Equal(100m, completedSale.Payments.Sum(p => p.Amount));
    }

    [Fact]
    public async Task GetPendingSalesAsync_ReturnsAllOnHoldSalesAsNoTracking()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<MediatR.IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockInventory.Setup(i => i.GetTodayExchangeRateAsync()).ReturnsAsync(50m);

        var salesService = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        context.Customers.Add(new Customer { Id = 10, Name = "Cliente Prueba", CedulaOrRif = "V-99999999", IsActive = true });
        context.Sales.AddRange(
            new Sale
            {
                Id = 101,
                Status = SaleStatus.OnHold,
                CustomerId = 10,
                CustomerName = "Cliente Prueba",
                CustomerCedula = "V-99999999",
                AppliedRate = 50m,
                TotalUSD = 15m,
                Subtotal = 15m,
                TotalBsS = 750m,
                SubtotalBsS = 750m,
                Date = DateTime.UtcNow
            },
            new Sale
            {
                Id = 102,
                Status = SaleStatus.Completed,
                CustomerId = 10,
                CustomerName = "Cliente Prueba",
                CustomerCedula = "V-99999999",
                AppliedRate = 50m,
                TotalUSD = 20m,
                Subtotal = 20m,
                TotalBsS = 1000m,
                SubtotalBsS = 1000m,
                Date = DateTime.UtcNow
            }
        );
        await context.SaveChangesAsync();

        var pendingList = (await salesService.GetPendingSalesAsync()).ToList();

        Assert.Single(pendingList);
        Assert.Equal(101, pendingList[0].Id);
        Assert.Equal("OnHold", pendingList[0].Status);
    }
}
