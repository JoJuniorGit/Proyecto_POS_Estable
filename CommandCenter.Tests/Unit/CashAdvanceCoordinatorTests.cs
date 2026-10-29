using System;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class CashAdvanceCoordinatorTests
{
    private SalesDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private (CashAdvanceCoordinator coordinator, ICashDrawerService drawerService) CreateCoordinator(
        SalesDbContext context,
        ISystemSettingsService? settingsService = null,
        IMonetaryProfileService? monetaryProfileService = null,
        ICurrencyRateService? currencyRateService = null)
    {
        var inventoryMock = new Mock<IInventoryService>();
        inventoryMock.Setup(i => i.GetCashAdvanceProductAsync())
            .ReturnsAsync(new SaleProductInfoDto { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        var cashDrawerMock = new Mock<ICashDrawerService>();
        cashDrawerMock.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var mediatorMock = new Mock<IMediator>();
        var settings = settingsService ?? new Mock<ISystemSettingsService>().Object;

        var salesService = new SalesService(
            context,
            inventoryMock.Object,
            mediatorMock.Object,
            cashDrawerMock.Object,
            settings,
            monetaryProfileService: monetaryProfileService,
            currencyRateService: currencyRateService);

        // 8.211-T3c3c2 (S7): the drawer follows the same profile so local (COP) movements and the
        // fractional cash check use the same currency as the sale.
        var drawerService = new CashDrawerService(context, monetaryProfileService);

        return (
            new CashAdvanceCoordinator(context, salesService, drawerService, settings, monetaryProfileService),
            drawerService);
    }

    [Fact]
    public async Task ProcessAsync_ConfiguredCommissionApplied()
    {
        using var context = GetInMemoryDbContext();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("5.5");
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct))
            .ReturnsAsync("10.0");

        var (coordinator, drawerService) = CreateCoordinator(context, settingsMock.Object);
        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        var result = await coordinator.ProcessAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia Bancaria",
            isTransfer: true,
            exchangeRate: 50.0m
        );

        Assert.Equal(55m, result.CommissionAmountLocal);
        Assert.Equal(5.5m, result.CommissionPercentage);
        Assert.Equal(1055m, result.TotalChargedLocal);
    }

    [Fact]
    public async Task ProcessAsync_MissingCommissionRejectsWithoutPayoutOrSale()
    {
        using var context = GetInMemoryDbContext();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        var (coordinator, drawerService) = CreateCoordinator(context, settingsMock.Object);
        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ProcessAsync(
                sessionId: session.Id,
                requestedAmountLocal: 1000m,
                paymentMethodId: 2,
                paymentMethodName: "Transferencia Bancaria",
                isTransfer: true,
                exchangeRate: 50.0m
            ));

        Assert.Contains("no está configurada", ex.Message);

        var balance = await drawerService.GetCurrentBalanceLocalAsync(session.Id);
        Assert.Equal(2000m, balance);

        var saleCount = await context.Sales.CountAsync();
        Assert.Equal(0, saleCount);
    }

    [Fact]
    public async Task ProcessAsync_AppliedRateAnchorsDrawerMovements()
    {
        using var context = GetInMemoryDbContext();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");

        var inventoryMock = new Mock<IInventoryService>();
        inventoryMock.Setup(i => i.GetCashAdvanceProductAsync())
            .ReturnsAsync(new SaleProductInfoDto { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });
        inventoryMock.Setup(i => i.GetTodayExchangeRateAsync())
            .ReturnsAsync(60.0m);

        var cashDrawerMock = new Mock<ICashDrawerService>();
        cashDrawerMock.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var mediatorMock = new Mock<IMediator>();
        var salesService = new SalesService(context, inventoryMock.Object, mediatorMock.Object, cashDrawerMock.Object, settingsMock.Object);
        var drawerService = new CashDrawerService(context);
        var coordinator = new CashAdvanceCoordinator(context, salesService, drawerService, settingsMock.Object);

        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        var result = await coordinator.ProcessAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia Bancaria",
            isTransfer: true,
            exchangeRate: 50.0m
        );

        var sale = await context.Sales.FirstOrDefaultAsync(s => s.Id == result.RelatedSaleId);
        Assert.NotNull(sale);

        Assert.Equal(60.0m, sale.AppliedRate);
        Assert.Equal(60.0m, result.ExpenseTransaction.ExchangeRate);
        Assert.Equal(60.0m, result.IncomeTransaction.ExchangeRate);
    }

    [Fact]
    public async Task ProcessAsync_SnapshotsPreservedWithDefaultRoundingAdjustment()
    {
        using var context = GetInMemoryDbContext();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");

        var (coordinator, drawerService) = CreateCoordinator(context, settingsMock.Object);
        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        var result = await coordinator.ProcessAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia Bancaria",
            isTransfer: true,
            exchangeRate: 50.0m
        );

        var sale = await context.Sales.FirstOrDefaultAsync(s => s.Id == result.RelatedSaleId);
        Assert.NotNull(sale);
        Assert.True(sale.AppliedRate > 0m);
        Assert.Equal(21.4m, sale.TotalUSD);
        Assert.Equal(1070m, sale.TotalBsS);
        Assert.Equal(1070m, sale.FinalPaidAmountBsS);
        Assert.Equal(0m, sale.RoundingAdjustment);
    }

    [Fact]
    public async Task ProcessAsync_PhysicalDrawerBalanceDeductsOnlyRequested()
    {
        using var context = GetInMemoryDbContext();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");

        var (coordinator, drawerService) = CreateCoordinator(context, settingsMock.Object);
        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        var result = await coordinator.ProcessAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia Bancaria",
            isTransfer: true,
            exchangeRate: 50.0m
        );

        var physicalBalance = await drawerService.GetCurrentBalanceLocalAsync(session.Id);
        Assert.Equal(1000m, physicalBalance);
    }

    [Fact]
    public async Task ProcessAsync_InsufficientCashThrows()
    {
        using var context = GetInMemoryDbContext();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");

        var (coordinator, drawerService) = CreateCoordinator(context, settingsMock.Object);
        var session = await drawerService.OpenSessionAsync(500m, 50.0m);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ProcessAsync(
                sessionId: session.Id,
                requestedAmountLocal: 1000m,
                paymentMethodId: 2,
                paymentMethodName: "Transferencia",
                isTransfer: true,
                exchangeRate: 50.0m
            ));
    }

    [Fact]
    public async Task ProcessAsync_InsufficientCash_ThrowsMaskedMessageWithoutFigures()
    {
        using var context = GetInMemoryDbContext();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");

        var (coordinator, drawerService) = CreateCoordinator(context, settingsMock.Object);
        var session = await drawerService.OpenSessionAsync(500m, 50.0m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ProcessAsync(
                sessionId: session.Id,
                requestedAmountLocal: 1000m,
                paymentMethodId: 2,
                paymentMethodName: "Transferencia",
                isTransfer: true,
                exchangeRate: 50.0m
            ));

        Assert.Equal("Saldo de efectivo en caja insuficiente para el monto solicitado.", ex.Message);
        Assert.DoesNotContain("Disponible", ex.Message);
        Assert.DoesNotContain("500", ex.Message);

        var balance = await drawerService.GetCurrentBalanceLocalAsync(session.Id);
        Assert.Equal(500m, balance);
        Assert.Equal(0, await context.Sales.CountAsync());
    }

    [Fact]
    public async Task ProcessAsync_DecimalAmountThrows()
    {
        using var context = GetInMemoryDbContext();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");

        var (coordinator, drawerService) = CreateCoordinator(context, settingsMock.Object);
        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            coordinator.ProcessAsync(
                sessionId: session.Id,
                requestedAmountLocal: 100.50m,
                paymentMethodId: 2,
                paymentMethodName: "Transferencia",
                isTransfer: true,
                exchangeRate: 50.0m
            ));

        Assert.Contains("número entero sin decimales", ex.Message);
    }

    private static Mock<IMonetaryProfileService> CreateProfileMock(
        MonetaryMode mode,
        string primaryCode,
        string? secondaryCode)
    {
        var profile = new Mock<IMonetaryProfileService>();
        profile.Setup(p => p.GetAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = mode,
                PrimaryCurrencyCode = primaryCode,
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = secondaryCode,
                SecondaryCurrencySymbol = secondaryCode is null ? null : secondaryCode,
                IsConfigured = true
            });
        return profile;
    }

    [Fact]
    public async Task ProcessAsync_FractionalAmountWithSingleProfile_SkipsIntegerRuleAndReachesCheckoutGuard()
    {
        using var context = GetInMemoryDbContext();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");

        var profile = CreateProfileMock(MonetaryMode.Single, "USD", null);
        var (coordinator, drawerService) = CreateCoordinator(context, settingsMock.Object, profile.Object);
        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ProcessAsync(
                sessionId: session.Id,
                requestedAmountLocal: 100.50m,
                paymentMethodId: 2,
                paymentMethodName: "Transferencia",
                isTransfer: true,
                exchangeRate: 50.0m));

        // The fractional check passed; the Single checkout guard is the next rejection.
        Assert.Contains("moneda única", ex.Message);
        Assert.DoesNotContain("número entero sin decimales", ex.Message);
    }

    [Fact]
    public async Task ProcessAsync_FractionalAmountWithDualNonVesProfile_CompletesInCopWithManualSource()
    {
        using var context = GetInMemoryDbContext();
        context.Customers.Add(new Customer
        {
            Id = 1,
            Name = "Consumidor Final",
            CedulaOrRif = "V-00000000",
            IsDefault = true,
            IsActive = true
        });
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 6,
            Name = "Transferencia COP",
            IsCash = false,
            IsActive = true,
            Currency = "COP"
        });
        await context.SaveChangesAsync();

        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");

        var profile = CreateProfileMock(MonetaryMode.Dual, "USD", "COP");
        var rateMock = new Mock<ICurrencyRateService>();
        rateMock
            .Setup(r => r.GetRateAsync("USD", "COP", It.IsAny<DateOnly?>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new CurrencyRateQuote(
                4000m,
                CurrencyRateSource.Manual,
                Core.Helpers.TimeZoneHelper.GetVenezuelaDate(),
                "USD",
                "COP"));

        var (coordinator, drawerService) = CreateCoordinator(context, settingsMock.Object, profile.Object, rateMock.Object);
        var session = await drawerService.OpenSessionAsync(20000m, 4000m);

        // 8.211-T3c3c2 (S7): the fractional local request passes the integer guard and completes.
        var result = await coordinator.ProcessAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000.50m,
            paymentMethodId: 6,
            paymentMethodName: "Transferencia COP",
            isTransfer: true,
            exchangeRate: 4000m);

        Assert.Equal(1000.50m, result.RequestedAmountLocal);
        Assert.Equal("COP", result.ExpenseTransaction.CurrencyCode);
        Assert.Equal("COP", result.IncomeTransaction.CurrencyCode);

        var sale = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == result.RelatedSaleId!.Value);
        Assert.Equal("Manual", sale.AppliedRateSource);
        Assert.Equal("COP", sale.SecondaryCurrencyCode);
        Assert.Equal("USD", sale.PrimaryCurrencyCode);

        var payment = await context.SalePayments.AsNoTracking()
            .SingleAsync(p => p.SaleId == result.RelatedSaleId!.Value);
        Assert.Equal("COP", payment.CurrencyCode);
    }
}
