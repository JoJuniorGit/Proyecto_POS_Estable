using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Events;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;
using SalesService = Sales.Module.Services.SalesService;
using ICashDrawerService = Sales.Module.Interfaces.ICashDrawerService;
using CashDrawerStatus = Sales.Module.Entities.CashDrawerStatus;
using CashTransactionType = Sales.Module.Entities.CashTransactionType;
using CashTransactionSource = Sales.Module.Entities.CashTransactionSource;

namespace CommandCenter.Tests.Unit;

public class SalesServiceUnitTests
{
    private (SalesService service, SalesDbContext context, Mock<IInventoryService> inventoryMock, Mock<IMediator> mediatorMock, Mock<ICashDrawerService> cashDrawerMock) CreateService()
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        var inventoryMock = new Mock<IInventoryService>();
        var mediatorMock = new Mock<IMediator>();
        var cashDrawerMock = new Mock<ICashDrawerService>();
        var settingsMock = new Mock<ISystemSettingsService>();

        cashDrawerMock
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSession { Id = 1, Status = CashDrawerStatus.Open });

        var service = new SalesService(context, inventoryMock.Object, mediatorMock.Object, cashDrawerMock.Object, settingsMock.Object);
        return (service, context, inventoryMock, mediatorMock, cashDrawerMock);
    }

    [Fact]
    public async Task StartSaleAsync_AssignsDefaultCustomer_AndPendingStatus()
    {
        var (service, context, _, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var sale = await service.StartSaleAsync(5);

        Assert.NotNull(sale);
        Assert.True(sale.Id > 0);
        Assert.Equal("Pending", sale.Status);
        Assert.Equal("Consumidor Final", sale.CustomerName);
    }

    [Fact]
    public async Task AddItemAsync_CalculatesSubtotalAndTotalsCorrectly()
    {
        var (service, context, inventoryMock, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var product = new ProductBuilder().WithId(10).WithSku("SKU-10").WithName("Harina").WithCostAndMargin(2.00m, 25.00m).Build();
        inventoryMock.Setup(i => i.GetProductByIdAsync(10)).ReturnsAsync(product);

        var sale = await service.StartSaleAsync();
        var updated = await service.AddItemAsync(sale.Id, 10, 3, 50.00m);

        Assert.Single(updated.Items);
        var item = updated.Items.First();
        Assert.Equal(3, item.Quantity);
        Assert.Equal(product.PriceRetailUSD, item.UnitPrice);
        Assert.Equal(Math.Round(3 * product.PriceRetailUSD, 2, MidpointRounding.AwayFromZero), updated.TotalUSD);
        Assert.Equal(Math.Round(updated.TotalUSD * 50.00m, 2, MidpointRounding.AwayFromZero), updated.TotalBsS);
    }

    [Fact]
    public async Task RemoveItemAsync_RecalculatesTotals_ToZeroWhenEmpty()
    {
        var (service, context, inventoryMock, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var product = new ProductBuilder().WithId(11).WithCostAndMargin(10m, 20m).Build();
        inventoryMock.Setup(i => i.GetProductByIdAsync(11)).ReturnsAsync(product);

        var sale = await service.StartSaleAsync();
        var addedSale = await service.AddItemAsync(sale.Id, 11, 2, 50m);
        int itemId = addedSale.Items.First().Id;

        var result = await service.RemoveItemAsync(sale.Id, itemId, 50m);

        Assert.Empty(result.Items);
        Assert.Equal(0m, result.TotalUSD);
        Assert.Equal(0m, result.TotalBsS);
    }

    [Fact]
    public async Task UpdateItemQuantityAsync_UpdatesSubtotalsAndGrandTotals()
    {
        var (service, context, inventoryMock, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var product = new ProductBuilder().WithId(12).WithCostAndMargin(5m, 20m).Build();
        inventoryMock.Setup(i => i.GetProductByIdAsync(12)).ReturnsAsync(product);

        var sale = await service.StartSaleAsync();
        await service.AddItemAsync(sale.Id, 12, 1, 50m);

        var saleEntity = await context.Sales.Include(s => s.Items).FirstAsync(s => s.Id == sale.Id);
        int itemId = saleEntity.Items.First().Id;

        var result = await service.UpdateItemQuantityAsync(sale.Id, itemId, 4, 50m);

        var item = result.Items.First();
        Assert.Equal(4, item.Quantity);
        Assert.Equal(Math.Round(4 * product.PriceRetailUSD, 2, MidpointRounding.AwayFromZero), result.TotalUSD);
    }

    [Fact]
    public async Task UpdatePriceListAsync_SwitchesBetweenRetailAndWholesale()
    {
        var (service, context, inventoryMock, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var product = new ProductBuilder()
            .WithId(13)
            .WithCostAndMargin(10m, 30m)
            .WithWholesale(minQty: 6m, wholesaleMargin: 10m)
            .Build();

        inventoryMock.Setup(i => i.GetProductByIdAsync(13)).ReturnsAsync(product);
        inventoryMock.Setup(i => i.GetProductsByIdsAsync(It.IsAny<IEnumerable<int>>())).ReturnsAsync(new List<Product> { product });

        var sale = await service.StartSaleAsync();
        await service.AddItemAsync(sale.Id, 13, 10, 50m);

        // Cambiar a precio mayorista
        var wholesaleSale = await service.UpdatePriceListAsync(sale.Id, "Wholesale");
        var wholesaleItem = wholesaleSale.Items.First();
        Assert.Equal(product.PriceWholesaleUSD, wholesaleItem.UnitPrice);

        // Cambiar de vuelta a minorista
        var retailSale = await service.UpdatePriceListAsync(sale.Id, "Retail");
        var retailItem = retailSale.Items.First();
        Assert.Equal(product.PriceRetailUSD, retailItem.UnitPrice);
    }

    [Fact]
    public async Task CancelSaleAsync_SetsStatusToCancelled()
    {
        var (service, context, _, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var sale = await service.StartSaleAsync();
        var saleEntity = await context.Sales.FindAsync(sale.Id);
        saleEntity!.DeliveryStatus = SaleDeliveryStatus.PendingPickup;
        await context.SaveChangesAsync();

        await service.CancelSaleAsync(sale.Id);

        var saved = await context.Sales.FindAsync(sale.Id);
        Assert.NotNull(saved);
        Assert.Equal(SaleStatus.Cancelled, saved.Status);
    }

    [Fact]
    public async Task CompleteSaleAsync_WithOverpayment_CompletesSaleAndLogsInfo()
    {
        var (service, context, _, mediatorMock, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var sale = new SaleBuilder().WithId(20).WithAppliedRate(50m).WithItem(1, "Prod", 2, 25m).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        // Total USD = 50. Pago = 60 USD (sobrepago de 10 USD)
        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 60m, 3000m, null)
        };

        int invoiceNum = await service.CompleteSaleAsync(20, 50m, payments);

        var saved = await context.Sales.FindAsync(20);
        Assert.NotNull(saved);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.True(invoiceNum > 0);
        mediatorMock.Verify(m => m.Publish(It.IsAny<SaleMadeEvent>(), default), Times.Once);
    }

    [Fact]
    public async Task CompleteSaleAsync_WithZeroAppliedRate_ThrowsInvalidOperationException()
    {
        var (service, context, _, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var sale = new SaleBuilder().WithId(21).WithItem(1, "Prod", 1, 10m).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo> { new PaymentInfo(1, 10m, 500m, null) };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteSaleAsync(21, 0m, payments));
        Assert.Contains("Tasa de cambio AppliedRate inválida", ex.Message);
    }

    [Fact]
    public async Task HoldSaleAsync_WithIdentifiedCustomer_SetsOnHold()
    {
        var (service, context, _, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var customer = new CustomerBuilder().WithId(100).WithCedula("V-99999999").WithName("Pedro Perez").Build();
        context.Customers.Add(customer);

        var sale = new SaleBuilder().WithId(30).WithAppliedRate(50m).WithItem(1, "Item", 2, 20m).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var request = new HoldSaleRequestDto
        {
            CustomerId = customer.Id,
            ExchangeRate = 50m
        };

        var result = await service.HoldSaleAsync(30, request);

        Assert.Equal("OnHold", result.Status);
        Assert.Equal(customer.Name, result.CustomerName);
    }

    [Fact]
    public async Task HoldSaleAsync_WithDefaultCustomer_ThrowsInvalidOperationException()
    {
        var (service, context, _, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var sale = new SaleBuilder().WithId(31).WithItem(1, "Item", 1, 10m).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var request = new HoldSaleRequestDto
        {
            CustomerId = 1, // Consumidor Final
            ExchangeRate = 50m
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.HoldSaleAsync(31, request));
        Assert.Contains("Las ventas en espera requieren un cliente real identificable", ex.Message);
    }

    [Fact]
    public async Task RecalculateOnHoldSalesAsync_UpdatesPricesWithNewRate_PreservingPayments()
    {
        var (service, context, inventoryMock, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var product = new ProductBuilder().WithId(50).WithCostAndMargin(10m, 50m).Build();
        inventoryMock.Setup(i => i.GetProductsByIdsAsync(It.IsAny<IEnumerable<int>>())).ReturnsAsync(new List<Product> { product });

        var sale = new SaleBuilder()
            .WithId(40)
            .WithStatus(SaleStatus.OnHold)
            .WithAppliedRate(40m)
            .WithItem(50, "Prod 50", 1, 15m)
            .WithPayment(1, 5m, 200m)
            .Build();

        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        int updatedCount = await service.RecalculateOnHoldSalesAsync(60m);

        Assert.Equal(1, updatedCount);
        var updated = await context.Sales.Include(s => s.Payments).FirstAsync(s => s.Id == 40);
        Assert.Equal(60m, updated.AppliedRate);
        Assert.Single(updated.Payments);
        Assert.Equal(5m, updated.Payments[0].Amount); // Pagos previos intactos
    }

    [Fact]
    public async Task ConfirmPickupAsync_SetsDeliveredStatus_AndPickupDate()
    {
        var (service, context, _, mediatorMock, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var customer = new CustomerBuilder().WithId(200).WithName("Lucia").Build();
        context.Customers.Add(customer);

        var sale = new SaleBuilder()
            .WithId(50)
            .WithCustomer(200, "Lucia", "V-200")
            .WithStatus(SaleStatus.Completed)
            .WithDeliveryStatus(SaleDeliveryStatus.PendingPickup)
            .WithItem(1, "Custodia Prod", 1, 10m)
            .Build();

        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var detail = await service.ConfirmPickupAsync(50);

        Assert.Equal("Delivered", detail.DeliveryStatus);
        Assert.NotNull(detail.PickupDate);
        mediatorMock.Verify(m => m.Publish(It.IsAny<SaleMadeEvent>(), default), Times.Never);
    }

    [Fact]
    public async Task CompleteSale_WithCashAdvanceProduct_DoesNotDeductStockOrThrow()
    {
        var (service, context, inventoryMock, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var advProduct = new Product
        {
            Id = 99,
            SKU = "ADV-001",
            Name = "Adelanto de Efectivo",
            IsCashAdvance = true,
            StockQuantity = 0m,
            IsActive = true
        };

        inventoryMock.Setup(i => i.GetProductByIdAsync(99)).ReturnsAsync(advProduct);
        inventoryMock.Setup(i => i.GetTodayExchangeRateAsync()).ReturnsAsync(50m);

        var sale = await service.StartSaleAsync();
        var itemAdded = await service.AddItemAsync(sale.Id, 99, 1, 50m, custom_unit_price_usd: 10m, custom_unit_price_local: 500m);
        Assert.NotNull(itemAdded);

        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 10m, 500m, null)
        };

        int invoiceNum = await service.CompleteSaleAsync(sale.Id, 50m, payments);
        Assert.True(invoiceNum > 0);

        var completed = await context.Sales.FindAsync(sale.Id);
        Assert.NotNull(completed);
        Assert.Equal(SaleStatus.Completed, completed.Status);

        // Verify that stock is not deducted for cash advance products
        inventoryMock.Verify(i => i.UpdateStockAsync(99, It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
