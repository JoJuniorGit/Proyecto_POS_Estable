using System;

namespace Sales.Module.Helpers;

public static class TimeZoneHelper
{
    /// <summary>
    /// Retrieves a TimeZoneInfo object based on the given timezone ID.
    /// Falls back to the server's local machine time zone if the ID is missing, invalid, or obsolete.
    /// </summary>
    public static TimeZoneInfo GetTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return TimeZoneInfo.Local;
            
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch
        {
            // If the time zone ID cannot be found (e.g. invalid string or different OS), fallback securely.
            return TimeZoneInfo.Local;
        }
    }
}
