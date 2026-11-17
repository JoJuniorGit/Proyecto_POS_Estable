using System;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs.Profitability;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// P0 (T4/S3/S5): los gastos operativos mensuales se prorratean por dias calendario activos
/// dentro de la ventana local del reporte y se restan de la utilidad neta devengada y percibida.
/// Son gastos globales: no se podan por filtros de producto ni de medio de pago.
/// </summary>
public class ProfitabilityOperatingExpenseTests
{
    private const string TimeZoneId = "Profitability/UTC-4";

    private static readonly TimeZoneInfo FixedTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        TimeZoneId,
        TimeSpan.FromHours(-4),
        "Profitability UTC-4",
        "Profitability UTC-4");

    [Fact]
    public async Task GetSummaryAsync_Dual_ProratesTheMonthlyExpenseIntoAccruedAndRealized()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.OperatingExpenses.Add(CreateExpense(
            description: "Gastos mensuales del POS",
            monthlyAmountUsd: 41.12m,
            effectiveFromDate: new DateOnly(2026, 1, 1)));
        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(SingleDayFilter());

        // November 2026 has 30 days and the window covers exactly one local day.
        var expected = 41.12m * 1m / 30m;
        Assert.Equal(expected, summary.Accrued.OperatingExpenses);
        Assert.Equal(expected, summary.Realized.OperatingExpenses);
        Assert.Equal(-expected, summary.Accrued.NetProfit);
        Assert.Equal(-expected, summary.Realized.NetProfit);
        Assert.Equal(0m, summary.Accrued.NetMarginPercent);
        Assert.Equal(0m, summary.Realized.NetMarginPercent);
    }

    [Fact]
    public async Task GetSummaryAsync_AccruesAcrossCalendarMonthsUsingEachMonthDayCount()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.OperatingExpenses.Add(CreateExpense(
            description: "Gastos mensuales del POS",
            monthlyAmountUsd: 31m,
            effectiveFromDate: new DateOnly(2026, 1, 1)));
        await context.SaveChangesAsync();

        var filter = new ProfitabilityFilterDto
        {
            Preset = ProfitabilityPeriodPreset.Custom,
            // Local days 30-Jan, 31-Jan and 01-Feb 2026: 2 days of a 31-day month + 1 day of a
            // 28-day month.
            FromDate = new DateOnly(2026, 1, 30),
            ToDate = new DateOnly(2026, 2, 1)
        };

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(filter);

        var expected = 31m * 2m / 31m + 31m * 1m / 28m;
        Assert.Equal(expected, summary.Accrued.OperatingExpenses);
        Assert.Equal(expected, summary.Realized.OperatingExpenses);
    }

    [Fact]
    public async Task GetSummaryAsync_ExcludesInactiveExpensesAndRespectsTheEffectiveRange()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.OperatingExpenses.AddRange(
            CreateExpense(
                description: "Inactivo",
                monthlyAmountUsd: 100m,
                effectiveFromDate: new DateOnly(2026, 1, 1),
                isActive: false),
            CreateExpense(
                description: "Empieza despues",
                monthlyAmountUsd: 200m,
                effectiveFromDate: new DateOnly(2026, 12, 1)),
            CreateExpense(
                description: "Termino antes",
                monthlyAmountUsd: 300m,
                effectiveFromDate: new DateOnly(2026, 1, 1),
                effectiveToDate: new DateOnly(2026, 10, 31)));
        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(SingleDayFilter());

        Assert.Equal(0m, summary.Accrued.OperatingExpenses);
        Assert.Equal(0m, summary.Realized.OperatingExpenses);
    }

    [Fact]
    public async Task GetSummaryAsync_RespectsTheEffectiveToDateInsideTheWindow()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.OperatingExpenses.Add(CreateExpense(
            description: "Un solo dia",
            monthlyAmountUsd: 30m,
            effectiveFromDate: new DateOnly(2026, 11, 2),
            effectiveToDate: new DateOnly(2026, 11, 2)));
        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(SingleDayFilter());

        Assert.Equal(30m * 1m / 30m, summary.Accrued.OperatingExpenses);
    }

    [Fact]
    public async Task GetSummaryAsync_Single_UsesTheLocalAmountOnlyWhenTheCurrencyMatches()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.OperatingExpenses.AddRange(
            CreateExpense(
                description: "Coincide",
                monthlyAmountUsd: 1m,
                effectiveFromDate: new DateOnly(2026, 1, 1),
                monthlyAmountLocal: 300m,
                localCurrencyCode: "Bs.S"),
            CreateExpense(
                description: "No coincide",
                monthlyAmountUsd: 2m,
                effectiveFromDate: new DateOnly(2026, 1, 1),
                monthlyAmountLocal: 600m,
                localCurrencyCode: "COP"),
            CreateExpense(
                description: "Sin moneda local",
                monthlyAmountUsd: 3m,
                effectiveFromDate: new DateOnly(2026, 1, 1),
                monthlyAmountLocal: 900m,
                localCurrencyCode: null));
        await context.SaveChangesAsync();

        var matching = await CreateService(
            context, MonetaryMode.Single, primaryCurrencyCode: "VES").GetSummaryAsync(SingleDayFilter());
        Assert.Equal(300m * 1m / 30m, matching.Accrued.OperatingExpenses);
        Assert.Equal(300m * 1m / 30m, matching.Realized.OperatingExpenses);

        var mismatched = await CreateService(
            context, MonetaryMode.Single, primaryCurrencyCode: "COP").GetSummaryAsync(SingleDayFilter());
        Assert.Equal(600m * 1m / 30m, mismatched.Accrued.OperatingExpenses);
    }

    [Fact]
    public async Task GetSummaryAsync_ZeroesTheLocalAmountWhenTheReportCurrencyDoesNotMatch()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.OperatingExpenses.AddRange(
            CreateExpense(
                description: "Sin moneda local",
                monthlyAmountUsd: 3m,
                effectiveFromDate: new DateOnly(2026, 1, 1),
                monthlyAmountLocal: 900m,
                localCurrencyCode: null),
            CreateExpense(
                description: "Otra moneda",
                monthlyAmountUsd: 2m,
                effectiveFromDate: new DateOnly(2026, 1, 1),
                monthlyAmountLocal: 600m,
                localCurrencyCode: "COP"));
        await context.SaveChangesAsync();

        var summary = await CreateService(
            context, MonetaryMode.Single, primaryCurrencyCode: "USD").GetSummaryAsync(SingleDayFilter());

        Assert.Equal(0m, summary.Accrued.OperatingExpenses);
        Assert.Equal(0m, summary.Realized.OperatingExpenses);
    }

    [Fact]
    public async Task GetSummaryAsync_SeriesIncludesAnExpenseOnlyPeriod()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.OperatingExpenses.Add(CreateExpense(
            description: "Solo gasto",
            monthlyAmountUsd: 30m,
            effectiveFromDate: new DateOnly(2026, 1, 1)));
        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(SingleDayFilter());

        var point = Assert.Single(summary.Breakdown.Series);
        Assert.Equal(-1m, point.AccruedNetProfit);
        Assert.Equal(-1m, point.RealizedNetProfit);
        Assert.Equal(0m, point.AccruedNetSales);
        Assert.Equal(0m, point.RealizedCollectedRevenue);
    }

    [Fact]
    public async Task GetSummaryAsync_DoesNotProrateGlobalExpensesByProductOrPaymentMethodFilters()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.OperatingExpenses.Add(CreateExpense(
            description: "Gastos mensuales del POS",
            monthlyAmountUsd: 41.12m,
            effectiveFromDate: new DateOnly(2026, 1, 1)));
        await context.SaveChangesAsync();

        var filter = SingleDayFilter();
        filter.ProductIds = new[] { 999 };
        filter.PaymentMethodIds = new[] { 999 };

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(filter);

        Assert.Equal(41.12m * 1m / 30m, summary.Accrued.OperatingExpenses);
        Assert.Equal(41.12m * 1m / 30m, summary.Realized.OperatingExpenses);
    }

    private static OperatingExpense CreateExpense(
        string description,
        decimal monthlyAmountUsd,
        DateOnly effectiveFromDate,
        DateOnly? effectiveToDate = null,
        bool isActive = true,
        decimal? monthlyAmountLocal = null,
        string? localCurrencyCode = null) => new()
    {
        Description = description,
        MonthlyAmountUsd = monthlyAmountUsd,
        MonthlyAmountLocal = monthlyAmountLocal,
        LocalCurrencyCode = localCurrencyCode,
        EffectiveFromDate = effectiveFromDate,
        EffectiveToDate = effectiveToDate,
        IsActive = isActive
    };

    private static ProfitabilityFilterDto SingleDayFilter() => new()
    {
        Preset = ProfitabilityPeriodPreset.Custom,
        FromDate = new DateOnly(2026, 11, 2),
        ToDate = new DateOnly(2026, 11, 2)
    };

    private static ProfitabilityService CreateService(
        SalesDbContext context,
        MonetaryMode mode,
        string primaryCurrencyCode = "USD")
    {
        var profile = new MonetaryProfileDto
        {
            Mode = mode,
            PrimaryCurrencyCode = primaryCurrencyCode,
            PrimaryCurrencySymbol = primaryCurrencyCode == "USD" ? "$" : primaryCurrencyCode,
            SecondaryCurrencyCode = mode == MonetaryMode.Dual ? "VES" : null,
            SecondaryCurrencySymbol = mode == MonetaryMode.Dual ? "Bs.S" : null
        };

        var profileService = new Mock<IMonetaryProfileService>();
        profileService
            .Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        return new ProfitabilityService(context, new StubTimeZoneProvider(), profileService.Object);
    }

    private sealed class StubTimeZoneProvider : ITimeZoneProvider
    {
        public Task<TimeZoneInfo> GetTimeZoneInfoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(FixedTimeZone);

        public Task<string> GetTimeZoneIdAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(TimeZoneId);

        public void Invalidate()
        {
        }
    }
}
