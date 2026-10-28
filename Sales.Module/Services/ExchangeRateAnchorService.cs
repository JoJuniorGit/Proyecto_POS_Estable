using Core.Helpers;
using Core.Interfaces;
using Core.Logging;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public enum ExchangeRateAnchorOutcome
{
    Accepted,
    Anchored,
    NoOfficialRate,
    Rejected,
    // 8.211-T3c3b1 (S4): Single mode short-circuits to an effective rate of 1 before validation.
    Identity
}

public sealed record ExchangeRateAnchorResult(
    decimal EffectiveRate,
    decimal ClientRate,
    decimal OfficialRate,
    decimal DeviationPct,
    decimal TolerancePct,
    ExchangeRateAnchorOutcome Outcome);

/// <summary>
/// 8.205 (C-01): política única de anclaje de tasa BCV compartida por SalesService y
/// CashDrawerController. Solo calcula y decide; el llamador conserva su contrato de excepción.
/// </summary>
public sealed class ExchangeRateAnchorService
{
    private const decimal DefaultTolerancePct = 0.10m;

    private readonly IInventoryService? _inventoryService;
    private readonly ISystemSettingsService? _settingsService;
    private readonly ILogger? _logger;
    // 8.211-T3c3b1 (S4): optional profile lookup so Single mode can resolve identity before
    // any validation or BCV query. Null preserves the historical Dual behavior.
    private readonly IMonetaryProfileService? _monetaryProfileService;

    public ExchangeRateAnchorService(
        IInventoryService? inventoryService,
        ISystemSettingsService? settingsService,
        ILogger? logger = null,
        IMonetaryProfileService? monetaryProfileService = null)
    {
        _inventoryService = inventoryService;
        _settingsService = settingsService;
        _logger = logger;
        _monetaryProfileService = monetaryProfileService;
    }

    public async Task<ExchangeRateAnchorResult> ResolveAsync(
        decimal clientRate,
        string contextLabel,
        int referenceId,
        CancellationToken cancellationToken = default)
    {
        // 8.211-T3c3b1 (S4): Single mode is an identity regime. Resolve it before the client-rate
        // validation and the BCV anchor so any stored rate (including 0) yields an effective 1.
        if (_monetaryProfileService is not null)
        {
            try
            {
                var profile = await _monetaryProfileService.GetAsync(cancellationToken);
                if (profile.Mode == Core.DTOs.MonetaryMode.Single)
                {
                    return new ExchangeRateAnchorResult(1m, clientRate, 0m, 0m, 0m, ExchangeRateAnchorOutcome.Identity);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogWarning("[A5-AUDIT] No se pudo leer el modo monetario en {Context} #{Ref}. Fail-open: se continúa con el anclaje BCV. Error: {Error}", contextLabel, referenceId, ex.Message);
            }
        }

        if (clientRate <= 0m || clientRate > 1000000m)
        {
            throw new InvalidOperationException("Rechazo Defensivo: Tasa de cambio AppliedRate inválida o no inicializada (<= 0).");
        }

        clientRate = PricingCalculator.RoundExchangeRateCeiling(clientRate);

        if (_inventoryService == null)
        {
            LogWarning("[A5-AUDIT] Servicio de inventario/BCV no disponible en {Context} #{Ref}. Se usa la tasa recibida: {Rate}", contextLabel, referenceId, clientRate);
            return NoOfficialRate(clientRate);
        }

        decimal officialRate;
        try
        {
            officialRate = await _inventoryService.GetTodayExchangeRateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // 8.5-A5: SIN catch-swallow. Se audita el fallo y se continúa con fail-open ordenado.
            LogError(ex, "[A5-AUDIT] Error obteniendo la tasa BCV del día en {Context} #{Ref}. Fail-open: se usa la tasa recibida {Rate}.", contextLabel, referenceId, clientRate);
            return NoOfficialRate(clientRate);
        }

        if (officialRate <= 0m)
        {
            LogWarning("[A5-AUDIT] Sin tasa BCV del día registrada en {Context} #{Ref}. Fail-open: se usa la tasa recibida {Rate}.", contextLabel, referenceId, clientRate);
            return NoOfficialRate(clientRate);
        }

        decimal deviationPct = Math.Abs(clientRate - officialRate) / officialRate;

        if (deviationPct >= 1.0m)
        {
            LogWarning("[A5-AUDIT] Tasa rechazada por posible manipulación. {Context} #{Ref}, TasaRecibida={Received}, TasaBCV={Official}, Desvío={Deviation:P2}", contextLabel, referenceId, clientRate, officialRate, deviationPct);
            return new ExchangeRateAnchorResult(clientRate, clientRate, officialRate, deviationPct, DefaultTolerancePct, ExchangeRateAnchorOutcome.Rejected);
        }

        decimal tolerancePct = DefaultTolerancePct;
        if (_settingsService != null)
        {
            try
            {
                var toleranceSetting = await _settingsService.GetSettingAsync("RateDeviationTolerancePct");
                if (decimal.TryParse(toleranceSetting, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                    && parsed > 0m && parsed < 1.0m)
                {
                    tolerancePct = parsed;
                }
            }
            catch (Exception ex)
            {
                LogWarning("[A5-AUDIT] No se pudo leer la tolerancia configurada en {Context} #{Ref}; se usa el default {Tolerance:P2}. Error: {Error}", contextLabel, referenceId, DefaultTolerancePct, ex.Message);
            }
        }

        if (deviationPct > tolerancePct)
        {
            LogWarning("[A5-AUDIT] Desvío de tasa significativo ({Deviation:P2} > {Tolerance:P2}) en {Context} #{Ref}. Se ANCLA a la tasa BCV del día: {Received} -> {Official}", deviationPct, tolerancePct, contextLabel, referenceId, clientRate, officialRate);
            return new ExchangeRateAnchorResult(officialRate, clientRate, officialRate, deviationPct, tolerancePct, ExchangeRateAnchorOutcome.Anchored);
        }

        return new ExchangeRateAnchorResult(clientRate, clientRate, officialRate, deviationPct, tolerancePct, ExchangeRateAnchorOutcome.Accepted);
    }

    private static ExchangeRateAnchorResult NoOfficialRate(decimal clientRate)
        => new(clientRate, clientRate, 0m, 0m, DefaultTolerancePct, ExchangeRateAnchorOutcome.NoOfficialRate);

    private void LogWarning(string message, params object?[] args)
    {
        if (_logger != null)
        {
            _logger.LogWarning(message, args);
            return;
        }

        string formatted;
        try
        {
            formatted = string.Format(CultureInfo.InvariantCulture, message, args);
        }
        catch (FormatException)
        {
            formatted = message;
        }
        AppLogger.LogWarn(formatted, args.Length > 0 ? args[0]?.ToString() ?? "General" : "General");
    }

    private void LogError(Exception ex, string message, params object?[] args)
    {
        if (_logger != null)
        {
            _logger.LogError(ex, message, args);
            return;
        }

        string formatted;
        try
        {
            formatted = string.Format(CultureInfo.InvariantCulture, message, args);
        }
        catch (FormatException)
        {
            formatted = message;
        }
        AppLogger.LogDbError(ex, args.Length > 0 ? args[0]?.ToString() ?? "General" : "General");
        AppLogger.LogWarn($"{formatted} {ex.Message}", args.Length > 0 ? args[0]?.ToString() ?? "General" : "General");
    }
}
