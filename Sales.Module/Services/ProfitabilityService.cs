using Core.DTOs;
using Core.Interfaces;
using Sales.Module.Data;
using Sales.Module.DTOs.Profitability;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// Paso 14 (F2-S1/S4): fachada del Estado de Resultados. Resuelve el contexto (ventana UTC, zona
/// horaria y moneda de reporte), traduce el filtro de categoría con el catálogo de inventario e
/// integra los motores devengado, percibido, los desgloses y las alertas de erosión de margen.
/// </summary>
public partial class ProfitabilityService : IProfitabilityService
{
    // F2-WU4: a category filter that resolves to no products must mean an empty sales scope, not
    // "no product filter". This id cannot collide with a real product and keeps the engines intact.
    private static readonly IReadOnlyList<int> EmptyProductScope = new[] { int.MinValue };

    private readonly SalesDbContext _context;
    private readonly ITimeZoneProvider _timeZoneProvider;
    // Dual reports in USD; Single reports in its primary currency. Optional so direct test
    // compositions and installations without the monetary profile keep the historical USD/VES
    // behavior instead of failing the report.
    private readonly IMonetaryProfileService? _monetaryProfileService;
    // F2-WU4: optional catalog dependency. Direct compositions without it keep working for every
    // report except a category filter, which fails closed with a clear configuration message.
    private readonly IInventoryService? _inventoryService;

    public ProfitabilityService(
        SalesDbContext context,
        ITimeZoneProvider timeZoneProvider,
        IMonetaryProfileService? monetaryProfileService = null,
        IInventoryService? inventoryService = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeZoneProvider);

        _context = context;
        _timeZoneProvider = timeZoneProvider;
        _monetaryProfileService = monetaryProfileService;
        _inventoryService = inventoryService;
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

        // F2-WU4: the category catalog is resolved before any engine runs; downstream engines
        // receive only effective product ids and never see CategoryIds.
        var effectiveFilter = await ResolveEffectiveFilterAsync(filter, cancellationToken);

        var window = await ProfitabilityTimeWindow.ResolveAsync(
            filter,
            _timeZoneProvider,
            cancellationToken: cancellationToken);

        var reportCurrency = await ResolveReportCurrencyAsync(cancellationToken);
        var accruedResult = await BuildAccruedAsync(
            effectiveFilter,
            window,
            reportCurrency,
            cancellationToken);
        var realizedResult = await BuildRealizedAsync(
            effectiveFilter,
            window,
            reportCurrency,
            cancellationToken);
        var breakdown = await BuildBreakdownAsync(
            effectiveFilter,
            window,
            reportCurrency,
            cancellationToken);
        var alerts = await BuildAlertsAsync(
            effectiveFilter,
            window,
            reportCurrency,
            accruedResult.Accrued.GrossMarginPercent,
            cancellationToken);

        // F3-S1: the breakdown endpoint exposes the same alerts as the summary.
        breakdown = breakdown with { Alerts = alerts };

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
            Realized = realizedResult,
            Coverage = accruedResult.Coverage,
            Breakdown = breakdown,
            Alerts = alerts
        };
    }

    public async Task<ProfitabilityBreakdownDto> GetBreakdownAsync(
        ProfitabilityFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var summary = await GetSummaryAsync(filter, cancellationToken);
        return summary.Breakdown;
    }

    private async Task<ProfitabilityFilterDto> ResolveEffectiveFilterAsync(
        ProfitabilityFilterDto filter,
        CancellationToken cancellationToken)
    {
        if (filter.CategoryIds is not { Count: > 0 })
        {
            return filter;
        }

        if (_inventoryService is null)
        {
            throw new InvalidOperationException(
                "El catálogo de productos no está disponible para resolver el filtro por categoría.");
        }

        var categoryProductIds = await _inventoryService.GetProductIdsByCategoryAsync(
            filter.CategoryIds,
            filter.IncludeCategorySubtree,
            cancellationToken);

        IReadOnlyList<int> effectiveProductIds;
        if (filter.ProductIds is { Count: > 0 } productIds)
        {
            var categorySet = new HashSet<int>(categoryProductIds);
            effectiveProductIds = productIds
                .Where(categorySet.Contains)
                .Distinct()
                .ToList();
        }
        else
        {
            effectiveProductIds = categoryProductIds;
        }

        if (effectiveProductIds.Count == 0)
        {
            effectiveProductIds = EmptyProductScope;
        }

        return CopyFilterWithProductIds(filter, effectiveProductIds);
    }

    private static ProfitabilityFilterDto CopyFilterWithProductIds(
        ProfitabilityFilterDto filter,
        IReadOnlyList<int> productIds) => new()
    {
        Preset = filter.Preset,
        FromDate = filter.FromDate,
        ToDate = filter.ToDate,
        // F2-WU4: the engines work on effective product ids; keeping the raw category ids would
        // make downstream code re-resolve the catalog or reject the filter.
        CategoryIds = null,
        IncludeCategorySubtree = filter.IncludeCategorySubtree,
        ProductIds = productIds,
        PaymentMethodIds = filter.PaymentMethodIds,
        CashierId = filter.CashierId,
        SaleType = filter.SaleType,
        PriceListType = filter.PriceListType,
        GroupBy = filter.GroupBy
    };

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
