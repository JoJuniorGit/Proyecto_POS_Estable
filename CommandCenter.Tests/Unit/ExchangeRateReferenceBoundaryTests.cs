using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Hubs;
using Backend.API.Services;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ExchangeRateReferenceBoundaryTests
{
    [Fact]
    public async Task UpsertTodayRateAsync_WhenRateHasMoreThan2Decimals_PersistsCeiling2dReferenceAndIsIdempotent()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext("UpsertRate_Reference_" + Guid.NewGuid());
        var hubContextMock = new Mock<IHubContext<ExchangeRateHub>>();
        var hubClientsMock = new Mock<IHubClients>();
        var clientProxyMock = new Mock<IClientProxy>();
        hubContextMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
        hubClientsMock.Setup(c => c.All).Returns(clientProxyMock.Object);

        var inventoryMock = new Mock<IInventoryService>();
        var salesMock = new Mock<ISalesService>();
        var service = new ExchangeRateWriteService(context, inventoryMock.Object, salesMock.Object, hubContextMock.Object);

        bool first = await service.UpsertTodayRateAsync(804.63001m);
        var record = await context.ExchangeRateHistory.SingleAsync();

        Assert.True(first);
        Assert.Equal(804.64m, record.Rate);
        inventoryMock.Verify(i => i.InvalidateTodayExchangeRateCache(), Times.Once);
        salesMock.Verify(s => s.RecalculateOnHoldSalesAsync(804.64m), Times.Once);

        bool second = await service.UpsertTodayRateAsync(804.64m);
        Assert.False(second);
        inventoryMock.Verify(i => i.InvalidateTodayExchangeRateCache(), Times.Once);
        salesMock.Verify(s => s.RecalculateOnHoldSalesAsync(It.IsAny<decimal>()), Times.Once);
        clientProxyMock.Verify(p => p.SendCoreAsync("ReceiveRateUpdate", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReadEffectiveTodayRateAsync_WhenStoredRateHasMoreThan2Decimals_ReturnsCeiling2dReference()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext("Resolver_Reference_" + Guid.NewGuid());
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        context.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = today, Rate = 804.63001m, UpdatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var cashDrawerMock = new Mock<ICashDrawerService>();

        decimal rate = await ExchangeRateResolver.ReadEffectiveTodayRateAsync(context, cashDrawerMock.Object, CancellationToken.None);

        Assert.Equal(804.64m, rate);
    }

    [Fact]
    public async Task GetToday_WhenStoredRateHasMoreThan2Decimals_ReturnsCeiling2dReference()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext("GetToday_Reference_" + Guid.NewGuid());
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        context.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = today, Rate = 36.502175m, UpdatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var controller = ControllerFactory.CreateExchangeRateController(context, new Mock<ICurrentUserService>().Object);

        var result = await controller.GetToday() as OkObjectResult;

        Assert.NotNull(result);
        dynamic payload = result!.Value!;
        Assert.Equal(36.51m, (decimal)payload.Value);
    }

    [Fact]
    public async Task GetTodayExchangeRateAsync_WhenStoredRateHasMoreThan2Decimals_ReturnsCeiling2dReference()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext("DailyRef_Reference_" + Guid.NewGuid());
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        context.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = today, Rate = 36.502175m, UpdatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var service = new InventoryService(context);

        decimal rate = await service.GetTodayExchangeRateAsync();

        Assert.Equal(36.51m, rate);
    }

    [Fact]
    public async Task GetTodayExchangeRateAsync_WhenCacheHoldsLegacyRateWithMoreThan2Decimals_ReturnsCeiling2dReference()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext("CacheLegacy_Reference_" + Guid.NewGuid());
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set("bcv_rate_today", 804.63001m, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10), Size = 1 });

        var service = new InventoryService(context, cache: cache);

        decimal rate = await service.GetTodayExchangeRateAsync();

        Assert.Equal(804.64m, rate);
    }

    [Fact]
    public async Task AddTransaction_WhenManualRateAndStoredRateHaveExtraDecimals_AnchorsCeiling2dReference()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext("CashDrawer_Reference_" + Guid.NewGuid());
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext("CashDrawer_InvRef_" + Guid.NewGuid());
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        inventoryDb.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = today, Rate = 804.6301m, UpdatedAt = DateTime.UtcNow });
        await inventoryDb.SaveChangesAsync();

        var mockCashDrawer = new Mock<ICashDrawerService>();
        decimal? capturedRate = null;
        mockCashDrawer.Setup(c => c.AddTransactionAsync(It.IsAny<int>(), It.IsAny<CashTransactionType>(), It.IsAny<CashTransactionSource>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int sid, CashTransactionType t, CashTransactionSource s, decimal al, decimal au, decimal er, string d, int? rid, bool isPhys, int? pmId, CancellationToken ct) =>
            {
                capturedRate = er;
                return new CashTransactionResponseDto { Id = 1, SessionId = sid, Type = t, Source = s, AmountLocal = al, AmountUsd = au, ExchangeRate = er, Description = d, TransactionTime = DateTime.UtcNow };
            });

        var mockSettings = new Mock<ISystemSettingsService>();
        mockSettings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        var mockSalesService = new Mock<ISalesService>();
        var coordinator = new CashAdvanceCoordinator(salesDb, mockSalesService.Object, mockCashDrawer.Object, mockSettings.Object);
        var controller = ControllerFactory.CreateCashDrawerController(mockCashDrawer.Object, mockSettings.Object, salesDb, new Mock<ICurrentUserService>().Object, inventoryDb, coordinator);

        var result = await controller.AddTransaction(new AddTransactionRequest
        {
            SessionId = 1,
            Type = CashTransactionType.Income,
            Source = CashTransactionSource.SalePayment,
            AmountLocal = 1609.28m,
            ExchangeRate = 804.6301m,
            Description = "Txn manual 8.103"
        }, CancellationToken.None);

        Assert.NotNull(result.Result);
        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(804.64m, capturedRate);
    }

    [Fact]
    public async Task GetExchangeRate_WhenStoredRateHasMoreThan2Decimals_ReturnsCeiling2dReference()
    {
        using var db = TestDatabaseFactory.CreateInventoryDbContext("Settings_Reference_" + Guid.NewGuid());
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        db.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = today, Rate = 804.6301m, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerFactory.CreateSettingsController(db, new Mock<ICurrentUserService>().Object, new SystemSettingsService(db));

        var result = await controller.GetExchangeRate() as OkObjectResult;

        Assert.NotNull(result);
        dynamic payload = result!.Value!;
        Assert.Equal(804.64m, (decimal)payload.Value);
    }
}