using Core.Entities;
using Inventory.Module.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Sales.Module.Interfaces;
using Backend.API.Hubs;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/exchange-rate")]
public class ExchangeRateController : ControllerBase
{
    private readonly InventoryDbContext _context;
    private readonly Core.Interfaces.ICurrentUserService _currentUserService;
    private readonly ISalesService _salesService;
    private readonly Core.Interfaces.IInventoryService _inventoryService;
    private readonly IHubContext<ExchangeRateHub> _hubContext;

    public ExchangeRateController(
        InventoryDbContext context,
        Core.Interfaces.ICurrentUserService currentUserService,
        ISalesService salesService,
        Core.Interfaces.IInventoryService inventoryService,
        IHubContext<ExchangeRateHub> hubContext)
    {
        _context = context;
        _currentUserService = currentUserService;
        _salesService = salesService;
        _inventoryService = inventoryService;
        _hubContext = hubContext;
    }

    /// <summary>
    /// Returns today's exchange rate, or 0 if none has been set.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("today")]
    public async Task<ActionResult> GetToday()
    {
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
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

        if (record == null)
            return Ok(new { Value = 0m, Date = today, UpdatedAt = (DateTime?)null, UpdatedAtLocal = (DateTime?)null });

        var utc = record.UpdatedAt.Kind == DateTimeKind.Utc
            ? record.UpdatedAt
            : DateTime.SpecifyKind(record.UpdatedAt, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);

        return Ok(new { Value = record.Rate, Date = record.Date, UpdatedAt = record.UpdatedAt, UpdatedAtLocal = (DateTime?)local });
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
    public async Task<ActionResult> UpsertRate([FromBody] UpsertExchangeRateRequest request)
    {
        if (!_currentUserService.CanMutateExchangeRate)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para actualizar la tasa de cambio.");
        }
        if (request.Value <= 0 || request.Value > 1_000_000m)
            return BadRequest("Exchange rate must be greater than zero and less than or equal to 1,000,000.");

        var roundedRate = Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(request.Value);
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var existing = await _context.ExchangeRateHistory
            .FirstOrDefaultAsync(r => r.Date == today);

        if (existing != null)
        {
            existing.Rate = roundedRate;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _context.ExchangeRateHistory.Add(new ExchangeRateHistory
            {
                Date = today,
                Rate = roundedRate,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync();

        // Invalidate today's cached exchange rate
        _inventoryService.InvalidateTodayExchangeRateCache();

        // Recalculate OnHold sales with the new exchange rate
        await _salesService.RecalculateOnHoldSalesAsync(roundedRate);

        // Broadcast rate update and OnHold sales refresh signal to all connected clients
        await _hubContext.Clients.All.SendAsync("ReceiveRateUpdate", roundedRate);
        await _hubContext.Clients.All.SendAsync("OnHoldSalesUpdated");

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
    public async Task<ActionResult> SyncBcv([FromServices] Backend.API.Services.BcvScraperService scraperService)
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
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { Message = "Tiempo de espera agotado al conectar con el portal del BCV. Puede reintentar la sincronización o ingresar la tasa manualmente.", Detail = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { Message = "La estructura del portal del BCV ha cambiado o no contiene el formato esperado. Por favor, reintente o ingrese la tasa manualmente.", Detail = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { Message = "Error de red o conexión al consultar el portal del BCV. Verifique el acceso a internet o ingrese la tasa manualmente.", Detail = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = $"Error inesperado al sincronizar con el BCV: {ex.Message}" });
        }

        if (rate.Value <= 0 || rate.Value > 1_000_000m)
        {
            return BadRequest("La tasa extraída del BCV se encuentra fuera del rango válido (0, 1.000.000].");
        }

        var roundedRate = Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(rate.Value);
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var existing = await _context.ExchangeRateHistory
            .FirstOrDefaultAsync(r => r.Date == today);

        if (existing != null)
        {
            existing.Rate = roundedRate;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _context.ExchangeRateHistory.Add(new ExchangeRateHistory
            {
                Date = today,
                Rate = roundedRate,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync();

        // Invalidate today's cached exchange rate immediately
        _inventoryService.InvalidateTodayExchangeRateCache();

        // Recalculate OnHold sales with the new exchange rate
        await _salesService.RecalculateOnHoldSalesAsync(roundedRate);

        // Broadcast to clients via SignalR
        await _hubContext.Clients.All.SendAsync("ReceiveRateUpdate", roundedRate);
        await _hubContext.Clients.All.SendAsync("OnHoldSalesUpdated");

        var tz = await GetConfiguredTimeZoneAsync();
        var nowUtc = DateTime.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);

        return Ok(new { Value = roundedRate, Date = today, UpdatedAt = nowUtc, UpdatedAtLocal = nowLocal });
    }

    private async Task<TimeZoneInfo> GetConfiguredTimeZoneAsync()
    {
        var tzId = await _context.SystemSettings
            .Where(s => s.Key == "SelectedTimeZoneId")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        return Core.Helpers.TimeZoneHelper.GetTimeZone(tzId);
    }
}

public class UpsertExchangeRateRequest
{
    public decimal Value { get; set; }
}
