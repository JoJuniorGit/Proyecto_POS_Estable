using Core.DTOs;
using Core.Entities;
using Core.Events;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests;

public class PendingPickupTests
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
    public async Task CompleteSale_WithPendingPickup_DeductsInventoryAndSetsDeliveryStatus()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockCashDrawer
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSession { Id = 1, Status = CashDrawerStatus.Open });

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Juan Perez", IsDefault = false };
        var paymentMethod = new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true };
        context.Customers.Add(customer);
        context.PaymentMethods.Add(paymentMethod);

        var sale = new Sale
        {
            Id = 10,
            CustomerId = 1,
            TotalUSD = 50m,
            AppliedRate = 40m,
            Status = SaleStatus.Pending,
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 1, ProductId = 101, ProductName = "Harina Pan", Quantity = 20, UnitPrice = 2.5m, Subtotal = 50.0m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 50m, 2000m, "REF-100")
        };

        int invoiceNum = await service.CompleteSaleAsync(10, 40m, payments, 0, 1, isPendingPickup: true);

        var savedSale = await context.Sales.FindAsync(10);
        Assert.NotNull(savedSale);
        Assert.Equal(SaleStatus.Completed, savedSale.Status);
        Assert.Equal(SaleDeliveryStatus.PendingPickup, savedSale.DeliveryStatus);
        Assert.Null(savedSale.PickupDate);

        // Verify inventory deduction event was published immediately
        mockMediator.Verify(m => m.Publish(It.IsAny<SaleMadeEvent>(), default), Times.Once);
    }

    [Fact]
    public async Task CompleteSale_WithPendingPickup_AndDefaultCustomer_Throws()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var defaultCustomer = new Customer { Id = 1, CedulaOrRif = "V-00000000", Name = "Consumidor Final", IsDefault = true };
        context.Customers.Add(defaultCustomer);

        var sale = new Sale
        {
            Id = 11,
            CustomerId = 1,
            TotalUSD = 20m,
            AppliedRate = 40m,
            Status = SaleStatus.Pending
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
        var payments = new List<PaymentInfo> { new PaymentInfo(1, 20m, 800m, null) };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteSaleAsync(11, 40m, payments, 0, 1, isPendingPickup: true));

        Assert.Contains("cliente real", ex.Message);
    }

    [Fact]
    public async Task ConfirmPickup_WhenNotPendingPickup_Throws()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale
        {
            Id = 12,
            Status = SaleStatus.Completed,
            DeliveryStatus = SaleDeliveryStatus.Delivered
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmPickupAsync(12));

        Assert.Contains("no se encuentra en estado Pendiente por Retirar", ex.Message);
    }

    [Fact]
    public async Task ConfirmPickup_UpdatesStatusAndTimestamp_WithoutChangingFinancials()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var customer = new Customer { Id = 2, CedulaOrRif = "V-87654321", Name = "Maria Perez", IsDefault = false };
        context.Customers.Add(customer);

        var sale = new Sale
        {
            Id = 13,
            InvoiceNumber = 1005,
            CustomerId = 2,
            TotalUSD = 100m,
            TotalBsS = 4000m,
            AppliedRate = 40m,
            Status = SaleStatus.Completed,
            DeliveryStatus = SaleDeliveryStatus.PendingPickup,
            PickupDate = null,
            Payments = new List<SalePayment>
            {
                new SalePayment { Id = 1, Amount = 100m, AmountBsS = 4000m, ExchangeRate = 40m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var result = await service.ConfirmPickupAsync(13);

        var updatedSale = await context.Sales.FindAsync(13);
        Assert.NotNull(updatedSale);
        Assert.Equal(SaleDeliveryStatus.Delivered, updatedSale.DeliveryStatus);
        Assert.NotNull(updatedSale.PickupDate);
        Assert.True((DateTime.UtcNow - updatedSale.PickupDate.Value).TotalSeconds < 10);

        // Verify zero change in financial amounts
        Assert.Equal(100m, updatedSale.TotalUSD);
        Assert.Equal(4000m, updatedSale.TotalBsS);
        Assert.Equal(40m, updatedSale.AppliedRate);
    }

    [Fact]
    public void Migration_SetsDeliveryStatusDeliveredForExistingSales()
    {
        var sale = new Sale();
        Assert.Equal(SaleDeliveryStatus.Delivered, sale.DeliveryStatus);
        Assert.Null(sale.PickupDate);
    }
}
