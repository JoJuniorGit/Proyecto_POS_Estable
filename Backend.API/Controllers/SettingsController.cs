using Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    public SettingsController(InventoryDbContext context, Core.Interfaces.ICurrentUserService currentUserService)
    {
        _context = context;
        _currentUserService = currentUserService;
    }

    [HttpGet("exchange-rate")]
    public async Task<ActionResult> GetExchangeRate()
    {
        var setting = await _context.SystemSettings
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
        if (request.Value <= 0)
            return BadRequest("Exchange rate must be greater than zero.");

        var setting = await _context.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == "ExchangeRate");

        if (setting == null)
        {
            setting = new SystemSetting
            {
                Key = "ExchangeRate",
                Value = request.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                LastUpdated = DateTime.UtcNow
            };
            _context.SystemSettings.Add(setting);
        }
        else
        {
            setting.Value = request.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            setting.LastUpdated = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        return Ok(new { Value = request.Value, LastUpdated = setting.LastUpdated });
    }

    [HttpGet("timezone")]
    public async Task<ActionResult> GetTimeZone()
    {
        var setting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == "SelectedTimeZoneId");
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
}

public class SetExchangeRateRequest
{
    public decimal Value { get; set; }
}

public class SetTimeZoneRequest
{
    public string Id { get; set; } = string.Empty;
}
