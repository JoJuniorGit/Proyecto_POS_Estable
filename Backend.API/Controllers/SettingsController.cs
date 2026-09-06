using Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly InventoryDbContext _context;
    private readonly Core.Interfaces.ICurrentUserService _currentUserService;
    private readonly Microsoft.AspNetCore.SignalR.IHubContext<Backend.API.Hubs.ExchangeRateHub>? _hubContext;

    public SettingsController(
        InventoryDbContext context, 
        Core.Interfaces.ICurrentUserService currentUserService,
        Microsoft.AspNetCore.SignalR.IHubContext<Backend.API.Hubs.ExchangeRateHub>? hubContext = null)
    {
        _context = context;
        _currentUserService = currentUserService;
        _hubContext = hubContext;
    }

    [HttpGet("exchange-rate")]
    public async Task<ActionResult> GetExchangeRate()
    {
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var record = await _context.ExchangeRateHistory.AsNoTracking().FirstOrDefaultAsync(r => r.Date == today);
        if (record == null)
        {
            record = await _context.ExchangeRateHistory
                .AsNoTracking()
                .Where(r => r.Date <= today)
                .OrderByDescending(r => r.Date)
                .FirstOrDefaultAsync();
        }

        if (record != null && record.Rate > 0)
        {
            return Ok(new { Value = record.Rate, LastUpdated = (DateTime?)record.UpdatedAt });
        }

        var setting = await _context.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == "ExchangeRate");

        if (setting == null)
            return Ok(new { Value = 0m, LastUpdated = (DateTime?)null });

        if (decimal.TryParse(setting.Value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var rate))
        {
            return Ok(new { Value = rate, LastUpdated = setting.LastUpdated });
        }

        return Ok(new { Value = 0m, LastUpdated = setting.LastUpdated });
    }

    [HttpPost("exchange-rate")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> SetExchangeRate([FromBody] SetExchangeRateRequest request)
    {
        if (!_currentUserService.CanMutateSettings)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para actualizar la configuración.");
        }
        if (request.Value <= 0 || request.Value > 1_000_000m)
            return BadRequest("Exchange rate must be greater than zero and less than or equal to 1,000,000.");

        var roundedRate = Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(request.Value);

        var setting = await _context.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == "ExchangeRate");

        if (setting == null)
        {
            setting = new SystemSetting
            {
                Key = "ExchangeRate",
                Value = roundedRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                LastUpdated = DateTime.UtcNow
            };
            _context.SystemSettings.Add(setting);
        }
        else
        {
            setting.Value = roundedRate.ToString(System.Globalization.CultureInfo.InvariantCulture);
            setting.LastUpdated = DateTime.UtcNow;
        }

        // Sincronizar fuente autoritativa ExchangeRateHistory (H-API-7)
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var existingHistory = await _context.ExchangeRateHistory.FirstOrDefaultAsync(r => r.Date == today);
        if (existingHistory != null)
        {
            existingHistory.Rate = roundedRate;
            existingHistory.UpdatedAt = DateTime.UtcNow;
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

        return Ok(new { Value = roundedRate, LastUpdated = setting.LastUpdated });
    }

    [HttpGet("timezone")]
    public async Task<ActionResult> GetTimeZone()
    {
        var setting = await _context.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "SelectedTimeZoneId");
        return Ok(new { Id = setting?.Value ?? string.Empty });
    }

    [HttpPost("timezone")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> SetTimeZone([FromBody] SetTimeZoneRequest request)
    {
        if (!_currentUserService.CanMutateSettings)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para actualizar la zona horaria.");
        }
        var setting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == "SelectedTimeZoneId");
        if (setting == null)
        {
            setting = new SystemSetting
            {
                Key = "SelectedTimeZoneId",
                Value = request.Id,
                LastUpdated = DateTime.UtcNow
            };
            _context.SystemSettings.Add(setting);
        }
        else
        {
            setting.Value = request.Id;
            setting.LastUpdated = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
        return Ok(new { Id = setting.Value });
    }

    [HttpGet("currency-format")]
    public async Task<ActionResult> GetCurrencyFormat()
    {
        var setting = await _context.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "CurrencyFormat");
        var format = setting?.Value;
        if (string.IsNullOrWhiteSpace(format) || 
            (!format.Equals("Venezuelan", StringComparison.OrdinalIgnoreCase) && 
             !format.Equals("International", StringComparison.OrdinalIgnoreCase)))
        {
            format = "Venezuelan"; // Default oficial
        }
        return Ok(new { Format = format });
    }

    [HttpPut("currency-format")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> SetCurrencyFormat([FromBody] SetCurrencyFormatRequest request)
    {
        if (!_currentUserService.CanMutateSettings)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para actualizar el formato de moneda.");
        }

        if (string.IsNullOrWhiteSpace(request?.Format) ||
            (!request.Format.Equals("Venezuelan", StringComparison.OrdinalIgnoreCase) &&
             !request.Format.Equals("International", StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest("El formato debe ser 'Venezuelan' o 'International'.");
        }

        var normalizedFormat = request.Format.Equals("International", StringComparison.OrdinalIgnoreCase)
            ? "International"
            : "Venezuelan";

        var setting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == "CurrencyFormat");
        if (setting == null)
        {
            setting = new SystemSetting
            {
                Key = "CurrencyFormat",
                Value = normalizedFormat,
                LastUpdated = DateTime.UtcNow
            };
            _context.SystemSettings.Add(setting);
        }
        else
        {
            setting.Value = normalizedFormat;
            setting.LastUpdated = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        if (_hubContext != null)
        {
            await _hubContext.Clients.All.SendAsync("OnCurrencyFormatUpdated", normalizedFormat);
        }

        return Ok(new { Format = normalizedFormat, LastUpdated = setting.LastUpdated });
    }
}

public class SetExchangeRateRequest
{
    public decimal Value { get; set; }
}

public class SetTimeZoneRequest
{
    public string Id { get; set; } = string.Empty;
}

public class SetCurrencyFormatRequest
{
    public string Format { get; set; } = "Venezuelan";
}
