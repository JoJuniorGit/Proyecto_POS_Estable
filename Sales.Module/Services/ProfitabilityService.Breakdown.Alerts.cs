using Sales.Module.DTOs.Profitability;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// Paso 14 (F2-WU4, F2-S4): alertas de erosión de margen. La comparación usa la misma ventana
/// sintética de igual duración inmediatamente anterior, con el mismo filtro efectivo.
/// </summary>
public partial class ProfitabilityService
{
    private async Task<IReadOnlyList<ProfitabilityAlertDto>> BuildAlertsAsync(
        ProfitabilityFilterDto filter,
        ProfitabilityTimeWindowResult window,
        ReportCurrencyContext reportCurrency,
        decimal currentGrossMargin,
        CancellationToken cancellationToken)
    {
        var duration = window.ToUtc - window.FromUtc;
        if (duration <= TimeSpan.Zero)
        {
            return Array.Empty<ProfitabilityAlertDto>();
        }

        var previousWindow = new ProfitabilityTimeWindowResult(
            window.FromUtc - duration,
            window.FromUtc,
            window.TimeZoneId);
        var previous = await BuildAccruedAsync(filter, previousWindow, reportCurrency, cancellationToken);

        // A prior period without known-cost revenue has no comparable gross margin; alerting
        // would manufacture an erosion that the data cannot support.
        if (previous.Accrued.KnownCostRevenue <= 0m)
        {
            return Array.Empty<ProfitabilityAlertDto>();
        }

        var previousGrossMargin = previous.Accrued.GrossMarginPercent;
        var delta = previousGrossMargin - currentGrossMargin;
        if (delta < 5m)
        {
            return Array.Empty<ProfitabilityAlertDto>();
        }

        var severity = delta >= 10m
            ? ProfitabilityAlertSeverity.Critical
            : ProfitabilityAlertSeverity.Warning;
        var deltaLabel = delta.ToString("0.#", CultureInfo.InvariantCulture);
        var message = severity == ProfitabilityAlertSeverity.Critical
            ? $"Erosión crítica de margen: cayó {deltaLabel} puntos porcentuales frente al período anterior."
            : $"El margen bruto devengado cayó {deltaLabel} puntos porcentuales frente al período anterior.";

        return new[]
        {
            new ProfitabilityAlertDto
            {
                Code = "MARGIN_EROSION",
                Severity = severity,
                Message = message,
                CurrentValue = currentGrossMargin,
                PreviousValue = previousGrossMargin,
                Delta = delta
            }
        };
    }
}
