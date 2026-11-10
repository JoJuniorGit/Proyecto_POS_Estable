using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
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
/// 8.211-T3c4 (S7/F3c4): the daily closure resolves the local drawer from the active profile.
/// A Dual USD/COP petty-cash expense deducts from the COP cash method (never the Bs.S one) and
/// the closure conversion uses the manual 8dp quote while persisting the 4dp snapshot. A Single
/// COP profile needs no Bs.S gaveta at all.
/// </summary>
public class DailyClosurePettyCashTests
{
    private const decimal ManualCopRate = 4000.12345678m;
    private static readonly TimeZoneInfo VetTz = Core.Helpers.TimeZoneHelper.GetVenezuelaTimeZone();

    private static DateTime ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), VetTz);
    }

    private static DateTime TodayMidnightUtc()
    {
        var venDate = Core.Helpers.TimeZoneHelper.GetVenezuelaDate(DateTime.UtcNow);
        return ToUtc(venDate, TimeOnly.MinValue);
    }

    private static Mock<IMonetaryProfileService> CreateProfile(
        MonetaryMode mode,
        string primaryCode,
        string? secondaryCode = null)
    {
        var profile = new Mock<IMonetaryProfileService>();
        profile
            .Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = mode,
                PrimaryCurrencyCode = primaryCode,
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = secondaryCode,
                SecondaryCurrencySymbol = secondaryCode,
                IsConfigured = true
            });
        return profile;
    }

    private static Mock<ICurrencyRateService> CreateManualRateMock(string secondaryCode, decimal rate)
    {
        var rates = new Mock<ICurrencyRateService>();
        rates
            .Setup(service => service.GetRateAsync("USD", secondaryCode, It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrencyRateQuote(
                rate,
                CurrencyRateSource.Manual,
                Core.Helpers.TimeZoneHelper.GetVenezuelaDate(),
                "USD",
                secondaryCode));
        return rates;
    }

    private static (DailyClosureService Service, Mock<ITodayExchangeRateProvider> RateProvider) CreateService(
        SalesDbContext context,
        IMonetaryProfileService? profile,
        ICurrencyRateService? currencyRateService,
        decimal bcvRate = 50m)
    {
        var rateProvider = new Mock<ITodayExchangeRateProvider>();
        rateProvider
            .Setup(provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(bcvRate);

        var cashDrawer = new Mock<ICashDrawerService>();
        return (
            new DailyClosureService(context, rateProvider.Object, cashDrawer.Object, null, profile, currencyRateService),
            rateProvider);
    }

    private static async Task SeedDualUsdCopMethodsAsync(SalesDbContext context)
    {
        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo USD", Currency = "USD", IsCash = true, IsActive = true, DisplayOrder = 1 },
            new PaymentMethod { Id = 2, Name = "Efectivo Bs.S", Currency = "Bs.S", IsCash = true, IsActive = true, DisplayOrder = 2 },
            new PaymentMethod { Id = 3, Name = "Punto de Venta", Currency = "COP", IsCash = false, IsActive = true, DisplayOrder = 3 },
            new PaymentMethod { Id = 4, Name = "Efectivo COP", Currency = "COP", IsCash = true, IsActive = true, DisplayOrder = 4 });
        await context.SaveChangesAsync();
    }

    private static async Task SeedSingleCopMethodsAsync(SalesDbContext context)
    {
        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo COP", Currency = "COP", IsCash = true, IsActive = true, DisplayOrder = 1 },
            new PaymentMethod { Id = 2, Name = "Punto de Venta", Currency = "COP", IsCash = false, IsActive = true, DisplayOrder = 2 });
        await context.SaveChangesAsync();
    }

    private static async Task<CashDrawerSession> SeedOpenSessionAsync(SalesDbContext context)
    {
        var session = new CashDrawerSession
        {
            OpenedAt = TodayMidnightUtc(),
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = 1000m,
            OpeningExchangeRate = 50m
        };
        context.CashDrawerSessions.Add(session);
        await context.SaveChangesAsync();
        return session;
    }

    private static int _saleSequence = 9600;

    private static async Task SeedSalePaymentAsync(
        SalesDbContext context,
        int paymentMethodId,
        decimal amountLocal,
        DateTime dateUtc)
    {
        int saleId = System.Threading.Interlocked.Increment(ref _saleSequence);
        context.Sales.Add(new Sale
        {
            Id = saleId,
            Status = SaleStatus.Completed,
            Date = dateUtc,
            TotalUSD = 0m,
            TotalBsS = amountLocal,
            AppliedRate = 1m
        });
        context.SalePayments.Add(new SalePayment
        {
            Id = saleId,
            SaleId = saleId,
            PaymentMethodId = paymentMethodId,
            AmountBsS = amountLocal,
            ExchangeRate = 1m,
            CreatedAt = dateUtc
        });
        await context.SaveChangesAsync();
    }

    private static async Task<PettyCashExpense> SeedExpenseAsync(
        SalesDbContext context,
        int sessionId,
        PettyCashCurrency currency,
        decimal amountLocal,
        decimal amountUsd,
        DateTime createdAtUtc)
    {
        var expense = new PettyCashExpense
        {
            VoucherNumber = 1,
            SessionId = sessionId,
            Category = PettyCashCategory.Varios,
            Currency = currency,
            AmountBsS = amountLocal,
            AmountUsd = amountUsd,
            HistoricalRate = 1m,
            Concept = "Compra operativa",
            Beneficiary = "Proveedor de prueba",
            ReferenceNumber = "REF-PC-001",
            CreatedByUserId = 1,
            CreatedAtUtc = createdAtUtc,
            Status = PettyCashStatus.Active
        };
        context.PettyCashExpenses.Add(expense);
        await context.SaveChangesAsync();
        return expense;
    }

    private static decimal ExpectedFor(List<ExpectedTotalDto> totals, int paymentMethodId)
        => totals.Single(total => total.PaymentMethodId == paymentMethodId).ExpectedAmountBsS;

    [Fact]
    public async Task DualUsdCop_DeductsPettyCashFromCopAndFreezesManualRateAt4dp()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedDualUsdCopMethodsAsync(context);
        var session = await SeedOpenSessionAsync(context);
        var now = DateTime.UtcNow;

        decimal usdLocalEquivalent = Math.Round(100m * ManualCopRate, 2, MidpointRounding.AwayFromZero);
        await SeedSalePaymentAsync(context, paymentMethodId: 4, amountLocal: 5000m, dateUtc: now);
        await SeedSalePaymentAsync(context, paymentMethodId: 1, amountLocal: usdLocalEquivalent, dateUtc: now);
        await SeedExpenseAsync(context, session.Id, PettyCashCurrency.BsS, 1000m, 0.25m, now);

        var profile = CreateProfile(MonetaryMode.Dual, "USD", "COP");
        var rates = CreateManualRateMock("COP", ManualCopRate);
        var (service, rateProvider) = CreateService(context, profile.Object, rates.Object);

        var totals = await service.GetExpectedTotalsByPaymentMethodAsync(now);

        Assert.Equal(4000m, ExpectedFor(totals, 4));
        Assert.Equal("COP", totals.Single(total => total.PaymentMethodId == 4).PaymentMethodCurrency);
        Assert.Equal(usdLocalEquivalent, ExpectedFor(totals, 1));
        Assert.Equal(0m, ExpectedFor(totals, 2));

        var command = new CreateClosureCommand(
            now,
            "Admin",
            null,
            new List<DeclaredPaymentAmount>
            {
                new(1, 100m),
                new(4, 4000m)
            });

        var result = await service.CreateClosureFromCommandAsync(command, CancellationToken.None);

        var saved = await context.DailyClosures.AsNoTracking()
            .Include(closure => closure.Details)
            .SingleAsync(closure => closure.Id == result.ClosureId);

        Assert.Equal(4000.1235m, saved.ExchangeRate);
        Assert.Equal(4000.1235m, result.ExchangeRate);
        Assert.Equal(4000m, saved.Details.Single(detail => detail.PaymentMethodId == 4).ExpectedAmountBsS);
        Assert.Equal(usdLocalEquivalent, saved.Details.Single(detail => detail.PaymentMethodId == 1).ExpectedAmountBsS);
        // Declared USD uses the 8dp calculation rate; the 4dp snapshot only feeds the persisted rate.
        Assert.Equal(100m * ManualCopRate, saved.Details.Single(detail => detail.PaymentMethodId == 1).ActualAmountBsS);
        Assert.Equal(0m, saved.Details.Single(detail => detail.PaymentMethodId == 2).ExpectedAmountBsS);

        rates.Verify(
            service => service.GetRateAsync("USD", "COP", It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        rateProvider.Verify(
            provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SingleCop_DeductsPettyCashFromCopWithoutRequiringABsSMethod()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedSingleCopMethodsAsync(context);
        var session = await SeedOpenSessionAsync(context);
        var now = DateTime.UtcNow;

        await SeedSalePaymentAsync(context, paymentMethodId: 1, amountLocal: 1000m, dateUtc: now);
        await SeedExpenseAsync(context, session.Id, PettyCashCurrency.BsS, 300m, 300m, now);

        var profile = CreateProfile(MonetaryMode.Single, "COP");
        var (service, rateProvider) = CreateService(context, profile.Object, currencyRateService: null);

        var totals = await service.GetExpectedTotalsByPaymentMethodAsync(now);

        Assert.Equal(700m, ExpectedFor(totals, 1));
        Assert.DoesNotContain(totals, total => total.PaymentMethodCurrency == "Bs.S");

        var command = new CreateClosureCommand(
            now,
            "Admin",
            null,
            new List<DeclaredPaymentAmount> { new(1, 700m) });

        var result = await service.CreateClosureFromCommandAsync(command, CancellationToken.None);

        var saved = await context.DailyClosures.AsNoTracking()
            .Include(closure => closure.Details)
            .SingleAsync(closure => closure.Id == result.ClosureId);

        Assert.Equal(1m, saved.ExchangeRate);
        Assert.Equal(1m, result.ExchangeRate);
        var cashDetail = saved.Details.Single(detail => detail.PaymentMethodId == 1);
        Assert.Equal(700m, cashDetail.ExpectedAmountBsS);
        Assert.Equal(700m, cashDetail.ActualAmountBsS);
        Assert.DoesNotContain(saved.Details, detail => detail.PaymentMethodId == 2 && detail.ExpectedAmountBsS != 0m);
        rateProvider.Verify(
            provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
