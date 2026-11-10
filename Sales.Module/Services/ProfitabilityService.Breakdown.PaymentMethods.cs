using Microsoft.EntityFrameworkCore;
using Sales.Module.DTOs.Profitability;
using Sales.Module.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// Paso 14 (F2-WU4, F2-S4): desglose por método de pago y eventos realizados de la serie. Las
/// reversas S10 de comisión se restan del método original y una fila de método se conserva aunque
/// la reversa sea su única actividad del período; así la suma de <c>FeeAmount</c> reconcilia con
/// <c>PaymentMethodFees</c> del resumen.
/// </summary>
public partial class ProfitabilityService
{
    private async Task<IReadOnlyList<PaymentMethodBreakdownDto>> BuildPaymentMethodBreakdownAsync(
        ProfitabilityFilterDto filter,
        ProfitabilityTimeWindowResult window,
        bool isSingle,
        CancellationToken cancellationToken)
    {
        var fromUtc = window.FromUtc;
        var toUtc = window.ToUtc;
        var paymentMethodIds = filter.PaymentMethodIds ?? Array.Empty<int>();
        var hasPaymentMethodFilter = paymentMethodIds.Count > 0;

        var salePaymentQuery =
            from payment in _context.SalePayments.AsNoTracking()
            join method in _context.PaymentMethods.AsNoTracking()
                on payment.PaymentMethodId equals method.Id into methods
            from method in methods.DefaultIfEmpty()
            where payment.CreatedAt >= fromUtc && payment.CreatedAt < toUtc
            select new PaymentMethodRow
            {
                PaymentMethodId = payment.PaymentMethodId,
                PaymentMethodName = method != null ? method.Name : null,
                IsCash = method != null && method.IsCash,
                Amount = payment.Amount,
                AmountLocal = payment.AmountBsS,
                FeeUsd = payment.FeeAmountUsd,
                FeeLocal = payment.FeeAmountLocal,
                FeeConfigured = payment.FeeConfigured
            };

        var receivablePaymentQuery =
            from payment in _context.ReceivablePayments.AsNoTracking()
            join method in _context.PaymentMethods.AsNoTracking()
                on payment.PaymentMethodId equals (int?)method.Id into methods
            from method in methods.DefaultIfEmpty()
            where payment.ReceivedAtUtc >= fromUtc && payment.ReceivedAtUtc < toUtc
            select new PaymentMethodRow
            {
                PaymentMethodId = payment.PaymentMethodId ?? 0,
                PaymentMethodName = method != null ? method.Name : null,
                IsCash = method != null && method.IsCash,
                Amount = payment.AmountUSD,
                AmountLocal = payment.AmountBsS,
                FeeUsd = payment.FeeAmountUsd,
                FeeLocal = payment.FeeAmountLocal,
                FeeConfigured = payment.FeeConfigured
            };

        var reversalQuery =
            from payment in _context.SalePayments.AsNoTracking()
            join sale in _context.Sales.AsNoTracking() on payment.SaleId equals sale.Id
            join method in _context.PaymentMethods.AsNoTracking()
                on payment.PaymentMethodId equals method.Id into methods
            from method in methods.DefaultIfEmpty()
            where sale.InvoiceNumber != null
                && sale.CancelledAtUtc >= fromUtc
                && sale.CancelledAtUtc < toUtc
            select new PaymentMethodRow
            {
                PaymentMethodId = payment.PaymentMethodId,
                PaymentMethodName = method != null ? method.Name : null,
                IsCash = method != null && method.IsCash,
                // Reversals only move the fee line; they are not current-period collections and
                // must not alter the fee-coverage payment bases.
                Amount = 0m,
                AmountLocal = 0m,
                FeeUsd = -payment.FeeAmountUsd,
                FeeLocal = -payment.FeeAmountLocal,
                FeeConfigured = payment.FeeConfigured
            };

        if (hasPaymentMethodFilter)
        {
            salePaymentQuery = salePaymentQuery.Where(row =>
                paymentMethodIds.Contains(row.PaymentMethodId));
            receivablePaymentQuery = receivablePaymentQuery.Where(row =>
                row.PaymentMethodId != 0 && paymentMethodIds.Contains(row.PaymentMethodId));
            reversalQuery = reversalQuery.Where(row =>
                paymentMethodIds.Contains(row.PaymentMethodId));
        }

        var rows = await salePaymentQuery.ToListAsync(cancellationToken);
        rows.AddRange(await receivablePaymentQuery.ToListAsync(cancellationToken));
        rows.AddRange(await reversalQuery.ToListAsync(cancellationToken));

        return rows
            .GroupBy(row => row.PaymentMethodId)
            .Select(group =>
            {
                var isCash = group.Any(row => row.IsCash);
                var collected = group.Sum(row => isSingle ? row.AmountLocal : row.Amount);
                var knownFeeAmount = isCash
                    ? 0m
                    : group.Where(row => row.FeeConfigured)
                        .Sum(row => isSingle ? row.AmountLocal : row.Amount);
                var unknownFeeAmount = isCash
                    ? 0m
                    : group.Where(row => !row.FeeConfigured)
                        .Sum(row => isSingle ? row.AmountLocal : row.Amount);
                var feeAmount = isCash
                    ? 0m
                    : group.Sum(row => isSingle ? row.FeeLocal : row.FeeUsd);
                var feeBase = knownFeeAmount + unknownFeeAmount;
                var feeCoverage = isCash
                    ? 100m
                    : feeBase > 0m
                        ? knownFeeAmount / feeBase * 100m
                        : 0m;

                return new PaymentMethodBreakdownDto
                {
                    PaymentMethodId = group.Key,
                    PaymentMethodName = group.Select(row => row.PaymentMethodName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                        ?? (group.Key == 0 ? "Sin método de pago" : $"Método {group.Key}"),
                    CollectedAmount = collected,
                    FeeAmount = feeAmount,
                    KnownFeeAmount = knownFeeAmount,
                    UnknownFeeAmount = unknownFeeAmount,
                    FeeCoveragePercent = feeCoverage
                };
            })
            .OrderByDescending(row => row.CollectedAmount)
            .ThenBy(row => row.PaymentMethodId)
            .ToList();
    }

    private async Task<bool> AddRealizedSeriesEventsAsync(
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
        var paymentMethodIds = filter.PaymentMethodIds ?? Array.Empty<int>();
        var hasPaymentMethodFilter = paymentMethodIds.Count > 0;
        var merchandiseIssuedSales = filteredSales.Where(sale =>
            sale.SaleType == SaleType.Merchandise && sale.InvoiceNumber != null);

        var salePaymentSource = _context.SalePayments.AsNoTracking();
        if (hasPaymentMethodFilter)
        {
            salePaymentSource = salePaymentSource.Where(payment =>
                paymentMethodIds.Contains(payment.PaymentMethodId));
        }

        var saleCollections = await (
                from payment in salePaymentSource
                join sale in merchandiseIssuedSales on payment.SaleId equals sale.Id
                where payment.CreatedAt >= fromUtc && payment.CreatedAt < toUtc
                select new BreakdownCollectionRow
                {
                    SaleId = sale.Id,
                    EventAtUtc = payment.CreatedAt,
                    AmountUsd = payment.Amount,
                    AmountLocal = payment.AmountBsS
                })
            .ToListAsync(cancellationToken);

        var receivablePaymentSource = _context.ReceivablePayments.AsNoTracking();
        if (hasPaymentMethodFilter)
        {
            receivablePaymentSource = receivablePaymentSource.Where(payment =>
                payment.PaymentMethodId.HasValue
                && paymentMethodIds.Contains(payment.PaymentMethodId.Value));
        }

        var allocationCollections = await (
                from allocation in _context.CreditPaymentAllocations.AsNoTracking()
                join payment in receivablePaymentSource
                    on allocation.ReceivablePaymentId equals payment.Id
                join receivable in _context.Receivables.AsNoTracking()
                    on allocation.ReceivableId equals receivable.Id
                join sale in merchandiseIssuedSales on receivable.SaleId equals sale.Id
                where payment.ReceivedAtUtc >= fromUtc && payment.ReceivedAtUtc < toUtc
                select new BreakdownCollectionRow
                {
                    SaleId = sale.Id,
                    EventAtUtc = payment.ReceivedAtUtc,
                    AmountUsd = allocation.AmountUSD,
                    AmountLocal = allocation.AmountBsS
                })
            .ToListAsync(cancellationToken);

        var refundRows = await (
                from creditReturn in _context.CreditReturns.AsNoTracking()
                join sale in merchandiseIssuedSales on creditReturn.SaleId equals sale.Id
                where creditReturn.CreatedAtUtc >= fromUtc && creditReturn.CreatedAtUtc < toUtc
                select new BreakdownRefundRow
                {
                    SaleId = sale.Id,
                    EventAtUtc = creditReturn.CreatedAtUtc,
                    RefundedUsd = creditReturn.RefundedUSD,
                    RefundedBsS = creditReturn.RefundedBsS
                })
            .ToListAsync(cancellationToken);

        var collections = saleCollections.Concat(allocationCollections).ToList();
        hasAnyEvent |= collections.Count > 0 || refundRows.Count > 0;

        var realizedSaleIds = collections.Select(row => row.SaleId)
            .Concat(refundRows.Select(row => row.SaleId))
            .Distinct()
            .ToList();
        var metricsBySale = await LoadRealizedSaleMetricsAsync(
            realizedSaleIds,
            filter.ProductIds,
            hasProductFilter: HasProductFilter(filter),
            isSingle,
            cancellationToken);

        foreach (var collection in collections)
        {
            if (!metricsBySale.TryGetValue(collection.SaleId, out var metrics))
            {
                continue;
            }

            var amount = isSingle ? collection.AmountLocal : collection.AmountUsd;
            var collected = amount * metrics.PaymentShare;
            var profit = metrics.HasFullCoverage
                ? amount * metrics.PaymentShare - amount * metrics.CogsShare
                : 0m;
            AddSeriesAmounts(buckets, collection.EventAtUtc, timeZone, filter.GroupBy,
                realizedCollectedRevenue: collected, realizedNetProfit: profit);
        }

        foreach (var refund in refundRows)
        {
            if (!metricsBySale.TryGetValue(refund.SaleId, out var metrics))
            {
                continue;
            }

            var amount = isSingle ? refund.RefundedBsS : refund.RefundedUsd;
            var attributed = amount * metrics.RefundShare;
            AddSeriesAmounts(buckets, refund.EventAtUtc, timeZone, filter.GroupBy,
                realizedCollectedRevenue: -attributed,
                realizedNetProfit: metrics.HasFullCoverage ? -attributed : 0m);
        }

        var feeRows = await LoadRealizedFeeEffectRowsAsync(
            salePaymentSource, receivablePaymentSource, filteredSales, fromUtc, toUtc, isSingle, cancellationToken);
        hasAnyEvent |= feeRows.Count > 0;
        foreach (var fee in feeRows)
        {
            AddSeriesAmounts(buckets, fee.EventAtUtc, timeZone, filter.GroupBy,
                realizedNetProfit: fee.ProfitEffect);
        }

        var fxRows = await receivablePaymentSource
            .Where(payment => payment.ReceivedAtUtc >= fromUtc && payment.ReceivedAtUtc < toUtc)
            .Select(payment => new BreakdownFxRow
            {
                EventAtUtc = payment.ReceivedAtUtc,
                FxDifferenceBsS = payment.FxDifferenceBsS,
                PaymentRate = payment.PaymentRate
            })
            .ToListAsync(cancellationToken);
        hasAnyEvent |= fxRows.Count > 0;
        foreach (var fx in fxRows)
        {
            AddSeriesAmounts(buckets, fx.EventAtUtc, timeZone, filter.GroupBy,
                realizedNetProfit: RealizedFx(
                    new RealizedFxRow { FxDifferenceBsS = fx.FxDifferenceBsS, PaymentRate = fx.PaymentRate },
                    isSingle));
        }

        return hasAnyEvent;
    }

    private async Task<List<BreakdownFeeEffectRow>> LoadAccruedFeeEffectRowsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        IReadOnlyList<int> paymentMethodIds,
        bool hasPaymentMethodFilter,
        bool isSingle,
        CancellationToken cancellationToken)
    {
        // Accrued fees obey the same method filter as the headline block so the series totals
        // reconcile with PaymentMethodFees when a method filter is applied.
        var salePaymentSource = _context.SalePayments.AsNoTracking();
        if (hasPaymentMethodFilter)
        {
            salePaymentSource = salePaymentSource.Where(payment =>
                paymentMethodIds.Contains(payment.PaymentMethodId));
        }

        var saleFees = await salePaymentSource
            .Where(payment => payment.CreatedAt >= fromUtc && payment.CreatedAt < toUtc)
            .Select(payment => new BreakdownFeeEffectRow
            {
                EventAtUtc = payment.CreatedAt,
                FeeUsd = payment.FeeAmountUsd,
                FeeLocal = payment.FeeAmountLocal,
                Sign = -1m
            })
            .ToListAsync(cancellationToken);

        var receivablePaymentSource = _context.ReceivablePayments.AsNoTracking();
        if (hasPaymentMethodFilter)
        {
            receivablePaymentSource = receivablePaymentSource.Where(payment =>
                payment.PaymentMethodId.HasValue
                && paymentMethodIds.Contains(payment.PaymentMethodId.Value));
        }

        var receivableFees = await receivablePaymentSource
            .Where(payment => payment.ReceivedAtUtc >= fromUtc && payment.ReceivedAtUtc < toUtc)
            .Select(payment => new BreakdownFeeEffectRow
            {
                EventAtUtc = payment.ReceivedAtUtc,
                FeeUsd = payment.FeeAmountUsd,
                FeeLocal = payment.FeeAmountLocal,
                Sign = -1m
            })
            .ToListAsync(cancellationToken);

        var reversalSource = _context.SalePayments.AsNoTracking();
        if (hasPaymentMethodFilter)
        {
            reversalSource = reversalSource.Where(payment =>
                paymentMethodIds.Contains(payment.PaymentMethodId));
        }

        var reversals = await (
                from payment in reversalSource
                join sale in _context.Sales.AsNoTracking() on payment.SaleId equals sale.Id
                where sale.InvoiceNumber != null
                    && sale.CancelledAtUtc >= fromUtc
                    && sale.CancelledAtUtc < toUtc
                select new BreakdownFeeEffectRow
                {
                    EventAtUtc = sale.CancelledAtUtc!.Value,
                    FeeUsd = payment.FeeAmountUsd,
                    FeeLocal = payment.FeeAmountLocal,
                    Sign = 1m
                })
            .ToListAsync(cancellationToken);

        saleFees.AddRange(receivableFees);
        saleFees.AddRange(reversals);
        foreach (var row in saleFees)
        {
            row.ProfitEffect = row.Sign * (isSingle ? row.FeeLocal : row.FeeUsd);
        }

        return saleFees;
    }

    private async Task<List<BreakdownFeeEffectRow>> LoadRealizedFeeEffectRowsAsync(
        IQueryable<SalePayment> salePaymentSource,
        IQueryable<ReceivablePayment> receivablePaymentSource,
        IQueryable<Sale> filteredSales,
        DateTime fromUtc,
        DateTime toUtc,
        bool isSingle,
        CancellationToken cancellationToken)
    {
        var saleFees = await (
                from payment in salePaymentSource
                join sale in filteredSales on payment.SaleId equals sale.Id
                where payment.CreatedAt >= fromUtc && payment.CreatedAt < toUtc
                select new BreakdownFeeEffectRow
                {
                    EventAtUtc = payment.CreatedAt,
                    FeeUsd = payment.FeeAmountUsd,
                    FeeLocal = payment.FeeAmountLocal,
                    Sign = -1m
                })
            .ToListAsync(cancellationToken);

        var receivableFees = await receivablePaymentSource
            .Where(payment => payment.ReceivedAtUtc >= fromUtc && payment.ReceivedAtUtc < toUtc)
            .Select(payment => new BreakdownFeeEffectRow
            {
                EventAtUtc = payment.ReceivedAtUtc,
                FeeUsd = payment.FeeAmountUsd,
                FeeLocal = payment.FeeAmountLocal,
                Sign = -1m
            })
            .ToListAsync(cancellationToken);

        var reversals = await (
                from payment in salePaymentSource
                join sale in filteredSales on payment.SaleId equals sale.Id
                where sale.InvoiceNumber != null
                    && sale.CancelledAtUtc >= fromUtc
                    && sale.CancelledAtUtc < toUtc
                select new BreakdownFeeEffectRow
                {
                    EventAtUtc = sale.CancelledAtUtc!.Value,
                    FeeUsd = payment.FeeAmountUsd,
                    FeeLocal = payment.FeeAmountLocal,
                    Sign = 1m
                })
            .ToListAsync(cancellationToken);

        saleFees.AddRange(receivableFees);
        saleFees.AddRange(reversals);
        foreach (var row in saleFees)
        {
            row.ProfitEffect = row.Sign * (isSingle ? row.FeeLocal : row.FeeUsd);
        }

        return saleFees;
    }

    private sealed class PaymentMethodRow
    {
        public int PaymentMethodId { get; init; }

        public string? PaymentMethodName { get; init; }

        public bool IsCash { get; init; }

        public decimal Amount { get; init; }

        public decimal AmountLocal { get; init; }

        public decimal FeeUsd { get; init; }

        public decimal FeeLocal { get; init; }

        public bool FeeConfigured { get; init; }
    }

    private sealed class BreakdownFeeEffectRow
    {
        public DateTime EventAtUtc { get; init; }

        public decimal FeeUsd { get; init; }

        public decimal FeeLocal { get; init; }

        public decimal Sign { get; init; }

        public decimal ProfitEffect { get; set; }
    }

    private sealed class BreakdownCollectionRow
    {
        public int SaleId { get; init; }

        public DateTime EventAtUtc { get; init; }

        public decimal AmountUsd { get; init; }

        public decimal AmountLocal { get; init; }
    }

    private sealed class BreakdownRefundRow
    {
        public int SaleId { get; init; }

        public DateTime EventAtUtc { get; init; }

        public decimal RefundedUsd { get; init; }

        public decimal RefundedBsS { get; init; }
    }

    private sealed class BreakdownFxRow
    {
        public DateTime EventAtUtc { get; init; }

        public decimal FxDifferenceBsS { get; init; }

        public decimal PaymentRate { get; init; }
    }
}
