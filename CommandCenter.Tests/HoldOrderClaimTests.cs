using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Exceptions;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests;

public class HoldOrderClaimTests
{
    private static SalesDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private static SalesService CreateService(
        SalesDbContext context,
        Mock<IInventoryService> inventory,
        Mock<IMediator> mediator,
        Mock<ICashDrawerService> cashDrawer,
        Mock<ISystemSettingsService> settings,
        Mock<IHoldOrderNotifier> notifier)
    {
        return new SalesService(context, inventory.Object, mediator.Object, cashDrawer.Object, settings.Object, notifier.Object);
    }

    private static Sale CreateOnHoldSale(int id, int? claimedByUserId = null, SaleClaimAction action = SaleClaimAction.None, string? claimedByUserName = null)
    {
        return new Sale
        {
            Id = id,
            Status = SaleStatus.OnHold,
            TotalUSD = 100m,
            AppliedRate = 50m,
            ClaimedByUserId = claimedByUserId,
            ClaimedByUserName = claimedByUserName,
            ClaimAction = action,
            ClaimedAtUtc = claimedByUserId.HasValue ? DateTime.UtcNow : null
        };
    }

    [Fact]
    public async Task ClaimSaleAsync_WhenUnclaimedOnHold_AssignsClaimToCashier()
    {
        using var context = GetInMemoryDbContext();
        var inventory = new Mock<IInventoryService>();
        var mediator = new Mock<IMediator>();
        var cashDrawer = new Mock<ICashDrawerService>();
        var settings = new Mock<ISystemSettingsService>();
        var notifier = new Mock<IHoldOrderNotifier>();

        context.Users.Add(new User { Id = 5, Name = "Ana Cajera", FullName = "Ana Cajera Perez" });
        context.Sales.Add(CreateOnHoldSale(1));
        await context.SaveChangesAsync();

        var service = CreateService(context, inventory, mediator, cashDrawer, settings, notifier);

        var result = await service.ClaimSaleAsync(1, SaleClaimAction.Editing, 5);

        Assert.Equal(5, result.ClaimedByUserId);
        Assert.Equal("Ana Cajera", result.ClaimedByUserName);
        Assert.Equal("Editing", result.ClaimAction);
        Assert.NotNull(result.ClaimedAtUtc);
        notifier.Verify(n => n.NotifyHoldOrdersChangedAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClaimSaleAsync_WhenActionNone_ThrowsArgumentException()
    {
        using var context = GetInMemoryDbContext();
        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.ClaimSaleAsync(1, SaleClaimAction.None, 5));

        Assert.Contains("Editing o Checkout", ex.Message);
    }

    [Fact]
    public async Task ClaimSaleAsync_WhenNoActingUser_ThrowsArgumentException()
    {
        using var context = GetInMemoryDbContext();
        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.ClaimSaleAsync(1, SaleClaimAction.Editing, null));

        Assert.Contains("usuario autenticado", ex.Message);
    }

    [Fact]
    public async Task ClaimSaleAsync_WhenClaimedByAnotherCashier_ThrowsSaleLockedException()
    {
        using var context = GetInMemoryDbContext();
        context.Sales.Add(CreateOnHoldSale(1, claimedByUserId: 9, action: SaleClaimAction.Checkout, claimedByUserName: "Carlos Cajero"));
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());

        var ex = await Assert.ThrowsAsync<SaleLockedException>(() => service.ClaimSaleAsync(1, SaleClaimAction.Editing, 5));

        Assert.Equal(9, ex.ClaimedByUserId);
        Assert.Equal("Checkout", ex.ClaimAction);
        Assert.Contains("Carlos Cajero", ex.Message);
        Assert.Contains("en proceso de pago", ex.Message);
    }

    [Fact]
    public async Task ClaimSaleAsync_WhenSameCashierReclaims_UpdatesAction()
    {
        using var context = GetInMemoryDbContext();
        context.Users.Add(new User { Id = 5, Name = "Ana Cajera" });
        context.Sales.Add(CreateOnHoldSale(1, claimedByUserId: 5, action: SaleClaimAction.Editing, claimedByUserName: "Ana Cajera"));
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());

        var result = await service.ClaimSaleAsync(1, SaleClaimAction.Checkout, 5);

        Assert.Equal(5, result.ClaimedByUserId);
        Assert.Equal("Checkout", result.ClaimAction);
    }

    [Fact]
    public async Task ClaimSaleAsync_WhenSaleNotOnHold_ThrowsInvalidOperationException()
    {
        using var context = GetInMemoryDbContext();
        context.Sales.Add(new Sale { Id = 1, Status = SaleStatus.Pending, TotalUSD = 10m });
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClaimSaleAsync(1, SaleClaimAction.Editing, 5));

        Assert.Contains("En Espera", ex.Message);
    }

    [Fact]
    public async Task ReleaseSaleAsync_WhenHolder_ReleasesClaim()
    {
        using var context = GetInMemoryDbContext();
        var notifier = new Mock<IHoldOrderNotifier>();
        context.Sales.Add(CreateOnHoldSale(1, claimedByUserId: 7, action: SaleClaimAction.Checkout, claimedByUserName: "Cajero Siete"));
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), notifier);

        var result = await service.ReleaseSaleAsync(1, 7);

        Assert.Null(result.ClaimedByUserId);
        Assert.Null(result.ClaimedByUserName);
        Assert.Equal("None", result.ClaimAction);
        Assert.Null(result.ClaimedAtUtc);
        notifier.Verify(n => n.NotifyHoldOrdersChangedAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReleaseSaleAsync_WhenForceByOther_ReleasesClaim()
    {
        using var context = GetInMemoryDbContext();
        context.Sales.Add(CreateOnHoldSale(1, claimedByUserId: 9, action: SaleClaimAction.Editing, claimedByUserName: "Carlos Cajero"));
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());

        var result = await service.ReleaseSaleAsync(1, 7, force: true);

        Assert.Null(result.ClaimedByUserId);
        Assert.Equal("None", result.ClaimAction);
    }

    [Fact]
    public async Task ReleaseSaleAsync_WhenNotHolderAndNotForce_ThrowsSaleLockedException()
    {
        using var context = GetInMemoryDbContext();
        context.Sales.Add(CreateOnHoldSale(1, claimedByUserId: 9, action: SaleClaimAction.Editing, claimedByUserName: "Carlos Cajero"));
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());

        var ex = await Assert.ThrowsAsync<SaleLockedException>(() => service.ReleaseSaleAsync(1, 7));

        Assert.Equal(9, ex.ClaimedByUserId);
        Assert.Contains("editando pedido", ex.Message);
    }

    [Fact]
    public async Task ReleaseSaleAsync_WhenSaleNotOnHold_IsNoOp()
    {
        using var context = GetInMemoryDbContext();
        context.Sales.Add(new Sale
        {
            Id = 1,
            Status = SaleStatus.Completed,
            TotalUSD = 100m,
            ClaimedByUserId = 7,
            ClaimedByUserName = "Cajero Siete",
            ClaimAction = SaleClaimAction.Checkout,
            ClaimedAtUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());

        var result = await service.ReleaseSaleAsync(1, 7);

        Assert.Null(result.ClaimedByUserId);
        Assert.Equal("None", result.ClaimAction);
    }

    [Fact]
    public async Task UpdateSaleItemsAsync_WhenClaimedByAnotherCashier_ThrowsSaleLockedException()
    {
        using var context = GetInMemoryDbContext();
        context.Sales.Add(CreateOnHoldSale(1, claimedByUserId: 9, action: SaleClaimAction.Editing, claimedByUserName: "Carlos Cajero"));
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());
        var request = new Sales.Module.DTOs.UpdateSaleItemsRequestDto();

        await Assert.ThrowsAsync<SaleLockedException>(() => service.UpdateSaleItemsAsync(1, request, false, 7));
    }

    [Fact]
    public async Task AddPaymentToHoldSaleAsync_WhenClaimedByAnotherCashier_ThrowsSaleLockedException()
    {
        using var context = GetInMemoryDbContext();
        context.Sales.Add(CreateOnHoldSale(1, claimedByUserId: 9, action: SaleClaimAction.Editing, claimedByUserName: "Carlos Cajero"));
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());
        var request = new AddPaymentRequestDto { PaymentMethodId = 1, AmountBsS = 100m, ExchangeRate = 50m };

        await Assert.ThrowsAsync<SaleLockedException>(() => service.AddPaymentToHoldSaleAsync(1, request, null, null, 7));
    }

    [Fact]
    public async Task CompleteSaleAsync_WhenClaimedByAnotherCashier_ThrowsSaleLockedException()
    {
        using var context = GetInMemoryDbContext();
        context.Sales.Add(CreateOnHoldSale(1, claimedByUserId: 9, action: SaleClaimAction.Checkout, claimedByUserName: "Carlos Cajero"));
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());
        var payments = new[] { new PaymentInfo(1, 100m, 5000m, null) };

        await Assert.ThrowsAsync<SaleLockedException>(() => service.CompleteSaleAsync(1, 50m, payments, 0m, 7, false, null, null, default, 7));
    }

    [Fact]
    public async Task CancelSaleAsync_WhenClaimedByAnotherCashier_ThrowsSaleLockedException()
    {
        using var context = GetInMemoryDbContext();
        context.Sales.Add(CreateOnHoldSale(1, claimedByUserId: 9, action: SaleClaimAction.Editing, claimedByUserName: "Carlos Cajero"));
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());

        await Assert.ThrowsAsync<SaleLockedException>(() => service.CancelSaleAsync(1, 7));
    }

    [Fact]
    public async Task CompleteSaleAsync_WhenHolder_CompletesAndClearsClaim()
    {
        using var context = GetInMemoryDbContext();
        var cashDrawer = new Mock<ICashDrawerService>();
        var notifier = new Mock<IHoldOrderNotifier>();

        cashDrawer.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1 });

        context.Sales.Add(CreateOnHoldSale(1, claimedByUserId: 7, action: SaleClaimAction.Checkout, claimedByUserName: "Cajero Siete"));
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true });
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), cashDrawer, new Mock<ISystemSettingsService>(), notifier);
        var payments = new[] { new PaymentInfo(1, 100m, 5000m, null) };

        var invoiceNumber = await service.CompleteSaleAsync(1, 50m, payments, 0m, 7, false, null, null, default, 7);

        Assert.True(invoiceNumber > 0);
        var stored = await context.Sales.FirstAsync(s => s.Id == 1);
        Assert.Equal(SaleStatus.Completed, stored.Status);
        Assert.Null(stored.ClaimedByUserId);
        Assert.Null(stored.ClaimedByUserName);
        Assert.Equal(SaleClaimAction.None, stored.ClaimAction);
        Assert.Null(stored.ClaimedAtUtc);
        notifier.Verify(n => n.NotifyHoldOrdersChangedAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HoldSale_WhenClaimedByAnotherCashier_ThrowsSaleLockedException()
    {
        using var context = GetInMemoryDbContext();
        context.Sales.Add(CreateOnHoldSale(1, claimedByUserId: 9, action: SaleClaimAction.Checkout, claimedByUserName: "Carlos Cajero"));
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());
        var request = new HoldSaleRequestDto { CustomerId = 5, ExchangeRate = 50m };

        await Assert.ThrowsAsync<SaleLockedException>(() => service.HoldSaleAsync(1, request, null, null, 7));
    }

    [Fact]
    public async Task UpdatePriceList_WhenClaimedByAnotherCashier_ThrowsSaleLockedException()
    {
        using var context = GetInMemoryDbContext();
        context.Sales.Add(CreateOnHoldSale(1, claimedByUserId: 9, action: SaleClaimAction.Editing, claimedByUserName: "Carlos Cajero"));
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());

        await Assert.ThrowsAsync<SaleLockedException>(() => service.UpdatePriceListAsync(1, "Wholesale", 7));
    }

    [Fact]
    public async Task ConfirmPickup_WhenClaimedByAnotherCashier_ThrowsSaleLockedException()
    {
        using var context = GetInMemoryDbContext();
        context.Sales.Add(CreateOnHoldSale(1, claimedByUserId: 9, action: SaleClaimAction.Checkout, claimedByUserName: "Carlos Cajero"));
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());

        await Assert.ThrowsAsync<SaleLockedException>(() => service.ConfirmPickupAsync(1, 7));
    }

    [Fact]
    public async Task ConfirmPickup_WhenOnHoldWithoutClaim_ThrowsInvalidOperationException()
    {
        using var context = GetInMemoryDbContext();
        context.Sales.Add(CreateOnHoldSale(1));
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());

        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.ConfirmPickupAsync(1, 7));

        Assert.Contains("no está reclamado", ex.Message);
    }

    [Fact]
    public async Task ConfirmPickup_WhenCompletedPendingPickup_ClearsNothingAndSucceeds()
    {
        using var context = GetInMemoryDbContext();
        var claimedAt = DateTime.UtcNow;
        context.Sales.Add(new Sale
        {
            Id = 1,
            Status = SaleStatus.Completed,
            DeliveryStatus = SaleDeliveryStatus.PendingPickup,
            TotalUSD = 100m,
            AppliedRate = 50m,
            ClaimedByUserId = 7,
            ClaimedByUserName = "Cajero Siete",
            ClaimAction = SaleClaimAction.Checkout,
            ClaimedAtUtc = claimedAt
        });
        await context.SaveChangesAsync();

        var service = CreateService(context, new Mock<IInventoryService>(), new Mock<IMediator>(), new Mock<ICashDrawerService>(), new Mock<ISystemSettingsService>(), new Mock<IHoldOrderNotifier>());

        var result = await service.ConfirmPickupAsync(1, 9);

        Assert.Equal("Delivered", result.DeliveryStatus);
        Assert.NotNull(result.PickupDate);

        var stored = await context.Sales.FirstAsync(s => s.Id == 1);
        Assert.Equal(SaleDeliveryStatus.Delivered, stored.DeliveryStatus);
        Assert.NotNull(stored.PickupDate);
        Assert.Equal(7, stored.ClaimedByUserId);
        Assert.Equal("Cajero Siete", stored.ClaimedByUserName);
        Assert.Equal(SaleClaimAction.Checkout, stored.ClaimAction);
        Assert.Equal(claimedAt, stored.ClaimedAtUtc);
    }
}
