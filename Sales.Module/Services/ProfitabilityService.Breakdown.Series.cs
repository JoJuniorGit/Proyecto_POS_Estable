using Microsoft.EntityFrameworkCore;
using Sales.Module.DTOs.Profitability;
using Sales.Module.Entities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// Paso 14 (F2-WU4, F2-S4): serie temporal. Cada evento se agrega en el período local que
/// contiene su timestamp (fecha de venta, devolución, comisión o caja chica); los períodos vacíos
/// se rellenan con ceros y los huecos DST de primavera se omiten en la agrupación horaria.
/// </summary>
public partial class ProfitabilityService
{
    private async Task<IReadOnlyList<ProfitabilitySeriesPointDto>> BuildSeriesAsync(
        ProfitabilityFilterDto filter,
        ProfitabilityTimeWindowResult window,
        TimeZoneInfo timeZone,
        bool isSingle,
        CancellationToken cancellationToken)
    {
        var buckets = new Dictionary<DateTime, SeriesAccumulator>();
        var hasAnyEvent = await AddAccruedSeriesEventsAsync(
            buckets, filter, window, timeZone, isSingle, cancellationToken);
        hasAnyEvent |= await AddRealizedSeriesEventsAsync(
            buckets, filter, window, timeZone, isSingle, cancellationToken);

        if (!hasAnyEvent)
        {
            // F2-WU1 contract: an empty report returns empty breakdown collections instead of a
            // synthetic zero timeline.
            return Array.Empty<ProfitabilitySeriesPointDto>();
        }

        return FillSeriesPoints(buckets, window, timeZone, filter.GroupBy);
    }

    private async Task<bool> AddAccruedSeriesEventsAsync(
        IDictionary<DateTime, SeriesAccumulator> buckets,
        ProfitabilityFilterDto filter,
        ProfitabilityTimeWindowResult window,
        TimeZoneInfo timeZone,
        bool isSingle,
        CancellationToken cancellationToken)
    {
        var fromUtc = window.FromUtc;
        var toUtc = window.ToUtc;
        var hasAnyEvent = false;
        var filteredSales = ApplySaleFilters(_context.Sales.AsNoTracking(), filter);
        var merchandiseSales = filteredSales.Where(sale => sale.SaleType == SaleType.Merchandise);

        var grossSalesQuery = filteredSales.Where(sale =>
            sale.SaleType == SaleType.Merchandise
            && sale.InvoiceNumber != null
            && sale.Date >= fromUtc
            && sale.Date < toUtc
            && (sale.CancelledAtUtc == null || sale.CancelledAtUtc >= fromUtc));
        var cancelledSalesQuery = filteredSales.Where(sale =>
            sale.SaleType == SaleType.Merchandise
            && sale.InvoiceNumber != null
            && sale.CancelledAtUtc >= fromUtc
            && sale.CancelledAtUtc < toUtc
            && !_context.CreditReturns.Any(creditReturn =>
                creditReturn.SaleId == sale.Id
                && creditReturn.CreatedAtUtc >= fromUtc
                && creditReturn.CreatedAtUtc < toUtc));

        var grossSales = await LoadBreakdownSaleRowsAsync(grossSalesQuery, cancellationToken);
        var cancelledSales = await LoadBreakdownSaleRowsAsync(cancelledSalesQuery, cancellationToken);
        hasAnyEvent |= grossSales.Count > 0 || cancelledSales.Count > 0;

        var saleIds = grossSales.Select(sale => sale.Id)
            .Concat(cancelledSales.Select(sale => sale.Id))
            .Distinct()
            .ToList();
        var saleItems = await LoadBreakdownItemRowsAsync(saleIds, filter.ProductIds, cancellationToken);
        var itemsBySale = saleItems
            .GroupBy(item => item.SaleId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<BreakdownSaleItemRow>)group.ToList());

        foreach (var sale in grossSales)
        {
            var (netSales, grossProfit) = SaleContribution(
                sale, itemsBySale.GetValueOrDefault(sale.Id), HasProductFilter(filter), isSingle);
            AddSeriesAmounts(buckets, sale.Date, timeZone, filter.GroupBy,
                accruedNetSales: netSales, accruedNetProfit: grossProfit);
        }

        foreach (var sale in cancelledSales)
        {
            var (netSales, grossProfit) = SaleContribution(
                sale, itemsBySale.GetValueOrDefault(sale.Id), HasProductFilter(filter), isSingle);
            AddSeriesAmounts(buckets, sale.CancelledAtUtc!.Value, timeZone, filter.GroupBy,
                accruedNetSales: -netSales, accruedNetProfit: -grossProfit);
        }

        var returnRows = await LoadBreakdownReturnRowsAsync(merchandiseSales, fromUtc, toUtc, cancellationToken);
        var returnItems = await LoadBreakdownReturnItemRowsAsync(
            merchandiseSales, fromUtc, toUtc, filter.ProductIds, cancellationToken);
        hasAnyEvent |= returnRows.Count > 0;
        var returnItemsByReturn = returnItems
            .GroupBy(item => item.CreditReturnId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<BreakdownReturnItemRow>)group.ToList());

        foreach (var creditReturn in returnRows)
        {
            var items = returnItemsByReturn.GetValueOrDefault(creditReturn.CreditReturnId);
            var netSales = HasProductFilter(filter)
                ? items?.Sum(item => ReturnedItemRevenue(item, isSingle)) ?? 0m
                : ReturnedSaleAmount(creditReturn.ReturnedUSD, creditReturn.AppliedRate, isSingle);
            var grossProfit = items?.Sum(item => ReturnedItemGrossProfit(item, isSingle)) ?? 0m;
            AddSeriesAmounts(buckets, creditReturn.CreatedAtUtc, timeZone, filter.GroupBy,
                accruedNetSales: -netSales, accruedNetProfit: -grossProfit);
        }

        var advanceCommissions = await LoadAdvanceCommissionRowsAsync(
            filteredSales, fromUtc, toUtc, isSingle, cancellationToken);
        hasAnyEvent |= advanceCommissions.Count > 0;
        foreach (var commission in advanceCommissions)
        {
            AddSeriesAmounts(buckets, commission.EventAtUtc, timeZone, filter.GroupBy,
                accruedNetProfit: commission.AmountLocalOrUsd,
                realizedNetProfit: commission.AmountLocalOrUsd);
        }

        var pettyCashRows = await LoadPettyCashEffectRowsAsync(fromUtc, toUtc, isSingle, cancellationToken);
        hasAnyEvent |= pettyCashRows.Count > 0;
        foreach (var expense in pettyCashRows)
        {
            AddSeriesAmounts(buckets, expense.EventAtUtc, timeZone, filter.GroupBy,
                accruedNetProfit: expense.ProfitEffect,
                realizedNetProfit: expense.ProfitEffect);
        }

        var accruedFeeRows = await LoadAccruedFeeEffectRowsAsync(
            fromUtc,
            toUtc,
            filter.PaymentMethodIds ?? Array.Empty<int>(),
            hasPaymentMethodFilter: filter.PaymentMethodIds is { Count: > 0 },
            isSingle,
            cancellationToken);
        hasAnyEvent |= accruedFeeRows.Count > 0;
        foreach (var fee in accruedFeeRows)
        {
            AddSeriesAmounts(buckets, fee.EventAtUtc, timeZone, filter.GroupBy,
                accruedNetProfit: fee.ProfitEffect);
        }

        return hasAnyEvent;
    }

    private static async Task<List<BreakdownCommissionRow>> LoadAdvanceCommissionRowsAsync(
        IQueryable<Sale> filteredSales,
        DateTime fromUtc,
        DateTime toUtc,
        bool isSingle,
        CancellationToken cancellationToken)
    {
        var earned = await filteredSales
            .Where(sale =>
                sale.SaleType == SaleType.CashAdvance
                && sale.InvoiceNumber != null
                && sale.Date >= fromUtc
                && sale.Date < toUtc
                && (sale.CancelledAtUtc == null || sale.CancelledAtUtc >= fromUtc))
            .Select(sale => new BreakdownCommissionRow
            {
                EventAtUtc = sale.Date,
                CommissionUSD = sale.CommissionAmountUSD,
                CommissionLocal = sale.CommissionAmountLocal,
                Sign = 1m
            })
            .ToListAsync(cancellationToken);

        var cancelled = await filteredSales
            .Where(sale =>
                sale.SaleType == SaleType.CashAdvance
                && sale.InvoiceNumber != null
                && sale.CancelledAtUtc >= fromUtc
                && sale.CancelledAtUtc < toUtc)
            .Select(sale => new BreakdownCommissionRow
            {
                EventAtUtc = sale.CancelledAtUtc!.Value,
                CommissionUSD = sale.CommissionAmountUSD,
                CommissionLocal = sale.CommissionAmountLocal,
                Sign = -1m
            })
            .ToListAsync(cancellationToken);

        earned.AddRange(cancelled);
        foreach (var row in earned)
        {
            row.AmountLocalOrUsd = row.Sign * (isSingle ? row.CommissionLocal : row.CommissionUSD);
        }

        return earned;
    }

    private async Task<List<BreakdownPettyCashRow>> LoadPettyCashEffectRowsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        bool isSingle,
        CancellationToken cancellationToken)
    {
        var active = await _context.PettyCashExpenses.AsNoTracking()
            .Where(expense =>
                expense.Status == PettyCashStatus.Active
                && expense.CreatedAtUtc >= fromUtc
                && expense.CreatedAtUtc < toUtc)
            .Select(expense => new BreakdownPettyCashRow
            {
                EventAtUtc = expense.CreatedAtUtc,
                AmountUsd = expense.AmountUsd,
                AmountBsS = expense.AmountBsS,
                Sign = -1m
            })
            .ToListAsync(cancellationToken);

        // A cancellation reverses an expense recognized in an earlier period; an expense created
        // and cancelled inside the same window was never counted (Status is already Cancelled).
        var reversed = await _context.PettyCashExpenses.AsNoTracking()
            .Where(expense =>
                expense.Status == PettyCashStatus.Cancelled
                && expense.CancelledAtUtc >= fromUtc
                && expense.CancelledAtUtc < toUtc
                && expense.CreatedAtUtc < fromUtc)
            .Select(expense => new BreakdownPettyCashRow
            {
                EventAtUtc = expense.CancelledAtUtc!.Value,
                AmountUsd = expense.AmountUsd,
                AmountBsS = expense.AmountBsS,
                Sign = 1m
            })
            .ToListAsync(cancellationToken);

        active.AddRange(reversed);
        foreach (var row in active)
        {
            row.ProfitEffect = row.Sign * (isSingle ? row.AmountBsS : row.AmountUsd);
        }

        return active;
    }

    private static void AddSeriesAmounts(
        IDictionary<DateTime, SeriesAccumulator> buckets,
        DateTime eventAtUtc,
        TimeZoneInfo timeZone,
        ProfitabilityGroupBy groupBy,
        decimal accruedNetSales = 0m,
        decimal accruedNetProfit = 0m,
        decimal realizedCollectedRevenue = 0m,
        decimal realizedNetProfit = 0m)
    {
        var periodStartUtc = PeriodStartUtc(eventAtUtc, timeZone, groupBy);
        if (!buckets.TryGetValue(periodStartUtc, out var accumulator))
        {
            accumulator = new SeriesAccumulator();
            buckets[periodStartUtc] = accumulator;
        }

        accumulator.AccruedNetSales += accruedNetSales;
        accumulator.AccruedNetProfit += accruedNetProfit;
        accumulator.RealizedCollectedRevenue += realizedCollectedRevenue;
        accumulator.RealizedNetProfit += realizedNetProfit;
    }

    private static IReadOnlyList<ProfitabilitySeriesPointDto> FillSeriesPoints(
        IReadOnlyDictionary<DateTime, SeriesAccumulator> buckets,
        ProfitabilityTimeWindowResult window,
        TimeZoneInfo timeZone,
        ProfitabilityGroupBy groupBy)
    {
        var localFrom = TimeZoneInfo.ConvertTimeFromUtc(EnsureUtc(window.FromUtc), timeZone);
        var localLast = TimeZoneInfo.ConvertTimeFromUtc(EnsureUtc(window.ToUtc).AddTicks(-1), timeZone);
        var cursor = LocalPeriodStart(localFrom, groupBy);
        var last = LocalPeriodStart(localLast, groupBy);
        var points = new List<ProfitabilitySeriesPointDto>();

        while (cursor <= last)
        {
            // Spring-forward gap (e.g. 02:00 -> 03:00): the local hour does not exist, so it has
            // no UTC instant and must be skipped instead of asking the runtime to convert it.
            if (timeZone.IsInvalidTime(cursor))
            {
                cursor = AdvanceLocalPeriod(cursor, groupBy);
                continue;
            }

            var periodStartUtc = LocalPeriodToUtc(cursor, timeZone);
            buckets.TryGetValue(periodStartUtc, out var accumulator);
            points.Add(new ProfitabilitySeriesPointDto
            {
                PeriodStartUtc = periodStartUtc,
                Label = SeriesLabel(cursor, groupBy),
                AccruedNetProfit = accumulator?.AccruedNetProfit ?? 0m,
                RealizedNetProfit = accumulator?.RealizedNetProfit ?? 0m,
                AccruedNetSales = accumulator?.AccruedNetSales ?? 0m,
                RealizedCollectedRevenue = accumulator?.RealizedCollectedRevenue ?? 0m
            });

            cursor = AdvanceLocalPeriod(cursor, groupBy);
        }

        return points;
    }

    private static DateTime PeriodStartUtc(
        DateTime eventAtUtc,
        TimeZoneInfo timeZone,
        ProfitabilityGroupBy groupBy)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(EnsureUtc(eventAtUtc), timeZone);
        return LocalPeriodToUtc(LocalPeriodStart(local, groupBy), timeZone);
    }

    private static DateTime LocalPeriodStart(DateTime local, ProfitabilityGroupBy groupBy) => groupBy switch
    {
        ProfitabilityGroupBy.Hour => new DateTime(
            local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified),
        ProfitabilityGroupBy.Week => local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7)),
        ProfitabilityGroupBy.Month => new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified),
        _ => local.Date
    };

    private static DateTime AdvanceLocalPeriod(DateTime local, ProfitabilityGroupBy groupBy) => groupBy switch
    {
        ProfitabilityGroupBy.Hour => local.AddHours(1),
        ProfitabilityGroupBy.Week => local.AddDays(7),
        ProfitabilityGroupBy.Month => local.AddMonths(1),
        _ => local.AddDays(1)
    };

    private static string SeriesLabel(DateTime localStart, ProfitabilityGroupBy groupBy) => groupBy switch
    {
        ProfitabilityGroupBy.Hour => localStart.ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture),
        ProfitabilityGroupBy.Week => $"Semana del {localStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
        ProfitabilityGroupBy.Month => localStart.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        _ => localStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    };

    private static DateTime LocalPeriodToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(unspecified))
        {
            // Defensive normalization for callers outside the fill loop: move to the first valid
            // minute of the spring-forward gap so ConvertTimeToUtc never receives an invalid time.
            var limit = unspecified.AddHours(2);
            while (unspecified < limit && timeZone.IsInvalidTime(unspecified))
            {
                unspecified = unspecified.AddMinutes(1);
            }
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone);
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private sealed class SeriesAccumulator
    {
        public decimal AccruedNetSales { get; set; }

        public decimal AccruedNetProfit { get; set; }

        public decimal RealizedCollectedRevenue { get; set; }

        public decimal RealizedNetProfit { get; set; }
    }

    private sealed class BreakdownCommissionRow
    {
        public DateTime EventAtUtc { get; init; }

        public decimal CommissionUSD { get; init; }

        public decimal CommissionLocal { get; init; }

        public decimal Sign { get; init; }

        public decimal AmountLocalOrUsd { get; set; }
    }

    private sealed class BreakdownPettyCashRow
    {
        public DateTime EventAtUtc { get; init; }

        public decimal AmountUsd { get; init; }

        public decimal AmountBsS { get; init; }

        public decimal Sign { get; init; }

        public decimal ProfitEffect { get; set; }
    }
}
