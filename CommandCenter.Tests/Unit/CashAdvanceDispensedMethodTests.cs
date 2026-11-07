using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.212-WU3: cash advance must distinguish the physical dispenser from the electronic receiver,
/// validate the exact physical method balance under the session lock, and preserve legacy callers.
/// </summary>
public class CashAdvanceDispensedMethodTests
{
    private static Mock<IMonetaryProfileService> DualUsdVesProfile()
    {
        var profile = new Mock<IMonetaryProfileService>();
        profile.Setup(p => p.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "USD",
            PrimaryCurrencySymbol = "$",
            SecondaryCurrencyCode = "VES",
            SecondaryCurrencySymbol = "Bs.S",
            IsConfigured = true
        });
        return profile;
    }

    private static Mock<ISystemSettingsService> Settings(decimal percentage = 10m)
    {
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync(percentage.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return settings;
    }

    private static (CashAdvanceCoordinator Coordinator, CashDrawerService Drawer) CreateCoordinator(
        SalesDbContext context,
        IMonetaryProfileService profile,
        ISystemSettingsService settings)
    {
        var inventory = new Mock<IInventoryService>();
        inventory.Setup(i => i.GetCashAdvanceProductAsync())
            .ReturnsAsync(new SaleProductInfoDto { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });
        var drawer = new CashDrawerService(context, profile);
        var sales = new SalesService(
            context,
            inventory.Object,
            Mock.Of<IMediator>(),
            drawer,
            settings,
            monetaryProfileService: profile);
        return (new CashAdvanceCoordinator(context, sales, drawer, settings, profile), drawer);
    }

    [Fact]
    public async Task LegacyRequest_WithoutMethodFields_KeepsWorking()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        var profile = DualUsdVesProfile();
        var settings = Settings();
        var (coordinator, drawer) = CreateCoordinator(context, profile.Object, settings.Object);
        var session = await drawer.OpenSessionAsync(1000m, 40m, openingBalanceUsd: 100m);

        var result = await coordinator.ProcessAsync(
            session.Id, 100m, 4, "Pago Movil", isTransfer: true, exchangeRate: 40m);

        Assert.Equal(100m, result.RequestedAmountLocal);
        Assert.Equal(110m, result.TotalChargedLocal);
        Assert.Equal(CashTransactionSource.CashAdvance, result.ExpenseTransaction.Source);
    }

    [Fact]
    public async Task ExplicitDispensedMethod_RecordsExpenseAgainstThatMethodAndIncomeAgainstReceiver()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        var profile = DualUsdVesProfile();
        var settings = Settings();
        var (coordinator, drawer) = CreateCoordinator(context, profile.Object, settings.Object);
        var session = await drawer.OpenSessionAsync(1000m, 40m, openingBalanceUsd: 100m);

        var result = await coordinator.ProcessAsync(
            session.Id, 100m, 4, "Pago Movil", isTransfer: true, exchangeRate: 40m,
            dispensedCashPaymentMethodId: 2, dispensedAmount: 100m, receivedPaymentMethodId: 4);

        Assert.Equal(2, result.DispensedCashPaymentMethodId);
        Assert.Equal("VES", result.DispensedCurrencyCode);
        Assert.Equal(4, result.ReceivedPaymentMethodId);
        Assert.Equal(2, result.ExpenseTransaction.PaymentMethodId);
        Assert.Equal("VES", result.ExpenseTransaction.CurrencyCode);
        Assert.Equal(4, result.IncomeTransaction.PaymentMethodId);
        Assert.False(result.IncomeTransaction.IsPhysicalCash);

        var balances = await drawer.GetPhysicalMethodBalancesAsync(session.Id);
        Assert.Equal(900m, balances.Single(b => b.PaymentMethodId == 2).CurrentBalance);
        Assert.Equal(100m, balances.Single(b => b.PaymentMethodId == 1).CurrentBalance);
    }

    [Fact]
    public async Task InsufficientDispensedMethodBalance_FailsWithoutSale()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        var profile = DualUsdVesProfile();
        var settings = Settings();
        var (coordinator, drawer) = CreateCoordinator(context, profile.Object, settings.Object);
        var session = await drawer.OpenSessionAsync(10m, 40m, openingBalanceUsd: 100m);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ProcessAsync(
                session.Id, 100m, 4, "Pago Movil", isTransfer: true, exchangeRate: 40m,
                dispensedCashPaymentMethodId: 2, dispensedAmount: 100m, receivedPaymentMethodId: 4));

        Assert.Contains("Existencia insuficiente", exception.Message);
        Assert.Equal(0, await context.Sales.CountAsync());
        Assert.Equal(0, await context.CashTransactions.CountAsync(t => t.Source == CashTransactionSource.CashAdvance));
    }

    [Fact]
    public async Task UsdDispensedToLocalReceiver_ComputesCrossCurrencyAndChargesReceiver()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        var profile = DualUsdVesProfile();
        var settings = Settings(5m);
        var (coordinator, drawer) = CreateCoordinator(context, profile.Object, settings.Object);
        var session = await drawer.OpenSessionAsync(1000m, 40m, openingBalanceUsd: 100m);

        var result = await coordinator.ProcessAsync(
            session.Id, 100m, 4, "Pago Movil", isTransfer: true, exchangeRate: 40m,
            dispensedCashPaymentMethodId: 1, dispensedAmount: 100m, receivedPaymentMethodId: 4);

        Assert.Equal("USD", result.DispensedCurrencyCode);
        Assert.Equal(100m, result.ExpenseTransaction.AmountLocal);
        Assert.Equal("VES", result.ReceivedCurrencyCode);
        Assert.Equal(4000m, result.ReceivedAmount);
        Assert.Equal(200m, result.ReceivedCommissionAmount);
        Assert.Equal(4200m, result.ReceivedTotalAmount);
        Assert.Equal(4, result.IncomeTransaction.PaymentMethodId);
        Assert.Equal("VES", result.IncomeTransaction.CurrencyCode);
        Assert.Equal(0m, (await drawer.GetPhysicalMethodBalancesAsync(session.Id)).Single(b => b.PaymentMethodId == 1).CurrentBalance);
    }

    [Fact]
    public async Task LocalDispensedToUsdReceiver_ComputesCrossCurrencyAndChargesReceiver()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 7,
            Name = "Zelle USD",
            IsCash = false,
            IsActive = true,
            Currency = "USD",
            DisplayOrder = 7
        });
        await context.SaveChangesAsync();
        var profile = DualUsdVesProfile();
        var settings = Settings(5m);
        var (coordinator, drawer) = CreateCoordinator(context, profile.Object, settings.Object);
        var session = await drawer.OpenSessionAsync(5000m, 40m, openingBalanceUsd: 100m);

        var result = await coordinator.ProcessAsync(
            session.Id, 4000m, 7, "Zelle USD", isTransfer: true, exchangeRate: 40m,
            dispensedCashPaymentMethodId: 2, dispensedAmount: 4000m, receivedPaymentMethodId: 7);

        Assert.Equal("VES", result.DispensedCurrencyCode);
        Assert.Equal("USD", result.ReceivedCurrencyCode);
        Assert.Equal(100m, result.ReceivedAmount);
        Assert.Equal(5m, result.ReceivedCommissionAmount);
        Assert.Equal(105m, result.ReceivedTotalAmount);
        Assert.Equal(2, result.ExpenseTransaction.PaymentMethodId);
        Assert.Equal(7, result.IncomeTransaction.PaymentMethodId);
        Assert.Equal(1000m, (await drawer.GetPhysicalMethodBalancesAsync(session.Id)).Single(b => b.PaymentMethodId == 2).CurrentBalance);
    }

    [Fact]
    public async Task TwoPhysicalMethodsWithoutDispensedSelection_AreRejected()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 8,
            Name = "Efectivo Bs.S secundario",
            IsCash = true,
            IsActive = true,
            Currency = "Bs.S",
            DisplayOrder = 8
        });
        await context.SaveChangesAsync();
        var profile = DualUsdVesProfile();
        var settings = Settings();
        var (coordinator, drawer) = CreateCoordinator(context, profile.Object, settings.Object);
        var session = await drawer.OpenSessionAsync(
            1000m, 40m, openingBalanceUsd: 100m,
            requestedMethodBalances: new List<OpenSessionMethodBalanceRequest>
            {
                new() { PaymentMethodId = 2, OpeningAmount = 500m },
                new() { PaymentMethodId = 8, OpeningAmount = 500m }
            });

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            coordinator.ProcessAsync(
                session.Id, 100m, 4, "Pago Movil", isTransfer: true, exchangeRate: 40m,
                dispensedAmount: 100m, receivedPaymentMethodId: 4));

        Assert.Contains("más de un método físico", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
