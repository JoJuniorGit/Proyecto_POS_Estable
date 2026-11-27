using Core.Interfaces;
using Sales.Module.DTOs.Profitability;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// Paso 14 (F2-S1): ventana UTC estricta [FromUtc, ToUtc) resuelta en la Zona Horaria Global.
/// </summary>
public sealed record ProfitabilityTimeWindowResult(DateTime FromUtc, DateTime ToUtc, string TimeZoneId);

/// <summary>
/// Paso 14 (F2-S1): traduce los presets de calendario local a límites UTC para comparar
/// directamente contra columnas indexadas (<c>&gt;= FromUtc</c> y <c>&lt; ToUtc</c>), sin
/// funciones SQL sobre las columnas.
/// </summary>
public static class ProfitabilityTimeWindow
{
    public static async Task<ProfitabilityTimeWindowResult> ResolveAsync(
        ProfitabilityFilterDto filter,
        ITimeZoneProvider timeZoneProvider,
        DateTime? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(timeZoneProvider);

        var timeZone = await timeZoneProvider.GetTimeZoneInfoAsync(cancellationToken);

        // F2-S1: the provider may fall back silently to the Venezuela zone when the configured
        // ID is invalid. The DTO must describe the zone actually used for the UTC conversion,
        // so report the effective TimeZoneInfo.Id instead of the raw configured value.
        var timeZoneId = timeZone.Id;

        var utcNow = nowUtc ?? DateTime.UtcNow;
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        }

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
        var today = DateOnly.FromDateTime(localNow);

        var (fromDate, toDateExclusive) = ResolveDateRange(filter, today);

        // The local midnights are Unspecified calendar instants: converting them through the
        // configured zone yields the exact UTC opening/closing of each local day, including DST.
        var fromUtc = ConvertLocalDateToUtc(fromDate, timeZone);
        var toUtc = ConvertLocalDateToUtc(toDateExclusive, timeZone);

        return new ProfitabilityTimeWindowResult(fromUtc, toUtc, timeZoneId);
    }

    private static (DateOnly FromDate, DateOnly ToDateExclusive) ResolveDateRange(
        ProfitabilityFilterDto filter,
        DateOnly today)
    {
        switch (filter.Preset)
        {
            case ProfitabilityPeriodPreset.Yesterday:
                return (today.AddDays(-1), today);

            case ProfitabilityPeriodPreset.Week:
            {
                var monday = StartOfLocalWeek(today);
                return (monday, monday.AddDays(7));
            }

            case ProfitabilityPeriodPreset.Month:
            {
                var firstDay = new DateOnly(today.Year, today.Month, 1);
                return (firstDay, firstDay.AddMonths(1));
            }

            case ProfitabilityPeriodPreset.Custom:
            {
                var fromDate = filter.FromDate;
                var toDate = filter.ToDate;

                if (fromDate is null && toDate is null)
                {
                    return (today, today.AddDays(1));
                }

                var effectiveFrom = fromDate ?? toDate!.Value;
                var effectiveTo = toDate ?? fromDate!.Value;
                if (effectiveFrom > effectiveTo)
                {
                    throw new ArgumentException("La fecha desde no puede ser posterior a la fecha hasta.");
                }

                return (effectiveFrom, effectiveTo.AddDays(1));
            }

            case ProfitabilityPeriodPreset.Today:
            default:
                return (today, today.AddDays(1));
        }
    }

    private static DateOnly StartOfLocalWeek(DateOnly date)
    {
        // DayOfWeek.Monday (1) maps to 0; Sunday (0) maps to 6.
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday);
    }

    private static DateTime ConvertLocalDateToUtc(DateOnly date, TimeZoneInfo timeZone)
    {
        var localMidnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localMidnight, timeZone);
    }
}
