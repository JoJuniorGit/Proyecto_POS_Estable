using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Core.Interfaces;
using Core.Services;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class CashDrawerRateAnchorTests
{
    private const decimal OfficialRate = 60.00m;

    private sealed class RateCapture
    {
        public decimal? AppliedRate { get; set; }
    }

    private static Mock<ICashDrawerService> CreateCashDrawerMock(RateCapture capture)
    {
        var mock = new Mock<ICashDrawerService>();
        mock.Setup(c => c.AddTransactionAsync(
                It.IsAny<int>(), It.IsAny<CashTransactionType>(), It.IsAny<CashTransactionSource>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int sessionId, CashTransactionType type, CashTransactionSource source, decimal amountLocal, decimal amountUsd, decimal exchangeRate, string description, int? referenceId, bool isPhysical, int? paymentMethodId, CancellationToken _) =>
            {
                capture.AppliedRate = exchangeRate;
                return new CashTransactionResponseDto
                {
                    Id = 1,
                    SessionId = sessionId,
                    Type = type,
                    Source = source,
                    AmountLocal = amountLocal,
                    AmountUsd = amountUsd,
                    ExchangeRate = exchangeRate,
                    Description = description,
                    TransactionTime = DateTime.UtcNow
                };
            });
        return mock;
    }

    private static Mock<ISystemSettingsService> CreateSettingsMock(string? toleranceSetting = null)
    {
        var mock = new Mock<ISystemSettingsService>();
        mock.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync(toleranceSetting);
        return mock;
    }

    private static AddTransactionRequest CreateRequest(decimal exchangeRate) => new()
    {
        SessionId = 1,
        Type = CashTransactionType.Income,
        Source = CashTransactionSource.SalePayment,
        AmountLocal = 600m,
        ExchangeRate = exchangeRate,
        Description = "AUD-13 rate anchor pin"
    };

    private static CashDrawerController CreateController(
        ICashDrawerService cashDrawer,
        ISystemSettingsService settings,
        SalesDbContext salesDb,
        IInventoryService inventory)
    {
        var coordinator = new CashAdvanceCoordinator(salesDb, new Mock<ISalesService>().Object, cashDrawer, settings);
        return new CashDrawerController(
            cashDrawer,
            settings,
            inventory,
            new UserService(salesDb),
            new Mock<ICurrentUserService>().Object,
            new TimeZoneProvider(settings),
            coordinator);
    }

    private static void SeedOfficialRate(InventoryDbContext inventoryDb)
    {
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        inventoryDb.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = today, Rate = OfficialRate, UpdatedAt = DateTime.UtcNow });
        inventoryDb.SaveChanges();
    }

    [Fact]
    public async Task AddTransaction_WhenDeviationExceedsDefaultTolerance_AnchorsToOfficialRate()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();
        SeedOfficialRate(inventoryDb);

        var capture = new RateCapture();
        var cashDrawer = CreateCashDrawerMock(capture);
        var controller = CreateController(cashDrawer.Object, CreateSettingsMock().Object, salesDb, new InventoryService(inventoryDb));

        var result = await controller.AddTransaction(CreateRequest(67.00m), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(OfficialRate, capture.AppliedRate);
    }

    [Fact]
    public async Task AddTransaction_WhenDeviationIsWithinDefaultTolerance_AcceptsClientRate()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();
        SeedOfficialRate(inventoryDb);

        var capture = new RateCapture();
        var cashDrawer = CreateCashDrawerMock(capture);
        var controller = CreateController(cashDrawer.Object, CreateSettingsMock().Object, salesDb, new InventoryService(inventoryDb));

        var result = await controller.AddTransaction(CreateRequest(63.50m), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(63.50m, capture.AppliedRate);
    }

    [Fact]
    public async Task AddTransaction_WhenDeviationReachesOneHundredPercent_ThrowsArgumentException()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();
        SeedOfficialRate(inventoryDb);

        var capture = new RateCapture();
        var cashDrawer = CreateCashDrawerMock(capture);
        var controller = CreateController(cashDrawer.Object, CreateSettingsMock().Object, salesDb, new InventoryService(inventoryDb));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => controller.AddTransaction(CreateRequest(120.00m), CancellationToken.None));

        Assert.Contains("excede", ex.Message);
        Assert.Null(capture.AppliedRate);
    }

    [Fact]
    public async Task AddTransaction_WhenNoBcvRateForToday_FailsOpenWithRoundedClientRate()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();

        var capture = new RateCapture();
        var cashDrawer = CreateCashDrawerMock(capture);
        var controller = CreateController(cashDrawer.Object, CreateSettingsMock().Object, salesDb, new InventoryService(inventoryDb));

        var result = await controller.AddTransaction(CreateRequest(804.6301m), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(804.64m, capture.AppliedRate);
    }

    [Fact]
    public async Task AddTransaction_WhenBcvLookupThrows_FailsOpenWithRoundedClientRate()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();

        var capture = new RateCapture();
        var cashDrawer = CreateCashDrawerMock(capture);
        var inventory = new Mock<IInventoryService>();
        inventory.Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BCV lookup failure"));
        var controller = CreateController(cashDrawer.Object, CreateSettingsMock().Object, salesDb, inventory.Object);

        var result = await controller.AddTransaction(CreateRequest(36.502175m), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(36.51m, capture.AppliedRate);
    }

    [Fact]
    public async Task AddTransaction_WhenConfiguredToleranceIsWider_AcceptsDeviationAboveDefault()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();
        SeedOfficialRate(inventoryDb);

        var capture = new RateCapture();
        var cashDrawer = CreateCashDrawerMock(capture);
        var controller = CreateController(cashDrawer.Object, CreateSettingsMock("0.20").Object, salesDb, new InventoryService(inventoryDb));

        var result = await controller.AddTransaction(CreateRequest(67.00m), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(67.00m, capture.AppliedRate);
    }

    [Fact]
    public async Task AddTransaction_WhenConfiguredToleranceIsInvalid_FallsBackToDefaultTenPercent()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();
        SeedOfficialRate(inventoryDb);

        var capture = new RateCapture();
        var cashDrawer = CreateCashDrawerMock(capture);
        var controller = CreateController(cashDrawer.Object, CreateSettingsMock("not-a-percentage").Object, salesDb, new InventoryService(inventoryDb));

        var result = await controller.AddTransaction(CreateRequest(67.00m), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(OfficialRate, capture.AppliedRate);
    }
}
