using System;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Exceptions;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests;

public class HoldNotClaimedPreconditionTests
{
    private const int TestActorId = 42;

    private SalesDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private static void SeedClaim(Sale sale, int actorId)
    {
        sale.ClaimedByUserId = actorId;
        sale.ClaimAction = SaleClaimAction.Editing;
        sale.ClaimedByUserName = "Test Actor";
        sale.ClaimedAtUtc = DateTime.UtcNow;
    }

    [Fact]
    public async Task UpdateSaleItems_SinReclamo_LanzaHoldNotClaimedException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 50m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
        var request = new Sales.Module.DTOs.UpdateSaleItemsRequestDto
        {
            Items = new System.Collections.Generic.List<Sales.Module.DTOs.UpdateSaleItemDto>
            {
                new Sales.Module.DTOs.UpdateSaleItemDto { ProductId = 1, Quantity = 1, UnitPrice = 100m }
            }
        };

        var ex = await Assert.ThrowsAsync<HoldNotClaimedException>(() => service.UpdateSaleItemsAsync(1, request, actingUserId: TestActorId));
        Assert.Equal(1, ex.SaleId);
    }

    [Fact]
    public async Task AddPaymentsBatch_SinReclamo_LanzaHoldNotClaimedException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 50m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
        var payments = new System.Collections.Generic.List<AddPaymentRequestDto>
        {
            new AddPaymentRequestDto { PaymentMethodId = 1, AmountUSD = 10m, AmountBsS = 500m, ExchangeRate = 50m }
        };

        var ex = await Assert.ThrowsAsync<HoldNotClaimedException>(() => service.AddPaymentsBatchToHoldSaleAsync(1, payments, actingUserId: TestActorId));
        Assert.Equal(1, ex.SaleId);
    }

    [Fact]
    public async Task CompleteSale_SinReclamo_LanzaHoldNotClaimedException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 50m };
        context.Sales.Add(sale);
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true });
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
        var payments = new[] { new Sales.Module.Interfaces.PaymentInfo(1, 100m, 5000m, null) };

        var ex = await Assert.ThrowsAsync<HoldNotClaimedException>(() => service.CompleteSaleAsync(1, 50m, payments, actingUserId: TestActorId));
        Assert.Equal(1, ex.SaleId);
    }

    [Fact]
    public async Task CancelSale_SinReclamo_LanzaHoldNotClaimedException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale { Id = 1, TotalUSD = 50m, Status = SaleStatus.OnHold };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var ex = await Assert.ThrowsAsync<HoldNotClaimedException>(() => service.CancelSaleAsync(1, actingUserId: TestActorId));
        Assert.Equal(1, ex.SaleId);
    }

    [Fact]
    public async Task UpdateExchangeRate_SinReclamo_LanzaHoldNotClaimedException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 50m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var ex = await Assert.ThrowsAsync<HoldNotClaimedException>(() => service.UpdateExchangeRateAsync(1, 60m, actingUserId: TestActorId));
        Assert.Equal(1, ex.SaleId);
    }

    [Fact]
    public async Task UpdatePriceList_SinReclamo_LanzaHoldNotClaimedException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 50m, PriceListType = "Retail" };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var ex = await Assert.ThrowsAsync<HoldNotClaimedException>(() => service.UpdatePriceListAsync(1, "Wholesale", actingUserId: TestActorId));
        Assert.Equal(1, ex.SaleId);
    }

    [Fact]
    public async Task UpdateSaleCustomer_SinReclamo_LanzaHoldNotClaimedException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Test Customer" };
        context.Customers.Add(customer);
        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 50m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var ex = await Assert.ThrowsAsync<HoldNotClaimedException>(() => service.UpdateSaleCustomerAsync(1, 1, actingUserId: TestActorId));
        Assert.Equal(1, ex.SaleId);
    }

    [Fact]
    public async Task HoldSale_Rehold_SinReclamo_LanzaHoldNotClaimedException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Test Customer" };
        context.Customers.Add(customer);
        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 50m, CustomerId = 1 };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
        var request = new HoldSaleRequestDto { CustomerId = 1, ExchangeRate = 50m };

        var ex = await Assert.ThrowsAsync<HoldNotClaimedException>(() => service.HoldSaleAsync(1, request, actingUserId: TestActorId));
        Assert.Equal(1, ex.SaleId);
    }

    [Fact]
    public async Task AddItem_SinReclamo_LanzaHoldNotClaimedException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockInventory.Setup(i => i.GetProductByIdAsync(10)).ReturnsAsync(new Product { Id = 10, Name = "Test", PriceUSD = 10m });

        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 50m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var ex = await Assert.ThrowsAsync<HoldNotClaimedException>(() => service.AddItemAsync(1, 10, 1m, 50m, actingUserId: TestActorId));
        Assert.Equal(1, ex.SaleId);
    }

    [Fact]
    public async Task ActorNulo_LanzaHoldNotClaimedException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 50m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        await Assert.ThrowsAsync<HoldNotClaimedException>(() => service.UpdateSaleItemsAsync(1, new Sales.Module.DTOs.UpdateSaleItemsRequestDto
        {
            Items = new System.Collections.Generic.List<Sales.Module.DTOs.UpdateSaleItemDto>
            {
                new Sales.Module.DTOs.UpdateSaleItemDto { ProductId = 1, Quantity = 1, UnitPrice = 100m }
            }
        }, actingUserId: null));
    }

    [Fact]
    public async Task ClaimDelActor_Permite_Mutacion()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockInventory.Setup(i => i.GetProductByIdAsync(10)).ReturnsAsync(new Product { Id = 10, Name = "Test", PriceUSD = 10m });

        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 50m };
        SeedClaim(sale, TestActorId);
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var result = await service.AddItemAsync(1, 10, 1m, 50m, actingUserId: TestActorId);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ClaimAjeno_LanzaSaleLockedException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 50m };
        SeedClaim(sale, 99);
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        await Assert.ThrowsAsync<SaleLockedException>(() => service.UpdateSaleItemsAsync(1, new Sales.Module.DTOs.UpdateSaleItemsRequestDto
        {
            Items = new System.Collections.Generic.List<Sales.Module.DTOs.UpdateSaleItemDto>
            {
                new Sales.Module.DTOs.UpdateSaleItemDto { ProductId = 1, Quantity = 1, UnitPrice = 100m }
            }
        }, actingUserId: TestActorId));
    }

    [Fact]
    public async Task ConfirmPickup_SobreCompleted_NoRequiereReclamo()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale
        {
            Id = 1,
            TotalUSD = 100m,
            Status = SaleStatus.Completed,
            DeliveryStatus = SaleDeliveryStatus.PendingPickup,
            AppliedRate = 50m
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var result = await service.ConfirmPickupAsync(1);
        Assert.NotNull(result);
        var updated = await context.Sales.FindAsync(1);
        Assert.Equal(SaleDeliveryStatus.Delivered, updated!.DeliveryStatus);
    }

    [Fact]
    public async Task RecalculateOnHoldSalesAsync_NoRequiereReclamo()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.OnHold,
            AppliedRate = 50m,
            TotalUSD = 100m,
            TotalBsS = 5000m
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        int count = await service.RecalculateOnHoldSalesAsync(60m);
        Assert.Equal(1, count);
        var updated = await context.Sales.FindAsync(1);
        Assert.Equal(60m, updated!.AppliedRate);
    }

    [Fact]
    public async Task ForceRelease_NoAfectadoPorPrecondicion()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 50m };
        SeedClaim(sale, 99);
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var result = await service.ReleaseSaleAsync(1, TestActorId, force: true);
        Assert.NotNull(result);
        var updated = await context.Sales.FindAsync(1);
        Assert.Null(updated!.ClaimedByUserId);
    }
}
