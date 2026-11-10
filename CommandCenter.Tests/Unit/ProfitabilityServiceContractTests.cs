using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs.Profitability;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 14 (F2-WU1, F2-S1/F2-S4): contrato inicial de ProfitabilityService. El esqueleto resuelve
/// la ventana UTC y la moneda de reporte, expone la nota de costos globales y devuelve los bloques
/// en cero hasta que los motores devengado/percibido lleguen en WU2/WU3.
/// </summary>
public class ProfitabilityServiceContractTests
{
    private const string TimeZoneId = "Profitability/UTC-4";
    private const string GlobalCostsNote =
        "PettyCashExpenses, ServiceCommissions y PaymentMethodFees son gastos/ingresos globales de operación y no se prorratean artificialmente por artículo.";

    private static readonly TimeZoneInfo FixedTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        TimeZoneId,
        TimeSpan.FromHours(-4),
        "Profitability UTC-4",
        "Profitability UTC-4");

    [Fact]
    public async Task GetSummaryAsync_DualProfile_ReportsUsdAndZeroedBlocks()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = CreateService(context, new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "USD",
            PrimaryCurrencySymbol = "$",
            SecondaryCurrencyCode = "VES",
            SecondaryCurrencySymbol = "Bs.S"
        });

        var summary = await service.GetSummaryAsync(new ProfitabilityFilterDto
        {
            Preset = ProfitabilityPeriodPreset.Today
        });

        Assert.Equal(MonetaryMode.Dual, summary.MonetaryMode);
        Assert.Equal("USD", summary.ReportCurrencyCode);
        Assert.Equal("$", summary.ReportCurrencySymbol);
        Assert.False(summary.IsSingleCurrency);
        Assert.Equal(TimeZoneId, summary.TimeZoneId);
        Assert.Equal(DateTimeKind.Utc, summary.FromUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, summary.ToUtc.Kind);
        Assert.Equal(TimeSpan.FromHours(24), summary.ToUtc - summary.FromUtc);
        Assert.Equal(GlobalCostsNote, summary.GlobalCostsNote);
        Assert.Equal(DateTimeKind.Utc, summary.GeneratedAtUtc.Kind);
        AssertZeroed(summary);
    }

    [Fact]
    public async Task GetSummaryAsync_SingleCopProfile_ReportsPrimaryCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = CreateService(context, new MonetaryProfileDto
        {
            Mode = MonetaryMode.Single,
            PrimaryCurrencyCode = "COP",
            PrimaryCurrencySymbol = "COL$",
            SecondaryCurrencyCode = null,
            SecondaryCurrencySymbol = null
        });

        var summary = await service.GetSummaryAsync(new ProfitabilityFilterDto
        {
            Preset = ProfitabilityPeriodPreset.Today
        });

        Assert.Equal(MonetaryMode.Single, summary.MonetaryMode);
        Assert.Equal("COP", summary.ReportCurrencyCode);
        Assert.Equal("COL$", summary.ReportCurrencySymbol);
        Assert.True(summary.IsSingleCurrency);
        Assert.Equal(GlobalCostsNote, summary.GlobalCostsNote);
        AssertZeroed(summary);
    }

    [Fact]
    public async Task GetSummaryAsync_WithoutMonetaryProfileService_FallsBackToDualUsd()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new ProfitabilityService(context, new StubTimeZoneProvider());

        var summary = await service.GetSummaryAsync(new ProfitabilityFilterDto
        {
            Preset = ProfitabilityPeriodPreset.Today
        });

        Assert.Equal(MonetaryMode.Dual, summary.MonetaryMode);
        Assert.Equal("USD", summary.ReportCurrencyCode);
        Assert.Equal("$", summary.ReportCurrencySymbol);
        Assert.False(summary.IsSingleCurrency);
        AssertZeroed(summary);
    }

    [Fact]
    public async Task GetSummaryAsync_CustomRange_UsesInclusiveToDateWindow()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = CreateService(context, new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "USD",
            PrimaryCurrencySymbol = "$",
            SecondaryCurrencyCode = "VES",
            SecondaryCurrencySymbol = "Bs.S"
        });

        var summary = await service.GetSummaryAsync(new ProfitabilityFilterDto
        {
            Preset = ProfitabilityPeriodPreset.Custom,
            FromDate = new DateOnly(2026, 11, 1),
            ToDate = new DateOnly(2026, 11, 3)
        });

        Assert.Equal(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc), summary.FromUtc);
        Assert.Equal(new DateTime(2026, 11, 4, 4, 0, 0, DateTimeKind.Utc), summary.ToUtc);
    }

    private static ProfitabilityService CreateService(SalesDbContext context, MonetaryProfileDto profile)
    {
        var profileService = new Mock<IMonetaryProfileService>();
        profileService
            .Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        return new ProfitabilityService(
            context,
            new StubTimeZoneProvider(),
            profileService.Object);
    }

    private static void AssertZeroed(ProfitabilitySummaryDto summary)
    {
        Assert.Equal(0m, summary.Accrued.GrossSales);
        Assert.Equal(0m, summary.Accrued.NetSales);
        Assert.Equal(0m, summary.Accrued.Cogs);
        Assert.Equal(0m, summary.Accrued.NetProfit);
        Assert.Equal(0m, summary.Accrued.NetMarginPercent);

        Assert.Equal(0m, summary.Realized.CollectedRevenue);
        Assert.Equal(0m, summary.Realized.RealizedCogs);
        Assert.Equal(0m, summary.Realized.NetProfit);
        Assert.Equal(0m, summary.Realized.NetMarginPercent);

        Assert.Equal(0m, summary.Coverage.CostCoveragePercent);
        Assert.Equal(0m, summary.Coverage.FeeCoveragePercent);

        Assert.Empty(summary.Breakdown.Series);
        Assert.Empty(summary.Breakdown.PaymentMethods);
        Assert.Empty(summary.Breakdown.Categories);
        Assert.Empty(summary.Breakdown.Products);
        Assert.Empty(summary.Breakdown.Sellers);
        Assert.Empty(summary.Alerts);
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
