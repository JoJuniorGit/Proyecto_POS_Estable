using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.212-WU2: physical balances per payment method. Movements are attributed to their exact
/// PaymentMethodId and valued with the movement currency snapshot (USD uses AmountUsd, any local
/// currency uses AmountLocal), so opening, sales, change, petty cash, cash-in/out and cash
/// advances never leak into another method.
/// </summary>
public class CashDrawerPhysicalMethodBalancesTests
{
    private const decimal Rate = 50m;
    private const int UsdMethodId = 1;
    private const int LocalMethodId = 2;

    private static Mock<IMonetaryProfileService> DualProfile()
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

    private static async Task<(CashDrawerService Service, int SessionId)> OpenDrawerAsync(
        Sales.Module.Data.SalesDbContext context,
        decimal openingLocal = 1000m,
        decimal openingUsd = 20m)
    {
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        var service = new CashDrawerService(context, DualProfile().Object);
        var session = await service.OpenSessionAsync(openingLocal, Rate, openingBalanceUsd: openingUsd);
        return (service, session.Id);
    }

    private static async Task<PhysicalMethodBalanceDto> BalanceForAsync(
        CashDrawerService service,
        int sessionId,
        int paymentMethodId)
    {
        var balances = await service.GetPhysicalMethodBalancesAsync(sessionId);
        return Assert.Single(balances, balance => balance.PaymentMethodId == paymentMethodId);
    }

    private static CashTransaction PhysicalMovement(
        int sessionId,
        int paymentMethodId,
        CashTransactionType type,
        CashTransactionSource source,
        decimal amountLocal,
        decimal amountUsd,
        string currencyCode,
        string description)
        => new()
        {
            SessionId = sessionId,
            Type = type,
            Source = source,
            AmountLocal = amountLocal,
            AmountUsd = amountUsd,
            ExchangeRate = Rate,
            IsPhysicalCash = true,
            PaymentMethodId = paymentMethodId,
            CurrencyCode = currencyCode,
            Description = description,
            TransactionTime = DateTime.UtcNow
        };

    [Fact]
    public async Task OpeningSession_ProjectsOneRowPerPhysicalMethodWithFrozenCurrencyAndSymbol()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var (service, sessionId) = await OpenDrawerAsync(context, openingLocal: 100m, openingUsd: 20m);

        var balances = await service.GetPhysicalMethodBalancesAsync(sessionId);

        Assert.Equal(2, balances.Count);

        var usd = Assert.Single(balances, balance => balance.PaymentMethodId == UsdMethodId);
        Assert.Equal("Efectivo USD", usd.Name);
        Assert.Equal("USD", usd.CurrencyCode);
        Assert.Equal("$", usd.CurrencySymbol);
        Assert.Equal(20m, usd.CurrentBalance);
        Assert.True(usd.IsActive);
        Assert.True(usd.CanDispense);

        var local = Assert.Single(balances, balance => balance.PaymentMethodId == LocalMethodId);
        Assert.Equal("Efectivo Bs.S", local.Name);
        Assert.Equal("VES", local.CurrencyCode);
        Assert.Equal("Bs.S", local.CurrencySymbol);
        Assert.Equal(100m, local.CurrentBalance);
        Assert.True(local.IsActive);
        Assert.True(local.CanDispense);

        var activeSession = await service.GetActiveSessionAsync();
        Assert.NotNull(activeSession);
        Assert.NotNull(activeSession!.PhysicalMethodBalances);
        Assert.Equal(2, activeSession.PhysicalMethodBalances!.Count);

        var withTransactions = await service.GetActiveSessionWithTransactionsAsync();
        Assert.NotNull(withTransactions);
        Assert.NotNull(withTransactions!.PhysicalMethodBalances);
        Assert.Equal(2, withTransactions.PhysicalMethodBalances!.Count);
    }

    [Fact]
    public async Task CashSalePaymentOnUsdMethod_ChangesOnlyThatMethod()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var (service, sessionId) = await OpenDrawerAsync(context);

        await service.AddTransactionAsync(
            sessionId,
            CashTransactionType.Income,
            CashTransactionSource.SalePayment,
            amountLocal: 500m,
            amountUsd: 10m,
            exchangeRate: Rate,
            description: "Venta efectivo USD",
            paymentMethodId: UsdMethodId);

        var usd = await BalanceForAsync(service, sessionId, UsdMethodId);
        var local = await BalanceForAsync(service, sessionId, LocalMethodId);

        Assert.Equal(30m, usd.CurrentBalance);
        Assert.Equal(1000m, local.CurrentBalance);
    }

    [Fact]
    public async Task SaleChangeOnLocalMethod_ChangesOnlyThatMethod()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var (service, sessionId) = await OpenDrawerAsync(context);

        await service.RecordSaleChangeAsync(
            sessionId,
            changeUsd: 2m,
            changeBsS: 100m,
            exchangeRate: Rate,
            description: "Vuelto",
            saleId: 1,
            cashPaymentMethodId: LocalMethodId);
        await context.SaveChangesAsync();

        var usd = await BalanceForAsync(service, sessionId, UsdMethodId);
        var local = await BalanceForAsync(service, sessionId, LocalMethodId);

        Assert.Equal(20m, usd.CurrentBalance);
        Assert.Equal(900m, local.CurrentBalance);
    }

    [Fact]
    public async Task PettyCashExpenseAndReversal_ChangeOnlyTheLocalMethod()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var (service, sessionId) = await OpenDrawerAsync(context);

        context.CashTransactions.Add(PhysicalMovement(
            sessionId,
            LocalMethodId,
            CashTransactionType.Expense,
            CashTransactionSource.PettyCashExpense,
            amountLocal: 30m,
            amountUsd: 0.6m,
            currencyCode: "Bs.S",
            description: "Caja chica"));
        await context.SaveChangesAsync();

        var usdAfterExpense = await BalanceForAsync(service, sessionId, UsdMethodId);
        var localAfterExpense = await BalanceForAsync(service, sessionId, LocalMethodId);
        Assert.Equal(20m, usdAfterExpense.CurrentBalance);
        Assert.Equal(970m, localAfterExpense.CurrentBalance);

        context.CashTransactions.Add(PhysicalMovement(
            sessionId,
            LocalMethodId,
            CashTransactionType.Income,
            CashTransactionSource.PettyCashReversal,
            amountLocal: 30m,
            amountUsd: 0.6m,
            currencyCode: "Bs.S",
            description: "Reversa caja chica"));
        await context.SaveChangesAsync();

        var usdAfterReversal = await BalanceForAsync(service, sessionId, UsdMethodId);
        var localAfterReversal = await BalanceForAsync(service, sessionId, LocalMethodId);
        Assert.Equal(20m, usdAfterReversal.CurrentBalance);
        Assert.Equal(1000m, localAfterReversal.CurrentBalance);
    }

    [Fact]
    public async Task CashInAndCashOutOnUsdMethod_ChangeOnlyThatMethod()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var (service, sessionId) = await OpenDrawerAsync(context);

        await service.AddTransactionAsync(
            sessionId,
            CashTransactionType.Income,
            CashTransactionSource.CashIn,
            amountLocal: 250m,
            amountUsd: 5m,
            exchangeRate: Rate,
            description: "Ingreso USD",
            paymentMethodId: UsdMethodId);

        await service.AddTransactionAsync(
            sessionId,
            CashTransactionType.Expense,
            CashTransactionSource.CashOut,
            amountLocal: 100m,
            amountUsd: 2m,
            exchangeRate: Rate,
            description: "Retiro USD",
            paymentMethodId: UsdMethodId);

        var usd = await BalanceForAsync(service, sessionId, UsdMethodId);
        var local = await BalanceForAsync(service, sessionId, LocalMethodId);

        Assert.Equal(23m, usd.CurrentBalance);
        Assert.Equal(1000m, local.CurrentBalance);
    }

    [Fact]
    public async Task CashAdvancePhysicalExpenseOnUsdMethod_ChangesOnlyThatMethod()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var (service, sessionId) = await OpenDrawerAsync(context);

        context.CashTransactions.Add(PhysicalMovement(
            sessionId,
            UsdMethodId,
            CashTransactionType.Expense,
            CashTransactionSource.CashAdvance,
            amountLocal: 150m,
            amountUsd: 3m,
            currencyCode: "USD",
            description: "Adelanto de efectivo"));
        await context.SaveChangesAsync();

        var usd = await BalanceForAsync(service, sessionId, UsdMethodId);
        var local = await BalanceForAsync(service, sessionId, LocalMethodId);

        Assert.Equal(17m, usd.CurrentBalance);
        Assert.Equal(1000m, local.CurrentBalance);
    }

    [Fact]
    public async Task LegacyAggregates_EqualTheSumOfMethodBalancesByCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var (service, sessionId) = await OpenDrawerAsync(context);

        await service.AddTransactionAsync(
            sessionId,
            CashTransactionType.Income,
            CashTransactionSource.SalePayment,
            amountLocal: 500m,
            amountUsd: 10m,
            exchangeRate: Rate,
            description: "Venta efectivo USD",
            paymentMethodId: UsdMethodId);

        await service.AddTransactionAsync(
            sessionId,
            CashTransactionType.Income,
            CashTransactionSource.SalePayment,
            amountLocal: 500m,
            amountUsd: 10m,
            exchangeRate: Rate,
            description: "Venta efectivo Bs.S",
            paymentMethodId: LocalMethodId);

        await service.RecordSaleChangeAsync(
            sessionId,
            changeUsd: 2m,
            changeBsS: 100m,
            exchangeRate: Rate,
            description: "Vuelto",
            saleId: 1,
            cashPaymentMethodId: LocalMethodId);
        await context.SaveChangesAsync();

        context.CashTransactions.AddRange(
            PhysicalMovement(
                sessionId,
                LocalMethodId,
                CashTransactionType.Expense,
                CashTransactionSource.PettyCashExpense,
                amountLocal: 30m,
                amountUsd: 0.6m,
                currencyCode: "Bs.S",
                description: "Caja chica"),
            PhysicalMovement(
                sessionId,
                LocalMethodId,
                CashTransactionType.Income,
                CashTransactionSource.PettyCashReversal,
                amountLocal: 30m,
                amountUsd: 0.6m,
                currencyCode: "Bs.S",
                description: "Reversa caja chica"),
            PhysicalMovement(
                sessionId,
                UsdMethodId,
                CashTransactionType.Expense,
                CashTransactionSource.CashAdvance,
                amountLocal: 150m,
                amountUsd: 3m,
                currencyCode: "USD",
                description: "Adelanto de efectivo"));
        await context.SaveChangesAsync();

        await service.AddTransactionAsync(
            sessionId,
            CashTransactionType.Income,
            CashTransactionSource.CashIn,
            amountLocal: 250m,
            amountUsd: 5m,
            exchangeRate: Rate,
            description: "Ingreso USD",
            paymentMethodId: UsdMethodId);

        await service.AddTransactionAsync(
            sessionId,
            CashTransactionType.Expense,
            CashTransactionSource.CashOut,
            amountLocal: 100m,
            amountUsd: 2m,
            exchangeRate: Rate,
            description: "Retiro USD",
            paymentMethodId: UsdMethodId);

        var balances = await service.GetPhysicalMethodBalancesAsync(sessionId);
        decimal usdSum = balances
            .Where(balance => PaymentMethodCurrencyResolver.Normalize(balance.CurrencyCode) == PaymentMethodCurrencyResolver.Usd)
            .Sum(balance => balance.CurrentBalance);
        decimal localSum = balances
            .Where(balance => PaymentMethodCurrencyResolver.Normalize(balance.CurrencyCode) == PaymentMethodCurrencyResolver.LocalCurrency)
            .Sum(balance => balance.CurrentBalance);

        Assert.Equal(30m, usdSum);
        Assert.Equal(1400m, localSum);
        Assert.Equal(usdSum, await service.GetPhysicalBalanceAsync(sessionId, PettyCashCurrency.Usd));
        Assert.Equal(localSum, await service.GetPhysicalBalanceAsync(sessionId, PettyCashCurrency.BsS));
        Assert.Equal(localSum, await service.GetCurrentBalanceLocalAsync(sessionId));
    }

    [Fact]
    public async Task CanDispense_IsFalseAtZeroAndForInactiveMethod_ButTheRowRemains()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var (service, sessionId) = await OpenDrawerAsync(context, openingLocal: 100m, openingUsd: 20m);

        await service.AddTransactionAsync(
            sessionId,
            CashTransactionType.Expense,
            CashTransactionSource.CashOut,
            amountLocal: 100m,
            amountUsd: 2m,
            exchangeRate: Rate,
            description: "Retiro total local",
            paymentMethodId: LocalMethodId);

        var zeroBalance = await BalanceForAsync(service, sessionId, LocalMethodId);
        Assert.Equal(0m, zeroBalance.CurrentBalance);
        Assert.True(zeroBalance.IsActive);
        Assert.False(zeroBalance.CanDispense);

        var method = await context.PaymentMethods.SingleAsync(candidate => candidate.Id == UsdMethodId);
        method.IsActive = false;
        await context.SaveChangesAsync();

        var inactive = await BalanceForAsync(service, sessionId, UsdMethodId);
        Assert.Equal(20m, inactive.CurrentBalance);
        Assert.False(inactive.IsActive);
        Assert.False(inactive.CanDispense);
        Assert.Equal(2, (await service.GetPhysicalMethodBalancesAsync(sessionId)).Count);
    }

    [Fact]
    public async Task PhysicalMethodBalances_FailClosed_WhenTheMonetaryProfileCannotBeRead()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var (_, sessionId) = await OpenDrawerAsync(context);

        var failingProfile = new Mock<IMonetaryProfileService>();
        failingProfile
            .Setup(profile => profile.GetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("settings unavailable"));
        var failingService = new CashDrawerService(context, failingProfile.Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingService.GetPhysicalMethodBalancesAsync(sessionId));

        Assert.Contains("perfil monetario", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
