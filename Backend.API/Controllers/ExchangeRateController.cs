using Core.Entities;
using Core.Interfaces;
using Core.Logging;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Backend.API.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/exchange-rate")]
public class ExchangeRateController : ControllerBase
{
    private readonly IExchangeRateHistoryService _historyService;
    private readonly IExchangeRateWriteService? _writeService;
    private readonly ITimeZoneProvider _timeZoneProvider;
    private readonly Core.Interfaces.ICurrentUserService _currentUserService;
    private readonly IMemoryCache? _cache;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public ExchangeRateController(
        IExchangeRateHistoryService historyService,
        IExchangeRateWriteService writeService,
        ITimeZoneProvider timeZoneProvider,
        Core.Interfaces.ICurrentUserService currentUserService,
        IMemoryCache? cache = null)
    {
        _historyService = historyService;
        _writeService = writeService;
        _timeZoneProvider = timeZoneProvider;
        _currentUserService = currentUserService;
        _cache = cache;
    }

    public ExchangeRateController(
        InventoryDbContext context,
        Core.Interfaces.ICurrentUserService currentUserService,
        IMemoryCache? cache = null)
        : this(
            new ExchangeRateHistoryService(context),
            null!,
            new Core.Services.TimeZoneProvider(new SystemSettingsService(context)),
            currentUserService,
            cache)
    {
    }

    [AllowAnonymous]
    [HttpGet("today")]
    public async Task<ActionResult> GetTodayAsync(CancellationToken cancellationToken = default)
    {
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var cacheKey = $"er_today_{today:yyyy-MM-dd}";
        if (_cache != null && _cache.TryGetValue(cacheKey, out object? cached))
        {
            return Ok(cached);
        }

        var (rate, date, updatedAt) = await _historyService.GetTodayRateWithMetadataAsync(cancellationToken);
        var tz = await _timeZoneProvider.GetTimeZoneInfoAsync(cancellationToken);

        ExchangeRateTodayResponseDto result;
        if (updatedAt == null && rate == 0m)
        {
            result = new ExchangeRateTodayResponseDto { Value = 0m, Date = today, UpdatedAt = null, UpdatedAtLocal = null };
        }
        else
        {
            var utc = updatedAt.HasValue && updatedAt.Value.Kind == DateTimeKind.Utc
                ? updatedAt.Value
                : (updatedAt.HasValue ? DateTime.SpecifyKind(updatedAt.Value, DateTimeKind.Utc) : (DateTime?)null);
            var local = utc.HasValue ? TimeZoneInfo.ConvertTimeFromUtc(utc.Value, tz) : (DateTime?)null;

            result = new ExchangeRateTodayResponseDto
            {
                Value = rate,
                Date = date,
                UpdatedAt = updatedAt,
                UpdatedAtLocal = local
            };
        }

        _cache?.Set(cacheKey, result, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(20),
            Size = 1
        });
        return Ok(result);
    }

    [NonAction]
    public Task<ActionResult> GetToday(CancellationToken cancellationToken = default) => GetTodayAsync(cancellationToken);

    [HttpGet("history")]
    public async Task<ActionResult> GetHistoryAsync([FromQuery] int limit = 365, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 365);
        var tz = await _timeZoneProvider.GetTimeZoneInfoAsync(cancellationToken);
        var history = await _historyService.GetHistoryAsync(limit, cancellationToken);

        var result = history.Select(r =>
        {
            var utc = r.UpdatedAt.Kind == DateTimeKind.Utc
                ? r.UpdatedAt
                : DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);

            return new ExchangeRateHistoryResponseDto
            {
                Date = r.Date,
                Rate = r.Rate,
                UpdatedAt = r.UpdatedAt,
                UpdatedAtLocal = local
            };
        });

        return Ok(result);
    }

    [NonAction]
    public Task<ActionResult> GetHistory(int limit = 365, CancellationToken cancellationToken = default) => GetHistoryAsync(limit, cancellationToken);

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> UpsertRateAsync(
        [FromBody] UpsertExchangeRateRequest request,
        [FromServices] IExchangeRateWriteService? rateWriteService = null)
    {
        if (!_currentUserService.CanMutateExchangeRate)
        {
            return this.ApiForbidden("El rol Cajero no tiene permisos para actualizar la tasa de cambio.");
        }
        if (request.Value <= 0 || request.Value > 1_000_000m)
            return this.ApiBadRequest("Exchange rate must be greater than zero and less than or equal to 1,000,000.");

        var roundedRate = Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(request.Value);
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();

        var effectiveWriteService = rateWriteService ?? _writeService;
        if (effectiveWriteService != null)
        {
            await effectiveWriteService.UpsertTodayRateAsync(roundedRate);
        }

        _cache?.Remove($"er_today_{today:yyyy-MM-dd}");

        var tz = await _timeZoneProvider.GetTimeZoneInfoAsync();
        var nowUtc = DateTime.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);

        return Ok(new ExchangeRateTodayResponseDto { Value = roundedRate, Date = today, UpdatedAt = nowUtc, UpdatedAtLocal = nowLocal });
    }

    [NonAction]
    public Task<ActionResult> UpsertRate(UpsertExchangeRateRequest request, IExchangeRateWriteService? rateWriteService = null) => UpsertRateAsync(request, rateWriteService);

    [HttpPost("sync-bcv")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> SyncBcvAsync(
        [FromServices] Backend.API.Services.BcvScraperService scraperService,
        [FromServices] IExchangeRateWriteService? rateWriteService = null)
    {
        if (!_currentUserService.CanMutateExchangeRate)
        {
            return this.ApiForbidden("El rol Cajero no tiene permisos para sincronizar la tasa de cambio.");
        }
        decimal? rate;
        try
        {
            rate = await scraperService.GetOfficialUsdRateAsync();
            if (!rate.HasValue)
            {
                return this.ApiProblem("No se pudo extraer la tasa oficial del BCV. Verifique el portal o ingrese la tasa manualmente.", StatusCodes.Status502BadGateway, "Bad Gateway");
            }
        }
        catch (TimeoutException ex)
        {
            AppLogger.LogCrash(ex, "ExchangeRateController.SyncBcvRate.Timeout");
            return this.ApiProblem("Tiempo de espera agotado al conectar con el portal del BCV. Puede reintentar la sincronización o ingresar la tasa manualmente.", StatusCodes.Status504GatewayTimeout, "Gateway Timeout");
        }
        catch (InvalidOperationException ex)
        {
            AppLogger.LogCrash(ex, "ExchangeRateController.SyncBcvRate.InvalidOperation");
            return this.ApiProblem("La estructura del portal del BCV ha cambiado o no contiene el formato esperado. Por favor, reintente o ingrese la tasa manualmente.", StatusCodes.Status502BadGateway, "Bad Gateway");
        }
        catch (HttpRequestException ex)
        {
            AppLogger.LogCrash(ex, "ExchangeRateController.SyncBcvRate.HttpRequest");
            return this.ApiProblem("Error de red o conexión al consultar el portal del BCV. Verifique el acceso a internet o ingrese la tasa manualmente.", StatusCodes.Status502BadGateway, "Bad Gateway");
        }
        catch (Exception ex)
        {
            AppLogger.LogCrash(ex, "ExchangeRateController.SyncBcvRate");
            return this.ApiProblem("Error inesperado al sincronizar con el BCV. Intente más tarde o ingrese la tasa manualmente.", StatusCodes.Status500InternalServerError, "Internal Server Error");
        }

        if (rate.Value <= 0 || rate.Value > 1_000_000m)
        {
            return this.ApiBadRequest("La tasa extraída del BCV se encuentra fuera del rango válido (0, 1.000.000].");
        }

        var roundedRate = Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(rate.Value);
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();

        var effectiveWriteService = rateWriteService ?? _writeService;
        if (effectiveWriteService != null)
        {
            await effectiveWriteService.UpsertTodayRateAsync(roundedRate);
        }

        _cache?.Remove($"er_today_{today:yyyy-MM-dd}");

        var tz = await _timeZoneProvider.GetTimeZoneInfoAsync();
        var nowUtc = DateTime.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);

        return Ok(new ExchangeRateTodayResponseDto { Value = roundedRate, Date = today, UpdatedAt = nowUtc, UpdatedAtLocal = nowLocal });
    }

    [NonAction]
    public Task<ActionResult> SyncBcv(Backend.API.Services.BcvScraperService scraperService, IExchangeRateWriteService? rateWriteService = null) => SyncBcvAsync(scraperService, rateWriteService);
}

public class UpsertExchangeRateRequest
{
    public decimal Value { get; set; }
}

public class ExchangeRateTodayResponseDto
{
    public decimal Value { get; set; }
    public DateOnly Date { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? UpdatedAtLocal { get; set; }
}

public class ExchangeRateHistoryResponseDto
{
    public DateOnly Date { get; set; }
    public decimal Rate { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime UpdatedAtLocal { get; set; }
}
