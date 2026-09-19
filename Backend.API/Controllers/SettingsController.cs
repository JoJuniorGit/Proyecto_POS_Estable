using Core.Entities;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Backend.API.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

[Authorize(Roles = "Admin,Manager")]
[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly ISystemSettingsService _settingsService;
    private readonly Core.Interfaces.ICurrentUserService _currentUserService;
    private readonly IExchangeRateHistoryService _historyService;
    private readonly IExchangeRateWriteService? _writeService;
    private readonly ITimeZoneProvider _timeZoneProvider;
    private readonly Microsoft.AspNetCore.SignalR.IHubContext<Backend.API.Hubs.ExchangeRateHub>? _hubContext;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public SettingsController(
        ISystemSettingsService settingsService,
        Core.Interfaces.ICurrentUserService currentUserService,
        IExchangeRateHistoryService historyService,
        IExchangeRateWriteService? writeService = null,
        ITimeZoneProvider? timeZoneProvider = null,
        Microsoft.AspNetCore.SignalR.IHubContext<Backend.API.Hubs.ExchangeRateHub>? hubContext = null)
    {
        _settingsService = settingsService;
        _currentUserService = currentUserService;
        _historyService = historyService;
        _writeService = writeService;
        _timeZoneProvider = timeZoneProvider ?? new Core.Services.TimeZoneProvider(settingsService);
        _hubContext = hubContext;
    }

    public SettingsController(
        InventoryDbContext context, 
        Core.Interfaces.ICurrentUserService currentUserService,
        Core.Interfaces.ISystemSettingsService settingsService,
        Microsoft.AspNetCore.SignalR.IHubContext<Backend.API.Hubs.ExchangeRateHub>? hubContext = null)
        : this(
            settingsService,
            currentUserService,
            new ExchangeRateHistoryService(context),
            new ExchangeRateWriteService(context, new InventoryService(context), hubContext: hubContext),
            new Core.Services.TimeZoneProvider(settingsService),
            hubContext)
    {
    }

    [HttpGet("exchange-rate")]
    public async Task<ActionResult> GetExchangeRateAsync(CancellationToken cancellationToken = default)
    {
        var (rate, _, updatedAt) = await _historyService.GetTodayRateWithMetadataAsync(cancellationToken);
        if (rate > 0)
        {
            return Ok(new SettingValueResponseDto { Value = rate, LastUpdated = updatedAt });
        }

        var settingValue = await _settingsService.GetSettingAsync("ExchangeRate");
        if (settingValue == null)
            return Ok(new SettingValueResponseDto { Value = 0m, LastUpdated = null });

        if (decimal.TryParse(settingValue, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var parsedRate))
        {
            return Ok(new SettingValueResponseDto { Value = parsedRate, LastUpdated = null });
        }

        return Ok(new SettingValueResponseDto { Value = 0m, LastUpdated = null });
    }

    [NonAction]
    public Task<ActionResult> GetExchangeRate() => GetExchangeRateAsync();

    [HttpPost("exchange-rate")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> SetExchangeRateAsync([FromBody] SetExchangeRateRequest request, CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateSettings)
        {
            return this.ApiForbidden("Su rol no tiene permisos para actualizar la configuración de tasa de cambio.");
        }
        if (request.Value <= 0 || request.Value > 1_000_000m)
            return this.ApiBadRequest("La tasa de cambio debe ser mayor a cero y menor o igual a 1.000.000.");

        var roundedRate = Core.Helpers.PricingCalculator.RoundExchangeRateCeiling(request.Value);

        await _settingsService.SetSettingAsync("ExchangeRate", roundedRate.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (_writeService != null)
        {
            await _writeService.UpsertTodayRateAsync(roundedRate, cancellationToken);
        }

        return Ok(new SettingValueResponseDto { Value = roundedRate, LastUpdated = DateTime.UtcNow });
    }

    [NonAction]
    public Task<ActionResult> SetExchangeRate(SetExchangeRateRequest request) => SetExchangeRateAsync(request);

    [HttpGet("timezone")]
    public async Task<ActionResult> GetTimeZoneAsync(CancellationToken cancellationToken = default)
    {
        var tzId = await _timeZoneProvider.GetTimeZoneIdAsync(cancellationToken);
        return Ok(new TimeZoneSettingResponseDto { Id = tzId ?? string.Empty });
    }

    [NonAction]
    public Task<ActionResult> GetTimeZone() => GetTimeZoneAsync();

    [HttpPost("timezone")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> SetTimeZoneAsync([FromBody] SetTimeZoneRequest request, CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateSettings)
        {
            return this.ApiForbidden("Su rol no tiene permisos para actualizar la zona horaria.");
        }

        await _settingsService.SetSettingAsync("SelectedTimeZoneId", request.Id);
        _timeZoneProvider.Invalidate();
        return Ok(new TimeZoneSettingResponseDto { Id = request.Id });
    }

    [NonAction]
    public Task<ActionResult> SetTimeZone(SetTimeZoneRequest request) => SetTimeZoneAsync(request);

    [HttpGet("currency-format")]
    public async Task<ActionResult> GetCurrencyFormatAsync(CancellationToken cancellationToken = default)
    {
        var format = await _settingsService.GetSettingAsync("CurrencyFormat");
        if (string.IsNullOrWhiteSpace(format) || 
            (!format.Equals("Venezuelan", StringComparison.OrdinalIgnoreCase) && 
             !format.Equals("International", StringComparison.OrdinalIgnoreCase)))
        {
            format = "Venezuelan";
        }
        return Ok(new CurrencyFormatResponseDto { Format = format });
    }

    [NonAction]
    public Task<ActionResult> GetCurrencyFormat() => GetCurrencyFormatAsync();

    [HttpPut("currency-format")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> SetCurrencyFormatAsync([FromBody] SetCurrencyFormatRequest request, CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateSettings)
        {
            return this.ApiForbidden("Su rol no tiene permisos para actualizar el formato de moneda.");
        }

        if (string.IsNullOrWhiteSpace(request?.Format) ||
            (!request.Format.Equals("Venezuelan", StringComparison.OrdinalIgnoreCase) &&
             !request.Format.Equals("International", StringComparison.OrdinalIgnoreCase)))
        {
            return this.ApiBadRequest("El formato debe ser 'Venezuelan' o 'International'.");
        }

        var normalizedFormat = request.Format.Equals("International", StringComparison.OrdinalIgnoreCase)
            ? "International"
            : "Venezuelan";

        await _settingsService.SetSettingAsync("CurrencyFormat", normalizedFormat);

        if (_hubContext != null)
        {
            await _hubContext.Clients.All.SendAsync("OnCurrencyFormatUpdated", normalizedFormat, cancellationToken);
        }

        return Ok(new CurrencyFormatResponseDto { Format = normalizedFormat, LastUpdated = DateTime.UtcNow });
    }

    [NonAction]
    public Task<ActionResult> SetCurrencyFormat(SetCurrencyFormatRequest request) => SetCurrencyFormatAsync(request);

    [HttpGet("allow-negative-stock")]
    public async Task<ActionResult> GetAllowNegativeStockAsync(CancellationToken cancellationToken = default)
    {
        var value = await _settingsService.GetSettingAsync(Core.Constants.SettingKeys.AllowNegativeStock);
        var allowed = bool.TryParse(value, out var parsed) && parsed;
        return Ok(new AllowNegativeStockResponseDto { Allowed = allowed });
    }

    [NonAction]
    public Task<ActionResult> GetAllowNegativeStock() => GetAllowNegativeStockAsync();

    [HttpPut("allow-negative-stock")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> SetAllowNegativeStockAsync([FromBody] SetAllowNegativeStockRequest request, CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateSettings)
        {
            return this.ApiForbidden("Su rol no tiene permisos para actualizar esta configuración.");
        }

        await _settingsService.SetSettingAsync(Core.Constants.SettingKeys.AllowNegativeStock, request.Allowed.ToString());
        return Ok(new AllowNegativeStockResponseDto { Allowed = request.Allowed });
    }

    [NonAction]
    public Task<ActionResult> SetAllowNegativeStock(SetAllowNegativeStockRequest request) => SetAllowNegativeStockAsync(request);
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

public class SetAllowNegativeStockRequest
{
    public bool Allowed { get; set; }
}

public class SettingValueResponseDto
{
    public decimal Value { get; set; }
    public DateTime? LastUpdated { get; set; }
}

public class TimeZoneSettingResponseDto
{
    public string Id { get; set; } = string.Empty;
}

public class CurrencyFormatResponseDto
{
    public string Format { get; set; } = string.Empty;
    public DateTime? LastUpdated { get; set; }
}

public class AllowNegativeStockResponseDto
{
    public bool Allowed { get; set; }
}
