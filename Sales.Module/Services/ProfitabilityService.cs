using Core.DTOs;
using Core.Interfaces;
using Sales.Module.Data;
using Sales.Module.DTOs.Profitability;
using Sales.Module.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// Paso 14 (F2-S1/S4): fachada del Estado de Resultados. Este work unit resuelve el contexto
/// (ventana UTC, zona horaria y moneda de reporte); los motores devengado/percibido y los
/// desgloses se incorporan en WU2/WU3/WU4 sobre las mismas dependencias.
/// </summary>
public partial class ProfitabilityService : IProfitabilityService
{
    private readonly SalesDbContext _context;
    private readonly ITimeZoneProvider _timeZoneProvider;
    // Dual reports in USD; Single reports in its primary currency. Optional so direct test
    // compositions and installations without the monetary profile keep the historical USD/VES
    // behavior instead of failing the report.
    private readonly IMonetaryProfileService? _monetaryProfileService;

    public ProfitabilityService(
        SalesDbContext context,
        ITimeZoneProvider timeZoneProvider,
        IMonetaryProfileService? monetaryProfileService = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeZoneProvider);

        _context = context;
        _timeZoneProvider = timeZoneProvider;
        _monetaryProfileService = monetaryProfileService;
    }

    /// <summary>
    /// Paso 14 (F2-WU2/WU3): context shared with the partial engine files. The skeleton does not
    /// query yet; exposing it here keeps the dependency alive for the accrual/realized engines.
    /// </summary>
    internal SalesDbContext Context => _context;

    public async Task<ProfitabilitySummaryDto> GetSummaryAsync(
        ProfitabilityFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var window = await ProfitabilityTimeWindow.ResolveAsync(
            filter,
            _timeZoneProvider,
            cancellationToken: cancellationToken);

        var reportCurrency = await ResolveReportCurrencyAsync(cancellationToken);

        // WU2-WU4 fill the KPI blocks and breakdowns; the empty structures keep the response
        // contract stable from the first work unit.
        return new ProfitabilitySummaryDto
        {
            FromUtc = window.FromUtc,
            ToUtc = window.ToUtc,
            TimeZoneId = window.TimeZoneId,
            MonetaryMode = reportCurrency.Mode,
            ReportCurrencyCode = reportCurrency.Code,
            ReportCurrencySymbol = reportCurrency.Symbol,
            IsSingleCurrency = reportCurrency.Mode == MonetaryMode.Single,
            GeneratedAtUtc = DateTime.UtcNow,
            GlobalCostsNote = ProfitabilitySummaryDto.GlobalCostsNoteText,
            Accrued = new ProfitabilityAccruedDto(),
            Realized = new ProfitabilityRealizedDto(),
            Coverage = new ProfitabilityCoverageDto(),
            Breakdown = new ProfitabilityBreakdownDto(),
            Alerts = Array.Empty<ProfitabilityAlertDto>()
        };
    }

    private async Task<ReportCurrencyContext> ResolveReportCurrencyAsync(CancellationToken cancellationToken)
    {
        if (_monetaryProfileService is null)
        {
            return ReportCurrencyContext.DualUsd;
        }

        var profile = await _monetaryProfileService.GetAsync(cancellationToken);
        if (profile.Mode == MonetaryMode.Single)
        {
            var primaryCode = string.IsNullOrWhiteSpace(profile.PrimaryCurrencyCode)
                ? ReportCurrencyContext.DualUsd.Code
                : profile.PrimaryCurrencyCode;
            var primarySymbol = string.IsNullOrWhiteSpace(profile.PrimaryCurrencySymbol)
                ? primaryCode
                : profile.PrimaryCurrencySymbol;

            return new ReportCurrencyContext(MonetaryMode.Single, primaryCode, primarySymbol);
        }

        // Dual reports against USD, the primary currency of the regime.
        return ReportCurrencyContext.DualUsd;
    }

    private sealed record ReportCurrencyContext(MonetaryMode Mode, string Code, string Symbol)
    {
        public static readonly ReportCurrencyContext DualUsd = new(MonetaryMode.Dual, "USD", "$");
    }
}
