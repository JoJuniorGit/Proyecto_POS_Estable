using System;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ClosureWindowResolverTests
{
    private static readonly TimeZoneInfo VetTz = Core.Helpers.TimeZoneHelper.GetVenezuelaTimeZone();

    private static DateTime ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), VetTz);
    }

    private static DateTime VenezuelaDayStartUtc(DateOnly date)
    {
        return ToUtc(date, TimeOnly.MinValue);
    }

    private static DateTime VenezuelaDayEndExclusiveUtc(DateOnly date)
    {
        return VenezuelaDayStartUtc(date).AddDays(1);
    }

    [Fact]
    public void Resolve_WhenSessionOpen_SessionOpensAtAnchorsWindow()
    {
        var queryDate = ToUtc(new DateOnly(2026, 9, 15), new TimeOnly(0, 15, 0));
        var sessionOpenedAt = ToUtc(new DateOnly(2026, 9, 14), new TimeOnly(20, 0, 0));
        var lastClosure = ToUtc(new DateOnly(2026, 9, 14), new TimeOnly(23, 0, 0));

        var (start, end) = ClosureWindowResolver.Resolve(queryDate, sessionOpenedAt, lastClosure);

        Assert.Equal(sessionOpenedAt, start);
        Assert.Equal(VenezuelaDayEndExclusiveUtc(new DateOnly(2026, 9, 15)), end);
    }

    [Fact]
    public void Resolve_WhenNoSession_FallsBackToLastClosure()
    {
        var queryDate = ToUtc(new DateOnly(2026, 9, 15), new TimeOnly(0, 15, 0));
        var lastClosure = ToUtc(new DateOnly(2026, 9, 14), new TimeOnly(23, 0, 0));

        var (start, end) = ClosureWindowResolver.Resolve(queryDate, null, lastClosure);

        Assert.Equal(lastClosure, start);
        Assert.Equal(VenezuelaDayEndExclusiveUtc(new DateOnly(2026, 9, 15)), end);
    }

    [Fact]
    public void Resolve_WhenNoSessionNoClosure_FallsBackToStartOfDay()
    {
        var queryDate = ToUtc(new DateOnly(2026, 9, 15), new TimeOnly(12, 0, 0));

        var (start, end) = ClosureWindowResolver.Resolve(queryDate, null, null);

        Assert.Equal(VenezuelaDayStartUtc(new DateOnly(2026, 9, 15)), start);
        Assert.Equal(VenezuelaDayEndExclusiveUtc(new DateOnly(2026, 9, 15)), end);
        Assert.True(start < end, "Start must be strictly less than EndExclusiveUtc");
    }

    [Fact]
    public void Resolve_WhenSessionOpensAtDayStart_StartEqualsSessionOpenedAt()
    {
        var queryDate = ToUtc(new DateOnly(2026, 9, 15), new TimeOnly(12, 0, 0));
        var sessionOpenedAt = VenezuelaDayStartUtc(new DateOnly(2026, 9, 15));

        var (start, end) = ClosureWindowResolver.Resolve(queryDate, sessionOpenedAt, null);

        Assert.Equal(sessionOpenedAt, start);
        Assert.True(start < end, "Start must be strictly less than EndExclusiveUtc");
    }

    [Fact]
    public void Resolve_WhenBackdatedClosureBehindSession_FallsBackToLastClosure()
    {
        var queryDate = ToUtc(new DateOnly(2026, 9, 15), new TimeOnly(0, 15, 0));
        var sessionOpenedAt = ToUtc(new DateOnly(2026, 9, 16), new TimeOnly(6, 0, 0));
        var lastClosure = ToUtc(new DateOnly(2026, 9, 15), new TimeOnly(22, 0, 0));

        var (start, end) = ClosureWindowResolver.Resolve(queryDate, sessionOpenedAt, lastClosure);

        Assert.Equal(lastClosure, start);
        Assert.True(start < end, "Start must be strictly less than EndExclusiveUtc");
    }

    [Fact]
    public void Resolve_WhenBackdatedClosureNoLastClosure_FallsBackToStartOfDay()
    {
        var queryDate = ToUtc(new DateOnly(2026, 9, 15), new TimeOnly(0, 15, 0));
        var sessionOpenedAt = ToUtc(new DateOnly(2026, 9, 16), new TimeOnly(6, 0, 0));

        var (start, end) = ClosureWindowResolver.Resolve(queryDate, sessionOpenedAt, null);

        Assert.Equal(VenezuelaDayStartUtc(new DateOnly(2026, 9, 15)), start);
        Assert.True(start < end, "Start must be strictly less than EndExclusiveUtc");
    }

    [Fact]
    public void Resolve_WhenNightShiftCrossingMidnight_SalesFromPreviousDayAreIncluded()
    {
        var queryDate = ToUtc(new DateOnly(2026, 9, 15), new TimeOnly(0, 15, 0));
        var sessionOpenedAt = ToUtc(new DateOnly(2026, 9, 14), new TimeOnly(18, 0, 0));
        var lastClosure = ToUtc(new DateOnly(2026, 9, 14), new TimeOnly(23, 0, 0));

        var (start, end) = ClosureWindowResolver.Resolve(queryDate, sessionOpenedAt, lastClosure);

        Assert.Equal(sessionOpenedAt, start);
        Assert.Equal(VenezuelaDayEndExclusiveUtc(new DateOnly(2026, 9, 15)), end);

        var saleAt23 = ToUtc(new DateOnly(2026, 9, 14), new TimeOnly(23, 30, 0));
        Assert.True(saleAt23 >= start, "Sale at 23:30 must be included (>= StartUtc)");
        Assert.True(saleAt23 < end, "Sale at 23:30 must be included (< EndExclusiveUtc)");
    }
}
