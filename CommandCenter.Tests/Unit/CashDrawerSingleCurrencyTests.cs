using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T3c3b1 (S4/S6): when the monetary profile is Single, local drawer rows snapshot the
/// configured primary currency instead of the legacy Bs.S constant. Amounts, balances and the
/// persisted-method-currency branches stay unchanged; Dual/null profiles keep Bs.S.
/// </summary>
public class CashDrawerSingleCurrencyTests
{
    private static Mock<IMonetaryProfileService> CreateSingleProfileMock(string primaryCode = "COP")
    {
        var profile = new Mock<IMonetaryProfileService>();
        profile
            .Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = primaryCode,
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = null,
                SecondaryCurrencySymbol = null,
                IsConfigured = true
            });
        return profile;
    }

    private static Mock<IMonetaryProfileService> CreateDualProfileMock()
    {
        var profile = new Mock<IMonetaryProfileService>();
        profile
            .Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
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

    [Fact]
    public async Task OpeningAndClosing_SingleProfile_SnapshotPrimaryCurrencyAndKeepAmounts()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var profile = CreateSingleProfileMock();
        var service = new CashDrawerService(context, profile.Object);

        var opened = await service.OpenSessionAsync(500m, 50m);
        var closed = await service.CloseSessionAsync(500m, 50m);

        var transactions = await context.CashTransactions.AsNoTracking().OrderBy(t => t.Id).ToListAsync();

        Assert.Equal(2, transactions.Count);
        Assert.Equal(CashTransactionSource.Opening, transactions[0].Source);
        Assert.Equal("COP", transactions[0].CurrencyCode);
        Assert.Equal(500m, transactions[0].AmountLocal);
        Assert.Equal(CashTransactionSource.Closing, transactions[1].Source);
        Assert.Equal("COP", transactions[1].CurrencyCode);
        Assert.Equal(500m, transactions[1].AmountLocal);
        Assert.Equal(500m, closed.ClosingBalanceLocal);
        Assert.Equal(opened.Id, closed.Id);
        profile.Verify(p => p.GetAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task ManualCashInCashOutAndSaleChange_SingleProfile_SnapshotPrimaryCurrencyAndKeepAmounts()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new CashDrawerService(context, CreateSingleProfileMock().Object);
        var session = await service.OpenSessionAsync(1000m, 50m);

        var cashIn = await service.AddTransactionAsync(
            session.Id, CashTransactionType.Income, CashTransactionSource.CashIn, 100m, 2m, 50m, "Ingreso manual");
        var cashOut = await service.AddTransactionAsync(
            session.Id, CashTransactionType.Expense, CashTransactionSource.CashOut, 50m, 1m, 50m, "Retiro manual");
        var saleChange = await service.AddTransactionAsync(
            session.Id, CashTransactionType.Expense, CashTransactionSource.SalePayment, 20m, 0.4m, 50m, "Vuelto directo");
        var recordedChange = await service.RecordSaleChangeAsync(
            session.Id, changeUsd: 2m, changeBsS: 100m, exchangeRate: 50m, "Vuelto", saleId: 1, cashPaymentMethodId: null);

        Assert.Equal("COP", cashIn.CurrencyCode);
        Assert.Equal("COP", cashOut.CurrencyCode);
        Assert.Equal("COP", saleChange.CurrencyCode);
        Assert.Equal("COP", recordedChange.CurrencyCode);
        Assert.Equal(100m, cashIn.AmountLocal);
        Assert.Equal(50m, cashOut.AmountLocal);
        Assert.Equal(20m, saleChange.AmountLocal);
        Assert.Equal(100m, recordedChange.AmountLocal);
        Assert.Equal(1030m, await service.GetCurrentBalanceLocalAsync(session.Id));

        await context.SaveChangesAsync();

        var persistedRecordedChange = await context.CashTransactions.AsNoTracking()
            .SingleAsync(t => t.Source == CashTransactionSource.SalePayment
                && t.Type == CashTransactionType.Expense
                && t.Description == "Vuelto");
        Assert.Equal("COP", persistedRecordedChange.CurrencyCode);
        Assert.Equal(100m, persistedRecordedChange.AmountLocal);
        Assert.Equal(2m, persistedRecordedChange.AmountUsd);
        Assert.Equal(50m, persistedRecordedChange.ExchangeRate);
    }

    [Fact]
    public async Task LocalDrawerRows_DualProfile_SnapshotBsS()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new CashDrawerService(context, CreateDualProfileMock().Object);
        var session = await service.OpenSessionAsync(1000m, 50m);

        var cashIn = await service.AddTransactionAsync(
            session.Id, CashTransactionType.Income, CashTransactionSource.CashIn, 100m, 2m, 50m, "Ingreso manual");
        var cashOut = await service.AddTransactionAsync(
            session.Id, CashTransactionType.Expense, CashTransactionSource.CashOut, 50m, 1m, 50m, "Retiro manual");
        var recordedChange = await service.RecordSaleChangeAsync(
            session.Id, changeUsd: 2m, changeBsS: 100m, exchangeRate: 50m, "Vuelto", saleId: 1, cashPaymentMethodId: null);
        var closed = await service.CloseSessionAsync(1000m, 50m);

        Assert.Equal("Bs.S", cashIn.CurrencyCode);
        Assert.Equal("Bs.S", cashOut.CurrencyCode);
        Assert.Equal("Bs.S", recordedChange.CurrencyCode);

        var opening = await context.CashTransactions.AsNoTracking()
            .SingleAsync(t => t.Source == CashTransactionSource.Opening);
        var closing = await context.CashTransactions.AsNoTracking()
            .SingleAsync(t => t.Source == CashTransactionSource.Closing);
        Assert.Equal("Bs.S", opening.CurrencyCode);
        Assert.Equal("Bs.S", closing.CurrencyCode);
        Assert.Equal(1000m, closed.ClosingBalanceLocal);
    }

    [Fact]
    public async Task LocalDrawerRows_NullProfileService_KeepBsS()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new CashDrawerService(context);
        var session = await service.OpenSessionAsync(1000m, 50m);

        var cashIn = await service.AddTransactionAsync(
            session.Id, CashTransactionType.Income, CashTransactionSource.CashIn, 100m, 2m, 50m, "Ingreso manual");

        Assert.Equal("Bs.S", cashIn.CurrencyCode);
        var opening = await context.CashTransactions.AsNoTracking()
            .SingleAsync(t => t.Source == CashTransactionSource.Opening);
        Assert.Equal("Bs.S", opening.CurrencyCode);
    }

    [Fact]
    public async Task SalePaymentMovements_WithCopMethod_AreIncludedInLocalAndPhysicalBalance()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 70,
            Name = "Efectivo COP",
            IsCash = true,
            IsActive = true,
            Currency = "COP"
        });
        await context.SaveChangesAsync();

        var service = new CashDrawerService(context, CreateSingleProfileMock().Object);
        var session = await service.OpenSessionAsync(1000m, 1m);

        var income = await service.AddTransactionAsync(
            session.Id, CashTransactionType.Income, CashTransactionSource.SalePayment, 100m, 100m, 1m, "Venta COP", paymentMethodId: 70);
        var expense = await service.AddTransactionAsync(
            session.Id, CashTransactionType.Expense, CashTransactionSource.SalePayment, 20m, 20m, 1m, "Vuelto COP", paymentMethodId: 70);

        Assert.Equal("COP", income.CurrencyCode);
        Assert.Equal("COP", expense.CurrencyCode);
        Assert.Equal(1080m, await service.GetCurrentBalanceLocalAsync(session.Id));
        Assert.Equal(1080m, await service.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.BsS));
        Assert.Equal(0m, await service.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.Usd));
    }

    [Fact]
    public async Task DualProfile_CashInWithUsdMethod_SnapshotsAndBalancesUsd()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 80,
            Name = "Efectivo USD",
            IsCash = true,
            IsActive = true,
            Currency = "USD"
        });
        await context.SaveChangesAsync();

        var service = new CashDrawerService(context, CreateDualProfileMock().Object);
        var session = await service.OpenSessionAsync(100m, 50m);

        var income = await service.AddTransactionAsync(
            session.Id,
            CashTransactionType.Income,
            CashTransactionSource.CashIn,
            1028.39m,
            20.5678m,
            50m,
            "Fondo USD de caja chica",
            paymentMethodId: 80);

        Assert.Equal("USD", income.CurrencyCode);
        Assert.Equal(20.5678m, await service.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.Usd));
        Assert.Equal(100m, await service.GetCurrentBalanceLocalAsync(session.Id));
    }

    [Fact]
    public async Task ProfileReadFailure_FailsClosedWithoutDrawerWrites()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var profile = new Mock<IMonetaryProfileService>();
        profile
            .Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("settings store unavailable"));
        var service = new CashDrawerService(context, profile.Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.OpenSessionAsync(100m, 50m));

        Assert.Contains("perfil monetario", exception.Message, StringComparison.Ordinal);
        Assert.False(await context.CashDrawerSessions.AsNoTracking().AnyAsync());
        Assert.False(await context.CashTransactions.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task SalePaymentIncome_WithSingleProfile_KeepsPersistedMethodCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 60,
            Name = "Efectivo USD",
            IsCash = true,
            IsActive = true,
            Currency = "USD"
        });
        await context.SaveChangesAsync();
        var service = new CashDrawerService(context, CreateSingleProfileMock().Object);
        var session = await service.OpenSessionAsync(1000m, 50m);

        var income = await service.AddTransactionAsync(
            session.Id,
            CashTransactionType.Income,
            CashTransactionSource.SalePayment,
            500m,
            10m,
            50m,
            "Venta efectivo USD",
            paymentMethodId: 60);

        Assert.Equal("USD", income.CurrencyCode);
    }
}



