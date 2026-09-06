using System;

namespace Core.Helpers;

public static class TimeZoneHelper
{
    private static readonly Lazy<TimeZoneInfo> _venezuelaTimeZone = new(() =>
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Caracas");
        }
        catch
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Venezuela Standard Time");
            }
            catch
            {
                return TimeZoneInfo.CreateCustomTimeZone("VET", TimeSpan.FromHours(-4), "Venezuela Time", "Venezuela Standard Time");
            }
        }
    });

    /// <summary>
    /// Devuelve el objeto TimeZoneInfo correspondiente a la hora legal de Venezuela (UTC-4),
    /// resolviendo de forma multiplataforma 'America/Caracas' (IANA/Linux/.NET moderno) o 'Venezuela Standard Time' (Windows).
    /// </summary>
    public static TimeZoneInfo GetVenezuelaTimeZone() => _venezuelaTimeZone.Value;

    /// <summary>
    /// Obtiene la fecha DateOnly correspondiente a la jornada cambiaria/local de Venezuela
    /// a partir de un instante UTC (o DateTime.UtcNow por defecto).
    /// </summary>
    public static DateOnly GetVenezuelaDate(DateTime? utcDateTime = null)
    {
        var utc = utcDateTime ?? DateTime.UtcNow;
        if (utc.Kind != DateTimeKind.Utc)
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        }
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(utc, GetVenezuelaTimeZone());
        return DateOnly.FromDateTime(localTime);
    }

    /// <summary>
    /// Convierte una fecha y hora UTC a la hora local de Venezuela.
    /// </summary>
    public static DateTime ToVenezuelaTime(DateTime utcDateTime)
    {
        if (utcDateTime.Kind != DateTimeKind.Utc)
        {
            utcDateTime = DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
        }
        return TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, GetVenezuelaTimeZone());
    }

    /// <summary>
    /// Obtiene el objeto TimeZoneInfo correspondiente al identificador provisto.
    /// Si el identificador es nulo, vacío o no válido en el sistema, recurre de forma segura a la hora legal de Venezuela.
    /// </summary>
    public static TimeZoneInfo GetTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return GetVenezuelaTimeZone();

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch
        {
            return GetVenezuelaTimeZone();
        }
    }

    /// <summary>
    /// Calcula los límites inclusivo y exclusivo en UTC correspondientes a un rango de fechas calendario
    /// interpretadas en la zona horaria provista (por defecto, hora legal de Venezuela UTC-4).
    /// Si <paramref name="endDate"/> es nulo y <paramref name="defaultEndDateToToday"/> es verdadero,
    /// se asume la fecha de hoy en la zona horaria correspondiente para abarcar hasta el fin de la jornada actual.
    /// </summary>
    public static (DateTime? StartUtc, DateTime? EndExclusiveUtc) GetUtcRange(
        DateTime? startDate,
        DateTime? endDate,
        TimeZoneInfo? timeZone = null,
        bool defaultEndDateToToday = true)
    {
        var tz = timeZone ?? GetVenezuelaTimeZone();

        DateTime ToLocalCalendarDate(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Utc)
                return TimeZoneInfo.ConvertTimeFromUtc(dt, tz);
            if (dt.Kind == DateTimeKind.Local)
                return TimeZoneInfo.ConvertTime(dt, tz);
            return dt;
        }

        DateTime? startUtc = null;
        if (startDate.HasValue)
        {
            var local = ToLocalCalendarDate(startDate.Value);
            var localMidnight = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified);
            startUtc = TimeZoneInfo.ConvertTimeToUtc(localMidnight, tz);
        }

        DateTime? effectiveEnd = endDate;
        if (!effectiveEnd.HasValue && defaultEndDateToToday)
        {
            var today = GetVenezuelaDate();
            effectiveEnd = new DateTime(today.Year, today.Month, today.Day, 0, 0, 0, DateTimeKind.Unspecified);
        }

        DateTime? endExclusiveUtc = null;
        if (effectiveEnd.HasValue)
        {
            var local = ToLocalCalendarDate(effectiveEnd.Value);
            var nextDayLocalMidnight = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified).AddDays(1);
            endExclusiveUtc = TimeZoneInfo.ConvertTimeToUtc(nextDayLocalMidnight, tz);
        }

        return (startUtc, endExclusiveUtc);
    }
}
