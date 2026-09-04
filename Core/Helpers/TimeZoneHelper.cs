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
}
