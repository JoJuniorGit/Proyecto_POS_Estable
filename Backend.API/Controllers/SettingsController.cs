using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
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
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache? _cache;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public SettingsController(
        ISystemSettingsService settingsService,
        Core.Interfaces.ICurrentUserService currentUserService,
        IExchangeRateHistoryService historyService,
        IExchangeRateWriteService? writeService = null,
        ITimeZoneProvider? timeZoneProvider = null,
        Microsoft.AspNetCore.SignalR.IHubContext<Backend.API.Hubs.ExchangeRateHub>? hubContext = null,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? cache = null)
    {
        _settingsService = settingsService;
        _currentUserService = currentUserService;
        _historyService = historyService;
        _writeService = writeService;
        _timeZoneProvider = timeZoneProvider ?? new Core.Services.TimeZoneProvider(settingsService);
        _hubContext = hubContext;
        _cache = cache;
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

        var todayDate = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        _cache?.Remove($"er_today_{todayDate:yyyy-MM-dd}");

        return Ok(new SettingValueResponseDto { Value = roundedRate, LastUpdated = DateTime.UtcNow });
    }

    [HttpGet("timezone")]
    public async Task<ActionResult> GetTimeZoneAsync(CancellationToken cancellationToken = default)
    {
        var tzId = await _timeZoneProvider.GetTimeZoneIdAsync(cancellationToken);
        return Ok(new TimeZoneSettingResponseDto { Id = tzId ?? string.Empty });
    }

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

    [HttpGet("allow-negative-stock")]
    public async Task<ActionResult> GetAllowNegativeStockAsync(CancellationToken cancellationToken = default)
    {
        var value = await _settingsService.GetSettingAsync(Core.Constants.SettingKeys.AllowNegativeStock);
        var allowed = bool.TryParse(value, out var parsed) && parsed;
        return Ok(new AllowNegativeStockResponseDto { Allowed = allowed });
    }

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

    // 8.184-T1 (S1): políticas globales de precios y redondeo. GET resuelve defaults si la clave
    // falta o es ilegible (ExactMath / ExactMath / protección de margen ACTIVADA); PUT valida la
    // whitelist de enums definidos y persiste por NOMBRE. SIN SignalR v1 (supuesto): la lectura
    // ocurre al abrir cada superficie (molde allow-negative-stock).
    [HttpGet("pricing-policies")]
    public async Task<ActionResult> GetPricingPoliciesAsync(CancellationToken cancellationToken = default)
    {
        var usdRule = await GetStoredRoundingRuleAsync(Core.Constants.SettingKeys.Pricing.DefaultUsdRoundingRule, RoundingRuleType.ExactMath);
        var cashRule = await GetStoredRoundingRuleAsync(Core.Constants.SettingKeys.Pricing.DefaultCashBsRoundingRule, RoundingRuleType.ExactMath);

        var floorValue = await _settingsService.GetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor);
        var enforceFloor = !bool.TryParse(floorValue, out var parsedFloor) || parsedFloor;

        return Ok(new PricingPoliciesDto
        {
            UsdRoundingRule = usdRule,
            CashBsRoundingRule = cashRule,
            EnforceMinimumMarginFloor = enforceFloor
        });
    }

    [HttpPut("pricing-policies")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> SetPricingPoliciesAsync([FromBody] PricingPoliciesDto request, CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateSettings)
        {
            return this.ApiForbidden("Su rol no tiene permisos para actualizar las políticas de precios.");
        }

        // Whitelist estricta: solo reglas definidas del contrato de redondeo (S1).
        if (request is null ||
            !Enum.IsDefined(request.UsdRoundingRule) ||
            !Enum.IsDefined(request.CashBsRoundingRule))
        {
            return this.ApiBadRequest("Las reglas de redondeo deben ser valores válidos del contrato de políticas de precios.");
        }

        await _settingsService.SetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultUsdRoundingRule, request.UsdRoundingRule.ToString());
        await _settingsService.SetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultCashBsRoundingRule, request.CashBsRoundingRule.ToString());
        await _settingsService.SetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor, request.EnforceMinimumMarginFloor.ToString());

        return Ok(new PricingPoliciesDto
        {
            UsdRoundingRule = request.UsdRoundingRule,
            CashBsRoundingRule = request.CashBsRoundingRule,
            EnforceMinimumMarginFloor = request.EnforceMinimumMarginFloor
        });
    }

    /// <summary>
    /// 8.184-T1 (S1): lee una regla de redondeo almacenada por NOMBRE de enum; cualquier valor
    /// ausente, ilegible o fuera del contrato cae al default indicado.
    /// </summary>
    private async Task<RoundingRuleType> GetStoredRoundingRuleAsync(string key, RoundingRuleType fallback)
    {
        var stored = await _settingsService.GetSettingAsync(key);
        return Enum.TryParse<RoundingRuleType>(stored, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : fallback;
    }

    // 8.177-T2 (S3): razón social usada en el encabezado legal de las exportaciones del Kardex.
    [HttpGet("company-name")]
    public async Task<ActionResult> GetCompanyNameAsync(CancellationToken cancellationToken = default)
    {
        var value = await _settingsService.GetSettingAsync("CompanyLegalName");
        return Ok(new CompanyNameSettingResponseDto
        {
            Name = string.IsNullOrWhiteSpace(value) ? "COMMANDCENTER POS" : value
        });
    }

    [HttpPost("company-name")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> SetCompanyNameAsync([FromBody] SetCompanyNameRequest request, CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateSettings)
        {
            return this.ApiForbidden("Su rol no tiene permisos para actualizar la razón social.");
        }

        var value = request?.Name?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return this.ApiBadRequest("La razón social no puede estar vacía.");
        }

        if (value.Length > 120)
        {
            return this.ApiBadRequest("La razón social no puede exceder los 120 caracteres.");
        }

        await _settingsService.SetSettingAsync("CompanyLegalName", value);
        return Ok(new CompanyNameSettingResponseDto { Name = value });
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

public class SetCompanyNameRequest
{
    public string Name { get; set; } = string.Empty;
}

public class CompanyNameSettingResponseDto
{
    public string Name { get; set; } = string.Empty;
}
