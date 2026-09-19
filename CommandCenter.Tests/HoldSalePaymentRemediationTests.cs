using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
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

namespace CommandCenter.Tests;

public class HoldSalePaymentRemediationTests
{
    private const int TestActorId = 42;

    private static SalesDbContext NewInMemoryDb(string name) =>
        new(new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static Mock<ICashDrawerService> CreateMockCashDrawer()
    {
        var mock = new Mock<ICashDrawerService>();
        mock.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });
        return mock;
    }

    private static SalesService CreateService(SalesDbContext context, IInventoryService? inventoryService = null, ICashDrawerService? cashDrawer = null)
    {
        return new SalesService(
            context,
            inventoryService ?? new Mock<IInventoryService>().Object,
            new Mock<IMediator>().Object,
            cashDrawer ?? CreateMockCashDrawer().Object,
            new Mock<ISystemSettingsService>().Object);
    }

    private static Sale BuildClaimedOnHoldSale(int id, decimal totalUsd, decimal appliedRate)
    {
        return new Sale
        {
            Id = id,
            Status = SaleStatus.OnHold,
            TotalUSD = totalUsd,
            AppliedRate = appliedRate,
            ClaimedByUserId = TestActorId,
            ClaimAction = SaleClaimAction.Editing,
            ClaimedByUserName = "Test Actor",
            ClaimedAtUtc = DateTime.UtcNow
        };
    }

    private static SaleProductInfoDto BuildWholesaleProduct() => new()
    {
        Id = 500,
        Name = "Mayorista",
        PriceUSD = 10m,
        PriceRetailUSD = 10m,
        PriceWholesaleUSD = 8m,
        HasWholesale = true,
        MinWholesaleQuantity = 6m,
        IsActive = true
    };

    [Fact]
    public async Task UpdateSaleItemsAsync_WholesaleList_AcceptsEffectiveWholesalePrice()
    {
        using var context = NewInMemoryDb(Guid.NewGuid().ToString());
        var inventory = new Mock<IInventoryService>();
        var product = BuildWholesaleProduct();
        inventory.Setup(i => i.GetSaleProductsByIdsAsync(It.IsAny<IEnumerable<int>>())).ReturnsAsync(new List<SaleProductInfoDto> { product });
        inventory.Setup(i => i.GetSaleProductByIdAsync(500)).ReturnsAsync(product);

        var sale = BuildClaimedOnHoldSale(1, 10m, 50m);
        sale.PriceListType = "Wholesale";
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context, inventory.Object);
        var result = await service.UpdateSaleItemsAsync(1, new UpdateSaleItemsRequestDto
        {
            Items = new List<UpdateSaleItemDto> { new() { ProductId = 500, Quantity = 6m, UnitPrice = 8m } }
        }, actingUserId: TestActorId);

        Assert.Equal(48m, result.TotalUSD);
        var item = Assert.Single(result.Items);
        Assert.Equal(8m, item.UnitPrice);
        Assert.True(item.IsWholesaleApplied);
        Assert.False(item.IsCustomPrice);
    }

    [Fact]
    public async Task UpdateSaleItemsAsync_PriceDifferentFromEffectiveWholesale_ThrowsUnauthorizedAccessException()
    {
        using var context = NewInMemoryDb(Guid.NewGuid().ToString());
        var inventory = new Mock<IInventoryService>();
        var product = BuildWholesaleProduct();
        inventory.Setup(i => i.GetSaleProductsByIdsAsync(It.IsAny<IEnumerable<int>>())).ReturnsAsync(new List<SaleProductInfoDto> { product });
        inventory.Setup(i => i.GetSaleProductByIdAsync(500)).ReturnsAsync(product);

        var sale = BuildClaimedOnHoldSale(1, 10m, 50m);
        sale.PriceListType = "Wholesale";
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context, inventory.Object);
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateSaleItemsAsync(1, new UpdateSaleItemsRequestDto
        {
            Items = new List<UpdateSaleItemDto> { new() { ProductId = 500, Quantity = 6m, UnitPrice = 10m } }
        }, actingUserId: TestActorId));

        Assert.Contains("Modificación de precio no autorizada", ex.Message);
    }

    [Fact]
    public async Task UpdateSaleItemsAsync_PriceDifferentFromEffectiveWholesale_WithOverride_AcceptsCustomPrice()
    {
        using var context = NewInMemoryDb(Guid.NewGuid().ToString());
        var inventory = new Mock<IInventoryService>();
        var product = BuildWholesaleProduct();
        inventory.Setup(i => i.GetSaleProductsByIdsAsync(It.IsAny<IEnumerable<int>>())).ReturnsAsync(new List<SaleProductInfoDto> { product });
        inventory.Setup(i => i.GetSaleProductByIdAsync(500)).ReturnsAsync(product);

        var sale = BuildClaimedOnHoldSale(1, 10m, 50m);
        sale.PriceListType = "Wholesale";
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context, inventory.Object);
        var result = await service.UpdateSaleItemsAsync(1, new UpdateSaleItemsRequestDto
        {
            Items = new List<UpdateSaleItemDto> { new() { ProductId = 500, Quantity = 6m, UnitPrice = 10m } }
        }, isPriceOverrideAuthorized: true, actingUserId: TestActorId);

        Assert.Equal(60m, result.TotalUSD);
        var item = Assert.Single(result.Items);
        Assert.Equal(10m, item.UnitPrice);
        Assert.True(item.IsCustomPrice);
    }

    [Fact]
    public async Task UpdateSaleItemsAsync_WhenRecalculationDropsTotalBelowPaid_ThrowsAndKeepsPersistedSale()
    {
        var db = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using var context = db.context;
        using var connection = db.connection;

        var inventory = new Mock<IInventoryService>();
        var productBefore = new SaleProductInfoDto { Id = 600, Name = "Prod600", PriceUSD = 100m, PriceRetailUSD = 100m, IsActive = true };
        var productAfter = new SaleProductInfoDto { Id = 600, Name = "Prod600", PriceUSD = 10m, PriceRetailUSD = 10m, IsActive = true };
        inventory.SetupSequence(i => i.GetSaleProductsByIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new List<SaleProductInfoDto> { productBefore })
            .ReturnsAsync(new List<SaleProductInfoDto> { productAfter });

        var sale = BuildClaimedOnHoldSale(1, 100m, 40m);
        sale.Payments.Add(new SalePayment { Id = 1, Amount = 50m, AmountBsS = 2000m, ExchangeRate = 40m, PaymentMethodId = 1 });
        sale.Items.Add(new SaleItem { Id = 1, ProductId = 2, ProductName = "Original Item", Quantity = 1m, UnitPrice = 100m, UnitPriceBsS = 4000m, Subtotal = 100m, SubtotalBsS = 4000m });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context, inventory.Object);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateSaleItemsAsync(1, new UpdateSaleItemsRequestDto
        {
            Items = new List<UpdateSaleItemDto> { new() { ProductId = 600, Quantity = 1m } }
        }, actingUserId: TestActorId));

        Assert.Contains("no puede ser menor al monto que ya ha sido abonado", ex.Message);

        context.ChangeTracker.Clear();
        var persisted = await context.Sales
            .AsNoTracking()
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .FirstAsync(s => s.Id == 1);

        Assert.Equal(100m, persisted.TotalUSD);
        var item = Assert.Single(persisted.Items);
        Assert.Equal("Original Item", item.ProductName);
        Assert.Equal(50m, persisted.Payments.Sum(p => p.Amount));
    }

    [Fact]
    public async Task AddPaymentToHoldSaleAsync_InconsistentUsdAndBsS_ThrowsArgumentException()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = NewInMemoryDb(dbName);
        var sale = BuildClaimedOnHoldSale(1, 100m, 50m);
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.AddPaymentToHoldSaleAsync(1, new AddPaymentRequestDto
        {
            PaymentMethodId = 1,
            AmountUSD = 100m,
            AmountBsS = 1m,
            ExchangeRate = 50m
        }, actingUserId: TestActorId));

        Assert.Contains("no son consistentes con la tasa aplicada", ex.Message);

        using var fresh = NewInMemoryDb(dbName);
        Assert.False(await fresh.SalePayments.AnyAsync(p => p.SaleId == 1));
    }

    [Fact]
    public async Task AddPaymentToHoldSaleAsync_ConsistentUsdAndBsS_IsAccepted()
    {
        using var context = NewInMemoryDb(Guid.NewGuid().ToString());
        var sale = BuildClaimedOnHoldSale(1, 100m, 36.56m);
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var result = await service.AddPaymentToHoldSaleAsync(1, new AddPaymentRequestDto
        {
            PaymentMethodId = 1,
            AmountUSD = 10m,
            AmountBsS = 365.60m,
            ExchangeRate = 36.56m
        }, actingUserId: TestActorId);

        Assert.Equal(10m, result.TotalPaidUSD);
        var payment = Assert.Single(result.Payments);
        Assert.Equal(365.60m, payment.AmountBsS);
    }

    [Fact]
    public async Task CompleteSaleAsync_InconsistentUsdAndBsS_ThrowsArgumentException()
    {
        using var context = NewInMemoryDb(Guid.NewGuid().ToString());
        var sale = new Sale { Id = 1, Status = SaleStatus.Pending, TotalUSD = 100m, TotalBsS = 5000m, AppliedRate = 50m };
        context.Sales.Add(sale);
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Punto de Venta", IsCash = false, IsActive = true });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var payments = new List<PaymentInfo> { new(1, 100m, 1m, null) };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.CompleteSaleAsync(1, 50m, payments));
        Assert.Contains("no son consistentes con la tasa aplicada", ex.Message);
    }

    [Fact]
    public async Task HoldSaleAsync_InconsistentInitialPayment_ThrowsArgumentException()
    {
        using var context = NewInMemoryDb(Guid.NewGuid().ToString());
        context.Customers.Add(new Customer { Id = 5, Name = "Maria Perez", CedulaOrRif = "V-11223344", IsDefault = false });
        var sale = new Sale { Id = 1, Status = SaleStatus.Pending, TotalUSD = 100m, TotalBsS = 5000m, AppliedRate = 50m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.HoldSaleAsync(1, new HoldSaleRequestDto
        {
            CustomerId = 5,
            ExchangeRate = 50m,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = 1,
                AmountUSD = 100m,
                AmountBsS = 1m,
                ExchangeRate = 50m
            }
        }));

        Assert.Contains("no son consistentes con la tasa aplicada", ex.Message);
    }

    [Fact]
    public async Task AddPaymentToHoldSaleAsync_UsdOnly_PersistsDerivedBsSInPaymentAndCashTransaction()
    {
        using var context = NewInMemoryDb(Guid.NewGuid().ToString());
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true, IsActive = true });
        var sale = BuildClaimedOnHoldSale(1, 100m, 50m);
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context, cashDrawer: new CashDrawerService(context));
        await service.AddPaymentToHoldSaleAsync(1, new AddPaymentRequestDto
        {
            PaymentMethodId = 1,
            AmountUSD = 50m,
            ExchangeRate = 50m
        }, actingUserId: TestActorId);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == 1);
        Assert.Equal(50m, payment.Amount);
        Assert.Equal(2500m, payment.AmountBsS);

        var cashTx = await context.CashTransactions.AsNoTracking().SingleAsync(t => t.SaleId == 1);
        Assert.Equal(50m, cashTx.AmountUsd);
        Assert.Equal(2500m, cashTx.AmountLocal);
        Assert.True(cashTx.IsPhysicalCash);
        Assert.Equal(CashTransactionType.Income, cashTx.Type);
    }

    [Fact]
    public async Task HoldSaleAsync_OverpaidInitialPayment_CompletesAndRegistersChangeExpense()
    {
        using var context = NewInMemoryDb(Guid.NewGuid().ToString());
        context.Customers.Add(new Customer { Id = 5, Name = "Maria Perez", CedulaOrRif = "V-11223344", IsDefault = false });
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true, IsActive = true });
        var sale = new Sale { Id = 1, Status = SaleStatus.Pending, TotalUSD = 100m, TotalBsS = 5000m, AppliedRate = 50m, CustomerId = 5 };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context, cashDrawer: new CashDrawerService(context));
        var result = await service.HoldSaleAsync(1, new HoldSaleRequestDto
        {
            CustomerId = 5,
            ExchangeRate = 50m,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = 1,
                AmountUSD = 150m,
                AmountBsS = 7500m,
                ExchangeRate = 50m
            }
        });

        Assert.Equal("Completed", result.Status);
        Assert.Equal(7500m, result.FinalPaidAmountBsS);

        var changeTx = await context.CashTransactions.AsNoTracking()
            .SingleAsync(t => t.SaleId == 1 && t.Type == CashTransactionType.Expense);
        Assert.Equal(50m, changeTx.AmountUsd);
        Assert.Equal(2500m, changeTx.AmountLocal);
        Assert.True(changeTx.IsPhysicalCash);
        Assert.Equal(CashTransactionSource.SalePayment, changeTx.Source);

        var incomeTx = await context.CashTransactions.AsNoTracking()
            .SingleAsync(t => t.SaleId == 1 && t.Type == CashTransactionType.Income);
        Assert.Equal(7500m, incomeTx.AmountLocal);
    }
}
