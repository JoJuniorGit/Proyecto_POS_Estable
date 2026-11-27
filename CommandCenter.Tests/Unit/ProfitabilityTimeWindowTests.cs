using Core.Helpers;
using Core.Interfaces;
using Sales.Module.DTOs.Profitability;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 14 (F2-S1): los cortes Hoy/Ayer/Semana/Mes y el rango personalizado se interpretan en la
/// Zona Horaria Global y se convierten a límites UTC estrictos [fromUtc, toUtc), sin funciones SQL.
/// </summary>
public class ProfitabilityTimeWindowTests
{
    private const string TimeZoneId = "Profitability/UTC-4";

    private static readonly TimeZoneInfo FixedTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        TimeZoneId,
        TimeSpan.FromHours(-4),
        "Profitability UTC-4",
        "Profitability UTC-4");

    private static readonly DateTime NowUtc = new(2026, 11, 11, 15, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ResolveAsync_Today_ReturnsLocalMidnightsAsStrictUtcWindow()
    {
        var result = await ResolveAsync(new ProfitabilityFilterDto
        {
            Preset = ProfitabilityPeriodPreset.Today
        });

        Assert.Equal(new DateTime(2026, 11, 11, 4, 0, 0, DateTimeKind.Utc), result.FromUtc);
        Assert.Equal(new DateTime(2026, 11, 12, 4, 0, 0, DateTimeKind.Utc), result.ToUtc);
        Assert.Equal(TimeZoneId, result.TimeZoneId);
        Assert.Equal(DateTimeKind.Utc, result.FromUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, result.ToUtc.Kind);
    }

    [Fact]
    public async Task ResolveAsync_Yesterday_ReturnsPreviousLocalDay()
    {
        var result = await ResolveAsync(new ProfitabilityFilterDto
        {
            Preset = ProfitabilityPeriodPreset.Yesterday
        });

        Assert.Equal(new DateTime(2026, 11, 10, 4, 0, 0, DateTimeKind.Utc), result.FromUtc);
        Assert.Equal(new DateTime(2026, 11, 11, 4, 0, 0, DateTimeKind.Utc), result.ToUtc);
    }

    [Fact]
    public async Task ResolveAsync_Week_StartsOnLocalMonday()
    {
        var result = await ResolveAsync(new ProfitabilityFilterDto
        {
            Preset = ProfitabilityPeriodPreset.Week
        });

        Assert.Equal(new DateTime(2026, 11, 9, 4, 0, 0, DateTimeKind.Utc), result.FromUtc);
        Assert.Equal(new DateTime(2026, 11, 16, 4, 0, 0, DateTimeKind.Utc), result.ToUtc);
    }

    [Fact]
    public async Task ResolveAsync_Month_StartsOnFirstLocalDayAndEndsOnNextMonth()
    {
        var result = await ResolveAsync(new ProfitabilityFilterDto
        {
            Preset = ProfitabilityPeriodPreset.Month
        });

        Assert.Equal(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc), result.FromUtc);
        Assert.Equal(new DateTime(2026, 12, 1, 4, 0, 0, DateTimeKind.Utc), result.ToUtc);
    }

    [Fact]
    public async Task ResolveAsync_Custom_ToDateIsInclusiveAndEndsOnNextLocalMidnight()
    {
        var result = await ResolveAsync(new ProfitabilityFilterDto
        {
            Preset = ProfitabilityPeriodPreset.Custom,
            FromDate = new DateOnly(2026, 11, 1),
            ToDate = new DateOnly(2026, 11, 3)
        });

        Assert.Equal(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc), result.FromUtc);
        Assert.Equal(new DateTime(2026, 11, 4, 4, 0, 0, DateTimeKind.Utc), result.ToUtc);
    }

    [Fact]
    public async Task ResolveAsync_CustomWithOnlyToDate_UsesThatSingleDay()
    {
        var result = await ResolveAsync(new ProfitabilityFilterDto
        {
            Preset = ProfitabilityPeriodPreset.Custom,
            ToDate = new DateOnly(2026, 11, 3)
        });

        Assert.Equal(new DateTime(2026, 11, 3, 4, 0, 0, DateTimeKind.Utc), result.FromUtc);
        Assert.Equal(new DateTime(2026, 11, 4, 4, 0, 0, DateTimeKind.Utc), result.ToUtc);
    }

    [Fact]
    public async Task ResolveAsync_CustomWithOnlyFromDate_UsesThatSingleDay()
    {
        var result = await ResolveAsync(new ProfitabilityFilterDto
        {
            Preset = ProfitabilityPeriodPreset.Custom,
            FromDate = new DateOnly(2026, 11, 3)
        });

        Assert.Equal(new DateTime(2026, 11, 3, 4, 0, 0, DateTimeKind.Utc), result.FromUtc);
        Assert.Equal(new DateTime(2026, 11, 4, 4, 0, 0, DateTimeKind.Utc), result.ToUtc);
    }

    [Fact]
    public async Task ResolveAsync_CustomWithoutDates_FallsBackToToday()
    {
        var result = await ResolveAsync(new ProfitabilityFilterDto
        {
            Preset = ProfitabilityPeriodPreset.Custom
        });

        Assert.Equal(new DateTime(2026, 11, 11, 4, 0, 0, DateTimeKind.Utc), result.FromUtc);
        Assert.Equal(new DateTime(2026, 11, 12, 4, 0, 0, DateTimeKind.Utc), result.ToUtc);
    }

    [Fact]
    public async Task ResolveAsync_CustomWithFromAfterTo_ThrowsArgumentException()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => ResolveAsync(new ProfitabilityFilterDto
        {
            Preset = ProfitabilityPeriodPreset.Custom,
            FromDate = new DateOnly(2026, 11, 5),
            ToDate = new DateOnly(2026, 11, 4)
        }));

        Assert.Equal("La fecha desde no puede ser posterior a la fecha hasta.", exception.Message);
    }

    [Fact]
    public async Task ResolveAsync_TodayAcrossDstStart_Returns23HourLocalDay()
    {
        var newYork = GetRequiredTimeZone("America/New_York", "Eastern Standard Time");

        var result = await ProfitabilityTimeWindow.ResolveAsync(
            new ProfitabilityFilterDto { Preset = ProfitabilityPeriodPreset.Today },
            new StubTimeZoneProvider(newYork, "America/New_York"),
            new DateTime(2026, 3, 8, 12, 0, 0, DateTimeKind.Utc));

        // 2026-03-08 is the US DST start: local midnight is EST (UTC-5) and the next local
        // midnight is EDT (UTC-4), so the local day lasts 23 hours.
        Assert.Equal(new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc), result.FromUtc);
        Assert.Equal(new DateTime(2026, 3, 9, 4, 0, 0, DateTimeKind.Utc), result.ToUtc);
        Assert.Equal(TimeSpan.FromHours(23), result.ToUtc - result.FromUtc);
        Assert.Equal(newYork.Id, result.TimeZoneId);
    }

    [Fact]
    public async Task ResolveAsync_InvalidConfiguredTimeZoneId_ReportsEffectiveFallbackZone()
    {
        var fallbackZone = TimeZoneHelper.GetVenezuelaTimeZone();

        var result = await ProfitabilityTimeWindow.ResolveAsync(
            new ProfitabilityFilterDto { Preset = ProfitabilityPeriodPreset.Today },
            new StubTimeZoneProvider(fallbackZone, "Foo/Bar"),
            NowUtc);

        Assert.Equal(fallbackZone.Id, result.TimeZoneId);
    }

    private static Task<ProfitabilityTimeWindowResult> ResolveAsync(ProfitabilityFilterDto filter)
        => ProfitabilityTimeWindow.ResolveAsync(
            filter,
            new StubTimeZoneProvider(FixedTimeZone, TimeZoneId),
            NowUtc);

    private static TimeZoneInfo GetRequiredTimeZone(params string[] timeZoneIds)
    {
        foreach (var timeZoneId in timeZoneIds)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
                // Try the next platform-specific identifier.
            }
            catch (InvalidTimeZoneException)
            {
                // Try the next platform-specific identifier.
            }
        }

        throw new InvalidOperationException(
            $"This test requires one of these time zones, but none is available on this host: {string.Join(", ", timeZoneIds)}.");
    }

    private sealed class StubTimeZoneProvider : ITimeZoneProvider
    {
        private readonly TimeZoneInfo _timeZone;
        private readonly string _timeZoneId;

        public StubTimeZoneProvider(TimeZoneInfo timeZone, string timeZoneId)
        {
            _timeZone = timeZone;
            _timeZoneId = timeZoneId;
        }

        public Task<TimeZoneInfo> GetTimeZoneInfoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_timeZone);

        public Task<string> GetTimeZoneIdAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_timeZoneId);

        public void Invalidate()
        {
        }
    }
}
