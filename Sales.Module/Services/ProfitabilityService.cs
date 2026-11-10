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
/// Paso 14 (F2-S1/S4): fachada del Estado de Resultados. Resuelve el contexto (ventana UTC, zona
/// horaria y moneda de reporte) e integra el motor devengado de WU2; el bloque percibido y los
/// desgloses se incorporan en WU3/WU4 sobre las mismas dependencias.
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
    /// Paso 14 (F2-WU2/WU3): context shared with the partial engine files so the accrual/realized
    /// engines can reuse the same tracked-free query surface.
    /// </summary>
    internal SalesDbContext Context => _context;

    public async Task<ProfitabilitySummaryDto> GetSummaryAsync(
        ProfitabilityFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // F2-WU4 owns the category catalog (including the subtree). Until then the filter fails
        // explicitly instead of silently returning unfiltered data.
        if (filter.CategoryIds is { Count: > 0 })
        {
            throw new NotSupportedException("El filtro por categoría se habilita en F2-WU4.");
        }

        var window = await ProfitabilityTimeWindow.ResolveAsync(
            filter,
            _timeZoneProvider,
            cancellationToken: cancellationToken);

        var reportCurrency = await ResolveReportCurrencyAsync(cancellationToken);
        var accruedResult = await BuildAccruedAsync(
            filter,
            window,
            reportCurrency,
            cancellationToken);

        // WU3-WU4 fill the realized block and breakdowns; the empty structures keep the response
        // contract stable while those engines land.
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
            Accrued = accruedResult.Accrued,
            Realized = new ProfitabilityRealizedDto(),
            Coverage = accruedResult.Coverage,
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
