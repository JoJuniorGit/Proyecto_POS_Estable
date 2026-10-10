using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.DTOs;
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
        mock.Setup(c => c.GetCurrentBalanceLocalAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000m);
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

    // 8.154 (SEC-03): los escenarios de adelanto exigen sesión (UserId) y usuario resuelto;
    // los defaults preservan el arnés A5 de AddTransaction (sin guard de identidad).
    private static CashDrawerController CreateController(
        ICashDrawerService cashDrawer,
        ISystemSettingsService settings,
        SalesDbContext salesDb,
        IInventoryService inventory,
        ICurrentUserService? currentUser = null,
        IUserService? userService = null,
        ISalesService? salesService = null)
    {
        var coordinator = new CashAdvanceCoordinator(salesDb, salesService ?? new Mock<ISalesService>().Object, cashDrawer, settings);
        var controller = new CashDrawerController(
            cashDrawer,
            settings,
            inventory,
            userService ?? new UserService(salesDb),
            currentUser ?? new Mock<ICurrentUserService>().Object,
            new TimeZoneProvider(settings),
            coordinator);

        // 8.149 (SRE-02): POST transaction exige Idempotency-Key.
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Request.Headers["Idempotency-Key"] = "RATE-ANCHOR-" + Guid.NewGuid().ToString("N");
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    private static void SeedOfficialRate(InventoryDbContext inventoryDb)
    {
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        inventoryDb.ExchangeRateHistory.Add(new ExchangeRateHistory { Date = today, Rate = OfficialRate, UpdatedAt = DateTime.UtcNow });
        inventoryDb.SaveChanges();
    }

    private static Mock<ISystemSettingsService> CreateAdvanceSettingsMock(string? toleranceSetting = null)
    {
        var mock = new Mock<ISystemSettingsService>();
        // Moq: el setup registrado al final gana; el específico de comisión va después del amplio.
        mock.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync(toleranceSetting);
        mock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct)).ReturnsAsync("5");
        return mock;
    }

    private static Mock<ISalesService> CreateAdvanceSalesMock()
    {
        var mock = new Mock<ISalesService>();
        mock.Setup(s => s.CreateCashAdvanceSaleAsync(
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<decimal>(), It.IsAny<int?>(), It.IsAny<string>(),
                It.IsAny<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SaleDto)null!);
        return mock;
    }

    private static (ICurrentUserService CurrentUser, IUserService UserService) CreateAdvanceIdentity()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(u => u.UserId).Returns("7");

        var userService = new Mock<IUserService>();
        userService.Setup(s => s.GetUserAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDto { Id = 7, Name = "Cajero A" });

        return (currentUser.Object, userService.Object);
    }

    // 8.154 (SEC-04): el adelanto se ancla antes del coordinador; la tasa capturada por
    // el AddTransactionAsync del envelope (Expense/Income) es la observable del anclaje.
    private static CashAdvanceRequest CreateAdvanceRequest(decimal exchangeRate) => new()
    {
        SessionId = 1,
        RequestedAmountLocal = 100m,
        PaymentMethodId = 1,
        PaymentMethodName = "Efectivo Bs.S",
        IsTransfer = false,
        ExchangeRate = exchangeRate
    };

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

    [Fact]
    public async Task CashAdvance_WhenDeviationExceedsDefaultTolerance_AnchorsCoordinatorRateToOfficial()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();
        SeedOfficialRate(inventoryDb);

        var capture = new RateCapture();
        var cashDrawer = CreateCashDrawerMock(capture);
        var (currentUser, userService) = CreateAdvanceIdentity();
        var controller = CreateController(
            cashDrawer.Object, CreateAdvanceSettingsMock().Object, salesDb, new InventoryService(inventoryDb),
            currentUser, userService, CreateAdvanceSalesMock().Object);

        var result = await controller.ProcessCashAdvance(CreateAdvanceRequest(67.00m), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(OfficialRate, capture.AppliedRate);
    }

    [Fact]
    public async Task CashAdvance_WhenDeviationIsWithinDefaultTolerance_UsesClientRate()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();
        SeedOfficialRate(inventoryDb);

        var capture = new RateCapture();
        var cashDrawer = CreateCashDrawerMock(capture);
        var (currentUser, userService) = CreateAdvanceIdentity();
        var controller = CreateController(
            cashDrawer.Object, CreateAdvanceSettingsMock().Object, salesDb, new InventoryService(inventoryDb),
            currentUser, userService, CreateAdvanceSalesMock().Object);

        var result = await controller.ProcessCashAdvance(CreateAdvanceRequest(63.50m), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(63.50m, capture.AppliedRate);
    }

    [Fact]
    public async Task CashAdvance_WhenDeviationReachesOneHundredPercent_ThrowsArgumentExceptionWithoutEffects()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();
        SeedOfficialRate(inventoryDb);

        var capture = new RateCapture();
        var cashDrawer = CreateCashDrawerMock(capture);
        var (currentUser, userService) = CreateAdvanceIdentity();
        var controller = CreateController(
            cashDrawer.Object, CreateAdvanceSettingsMock().Object, salesDb, new InventoryService(inventoryDb),
            currentUser, userService, CreateAdvanceSalesMock().Object);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => controller.ProcessCashAdvance(CreateAdvanceRequest(120.00m), CancellationToken.None));

        Assert.Contains("excede", ex.Message);
        Assert.Null(capture.AppliedRate);
    }

    [Fact]
    public async Task CashAdvance_WhenNoBcvRateForToday_FailsOpenWithRoundedClientRate()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();

        var capture = new RateCapture();
        var cashDrawer = CreateCashDrawerMock(capture);
        var (currentUser, userService) = CreateAdvanceIdentity();
        var controller = CreateController(
            cashDrawer.Object, CreateAdvanceSettingsMock().Object, salesDb, new InventoryService(inventoryDb),
            currentUser, userService, CreateAdvanceSalesMock().Object);

        var result = await controller.ProcessCashAdvance(CreateAdvanceRequest(80.463m), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(80.47m, capture.AppliedRate);
    }
}
