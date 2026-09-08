using Core.Entities;
using Inventory.Module.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Backend.API.Services;
using Core.Logging;
using Microsoft.Extensions.Caching.Memory;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/exchange-rate")]
public class ExchangeRateController : ControllerBase
{
    private readonly InventoryDbContext _context;
    private readonly Core.Interfaces.ICurrentUserService _currentUserService;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache? _cache;

    public ExchangeRateController(
        InventoryDbContext context,
        Core.Interfaces.ICurrentUserService currentUserService,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? cache = null)
    {
        _context = context;
        _currentUserService = currentUserService;
        _cache = cache;
    }

    /// <summary>
    /// Returns today's exchange rate, or 0 if none has been set.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("today")]
    public async Task<ActionResult> GetToday()
    {
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        // 8.9-M1: caché de corta vida (20s) para el endpoint de mayor frecuencia;
        // la tasa cambia pocas veces al día y se invalida explícitamente en cada write.
        var cacheKey = $"er_today_{today:yyyy-MM-dd}";
        if (_cache != null && _cache.TryGetValue(cacheKey, out object? cached))
        {
            return Ok(cached);
        }

        var record = await _context.ExchangeRateHistory
            .FirstOrDefaultAsync(r => r.Date == today);

        if (record == null)
        {
            // Fallback al último registro histórico válido hasta hoy (ignora registros futuros erróneos y cubre fines de semana/feriados)
            record = await _context.ExchangeRateHistory
                .Where(r => r.Date <= today)
                .OrderByDescending(r => r.Date)
                .FirstOrDefaultAsync();
        }

        var tz = await GetConfiguredTimeZoneAsync();

        object result;
        if (record == null)
            result = new { Value = 0m, Date = today, UpdatedAt = (DateTime?)null, UpdatedAtLocal = (DateTime?)null };
        else
        {
            var utc = record.UpdatedAt.Kind == DateTimeKind.Utc
                ? record.UpdatedAt
                : DateTime.SpecifyKind(record.UpdatedAt, DateTimeKind.Utc);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);

            result = new { Value = record.Rate, Date = record.Date, UpdatedAt = record.UpdatedAt, UpdatedAtLocal = (DateTime?)local };
        }

        _cache?.Set(cacheKey, result, new Microsoft.Extensions.Caching.Memory.MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(20),
            Size = 1
        });
        return Ok(result);
    }

    /// <summary>
    /// Returns the exchange rate history sorted by date descending, clamped to a maximum of 365 days.
    /// </summary>
    [HttpGet("history")]
    public async Task<ActionResult> GetHistory([FromQuery] int limit = 365)
    {
        limit = Math.Clamp(limit, 1, 365);
        var tz = await GetConfiguredTimeZoneAsync();
        var history = await _context.ExchangeRateHistory
            .OrderByDescending(r => r.Date)
            .Take(limit)
            .Select(r => new { r.Date, r.Rate, r.UpdatedAt })
            .ToListAsync();

        var result = history.Select(r =>
        {
            var utc = r.UpdatedAt.Kind == DateTimeKind.Utc
                ? r.UpdatedAt
                : DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);

            return new
            {
                r.Date,
                r.Rate,
                r.UpdatedAt,
                UpdatedAtLocal = local
            };
        });

        return Ok(result);
    }

    /// <summary>
    /// Upserts today's exchange rate. One record per day; overwrites if already set.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> UpsertRate(
        [FromBody] UpsertExchangeRateRequest request,
        [FromServices] IExchangeRateWriteService rateWriteService)
    {
        if (!_currentUserService.CanMutateExchangeRate)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para actualizar la tasa de cambio.");
        }
        if (request.Value <= 0 || request.Value > 1_000_000m)
            return BadRequest("Exchange rate must be greater than zero and less than or equal to 1,000,000.");

        var roundedRate = Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(request.Value);
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();

        // 8.7-M3: la escritura + recálculo OnHold + broadcast quedan centralizados en el servicio único.
        await rateWriteService.UpsertTodayRateAsync(roundedRate);

        // 8.9-M1: la escritura invalida la caché /today para visibilidad inmediata.
        _cache?.Remove($"er_today_{today:yyyy-MM-dd}");

        var tz = await GetConfiguredTimeZoneAsync();
        var nowUtc = DateTime.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);

        return Ok(new { Value = roundedRate, Date = today, UpdatedAt = nowUtc, UpdatedAtLocal = nowLocal });
    }

    /// <summary>
    /// Forces a manual scrape of the BCV website and upserts today's exchange rate.
    /// </summary>
    [HttpPost("sync-bcv")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> SyncBcv(
        [FromServices] Backend.API.Services.BcvScraperService scraperService,
        [FromServices] IExchangeRateWriteService rateWriteService)
    {
        if (!_currentUserService.CanMutateExchangeRate)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para sincronizar la tasa de cambio.");
        }
        decimal? rate;
        try
        {
            rate = await scraperService.GetOfficialUsdRateAsync();
            if (!rate.HasValue)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { Message = "No se pudo extraer la tasa oficial del BCV. Verifique el portal o ingrese la tasa manualmente." });
            }
        }
        catch (TimeoutException ex)
        {
            AppLogger.LogCrash(ex, "ExchangeRateController.SyncBcvRate.Timeout");
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { Message = "Tiempo de espera agotado al conectar con el portal del BCV. Puede reintentar la sincronización o ingresar la tasa manualmente." });
        }
        catch (InvalidOperationException ex)
        {
            AppLogger.LogCrash(ex, "ExchangeRateController.SyncBcvRate.InvalidOperation");
            return StatusCode(StatusCodes.Status502BadGateway, new { Message = "La estructura del portal del BCV ha cambiado o no contiene el formato esperado. Por favor, reintente o ingrese la tasa manualmente." });
        }
        catch (HttpRequestException ex)
        {
            AppLogger.LogCrash(ex, "ExchangeRateController.SyncBcvRate.HttpRequest");
            return StatusCode(StatusCodes.Status502BadGateway, new { Message = "Error de red o conexión al consultar el portal del BCV. Verifique el acceso a internet o ingrese la tasa manualmente." });
        }
        catch (Exception ex)
        {
            AppLogger.LogCrash(ex, "ExchangeRateController.SyncBcvRate");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Error inesperado al sincronizar con el BCV. Intente más tarde o ingrese la tasa manualmente." });
        }

        if (rate.Value <= 0 || rate.Value > 1_000_000m)
        {
            return BadRequest("La tasa extraída del BCV se encuentra fuera del rango válido (0, 1.000.000].");
        }

        var roundedRate = Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(rate.Value);
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();

        // 8.7-M3: la escritura + recálculo OnHold + broadcast quedan centralizados en el servicio único.
        await rateWriteService.UpsertTodayRateAsync(roundedRate);

        // 8.9-M1: la escritura invalida la caché /today.
        _cache?.Remove($"er_today_{today:yyyy-MM-dd}");

        var tz = await GetConfiguredTimeZoneAsync();
        var nowUtc = DateTime.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);

        return Ok(new { Value = roundedRate, Date = today, UpdatedAt = nowUtc, UpdatedAtLocal = nowLocal });
    }

    // 8.9-M1: la timezone configurada casi nunca cambia; se cachea 10 min (una query de SystemSettings menos por request).
    private async Task<TimeZoneInfo> GetConfiguredTimeZoneAsync()
    {
        if (_cache != null && _cache.TryGetValue("er_tz", out object? cachedTz) && cachedTz is TimeZoneInfo tzCached)
        {
            return tzCached;
        }

        var tzId = await _context.SystemSettings
            .Where(s => s.Key == "SelectedTimeZoneId")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        var tz = Core.Helpers.TimeZoneHelper.GetTimeZone(tzId);
        _cache?.Set("er_tz", tz, new Microsoft.Extensions.Caching.Memory.MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
            Size = 1
        });
        return tz;
    }
}

public class UpsertExchangeRateRequest
{
    public decimal Value { get; set; }
}
