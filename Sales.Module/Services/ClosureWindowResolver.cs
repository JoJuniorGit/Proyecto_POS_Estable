using System;
using Core.Helpers;

namespace Sales.Module.Services;

public static class ClosureWindowResolver
{
    public static (DateTime StartUtc, DateTime EndExclusiveUtc) Resolve(
        DateTime dateUtc,
        DateTime? activeSessionOpenedAtUtc,
        DateTime? lastClosureDateUtc)
    {
        var venDate = TimeZoneHelper.GetVenezuelaDate(dateUtc);
        var tz = TimeZoneHelper.GetVenezuelaTimeZone();
        var startOfDayLocal = venDate.ToDateTime(TimeOnly.MinValue);
        var startOfDayUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(startOfDayLocal, DateTimeKind.Unspecified), tz);
        var endExclusiveUtc = startOfDayUtc.AddDays(1);

        var anchor = activeSessionOpenedAtUtc ?? lastClosureDateUtc ?? startOfDayUtc;

        if (anchor >= endExclusiveUtc)
        {
            anchor = (lastClosureDateUtc.HasValue && lastClosureDateUtc.Value > startOfDayUtc
                && lastClosureDateUtc.Value < endExclusiveUtc)
                ? lastClosureDateUtc.Value
                : startOfDayUtc;
        }

        return (anchor, endExclusiveUtc);
    }
}
