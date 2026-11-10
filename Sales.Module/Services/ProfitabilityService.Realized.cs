using Core.DTOs;
using Core.Helpers;
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
/// Paso 14 (F2-WU3, F2-S3): motor percibido en caja. Cada cobro (pago de contado o cuota CxC)
/// se imputa a su factura y el CMV se prorratea con el factor NetCogs/NetTotal de la factura.
/// Solo las facturas con cobertura de costo 100% alimentan el margen realizado; el ingreso con
/// costo desconocido queda segregado en <c>UnknownCostCollectedRevenue</c>. La extension de WU3
/// agrega prorrateo por producto seleccionado sin prorratear las lineas globales de operacion.
/// </summary>
public partial class ProfitabilityService
{
    private async Task<ProfitabilityRealizedDto> BuildRealizedAsync(
        ProfitabilityFilterDto filter,
        ProfitabilityTimeWindowResult window,
        ReportCurrencyContext reportCurrency,
        CancellationToken cancellationToken)
    {
        var isSingle = reportCurrency.Mode == MonetaryMode.Single;
        var fromUtc = window.FromUtc;
        var toUtc = window.ToUtc;
        var productIds = filter.ProductIds ?? Array.Empty<int>();
        var hasProductFilter = productIds.Count > 0;
        var paymentMethodIds = filter.PaymentMethodIds ?? Array.Empty<int>();
        var hasPaymentMethodFilter = paymentMethodIds.Count > 0;

        // ProductIds is a WU3 extension: the realized engine prorates the collected amounts by
        // the selected products' share of the invoice. Cashier/SaleType/PriceListType are applied
        // through the sale join; ProductIds is deliberately not applied to this query because the
        // engine still needs the full invoice to build NetTotal/NetCogs (the selected basis).
        var filteredSales = ApplySaleFilters(_context.Sales.AsNoTracking(), filter);
        var merchandiseSales = filteredSales.Where(sale =>
            sale.SaleType == SaleType.Merchandise && sale.InvoiceNumber != null);

        var salePaymentSource = _context.SalePayments.AsNoTracking();
        if (hasPaymentMethodFilter)
        {
            salePaymentSource = salePaymentSource.Where(payment =>
                paymentMethodIds.Contains(payment.PaymentMethodId));
        }

        var salePaymentRows = await (
                from payment in salePaymentSource
                join sale in merchandiseSales on payment.SaleId equals sale.Id
                where payment.CreatedAt >= fromUtc && payment.CreatedAt < toUtc
                select new RealizedCollectionRow
                {
                    SaleId = sale.Id,
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

        // A CxC payment can be split across several receivables; each allocation is the portion
        // imputed to one invoice. AmountUSD is the 4dp effective amount in Dual and AmountBsS the
        // local snapshot in Single.
        var allocationRows = await (
                from allocation in _context.CreditPaymentAllocations.AsNoTracking()
                join payment in receivablePaymentSource
                    on allocation.ReceivablePaymentId equals payment.Id
                join receivable in _context.Receivables.AsNoTracking()
                    on allocation.ReceivableId equals receivable.Id
                join sale in merchandiseSales on receivable.SaleId equals sale.Id
                where payment.ReceivedAtUtc >= fromUtc && payment.ReceivedAtUtc < toUtc
                select new RealizedCollectionRow
                {
                    SaleId = sale.Id,
                    AmountUsd = allocation.AmountUSD,
                    AmountLocal = allocation.AmountBsS
                })
            .ToListAsync(cancellationToken);

        var refundRows = await (
                from creditReturn in _context.CreditReturns.AsNoTracking()
                join sale in merchandiseSales on creditReturn.SaleId equals sale.Id
                where creditReturn.CreatedAtUtc >= fromUtc && creditReturn.CreatedAtUtc < toUtc
                select new RealizedRefundRow
                {
                    SaleId = sale.Id,
                    RefundedUsd = creditReturn.RefundedUSD,
                    RefundedBsS = creditReturn.RefundedBsS
                })
            .ToListAsync(cancellationToken);

        var collectionRows = salePaymentRows.Concat(allocationRows).ToList();
        var saleIds = collectionRows.Select(row => row.SaleId)
            .Concat(refundRows.Select(row => row.SaleId))
            .Distinct()
            .ToList();

        var saleRows = saleIds.Count == 0
            ? new List<RealizedSaleRow>()
            : await _context.Sales.AsNoTracking()
                .Where(sale => saleIds.Contains(sale.Id))
                .Select(sale => new RealizedSaleRow
                {
                    Id = sale.Id,
                    TotalUSD = sale.TotalUSD,
                    TotalBsS = sale.TotalBsS,
                    AppliedRate = sale.AppliedRate
                })
                .ToListAsync(cancellationToken);

        var itemRows = saleIds.Count == 0
            ? new List<RealizedSaleItemRow>()
            : await _context.SaleItems.AsNoTracking()
                .Where(item => saleIds.Contains(item.SaleId))
                .Select(item => new RealizedSaleItemRow
                {
                    Id = item.Id,
                    SaleId = item.SaleId,
                    ProductId = item.ProductId,
                    Quantity = item.Quantity,
                    Subtotal = item.Subtotal,
                    SubtotalBsS = item.SubtotalBsS,
                    UnitCostUSD = item.UnitCostUSD,
                    UnitCostLocal = item.UnitCostLocal
                })
                .ToListAsync(cancellationToken);

        // Returns are cumulative for the invoice's net basis: NetTotal and NetCogs must reflect
        // every returned item of the sale, not only the returns whose event falls in the window.
        // The Refunded amount, by contrast, is a cash outflow only when CreditReturn.CreatedAtUtc
        // falls in the requested window (refundRows above).
        var returnItemRows = saleIds.Count == 0
            ? new List<RealizedReturnItemRow>()
            : await (
                    from returnItem in _context.CreditReturnItems.AsNoTracking()
                    join creditReturn in _context.CreditReturns.AsNoTracking()
                        on returnItem.CreditReturnId equals creditReturn.Id
                    where saleIds.Contains(creditReturn.SaleId)
                    select new RealizedReturnItemRow
                    {
                        SaleId = creditReturn.SaleId,
                        SaleItemId = returnItem.SaleItemId,
                        Quantity = returnItem.Quantity,
                        AmountUSD = returnItem.AmountUSD
                    })
                .ToListAsync(cancellationToken);

        var itemsBySale = itemRows
            .GroupBy(item => item.SaleId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<RealizedSaleItemRow>)group.ToList());
        var returnItemsBySale = returnItemRows
            .GroupBy(row => row.SaleId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<RealizedReturnItemRow>)group.ToList());
        var returnedQuantityBySaleItem = returnItemRows
            .GroupBy(row => row.SaleItemId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Quantity));

        var metricsBySale = new Dictionary<int, RealizedSaleMetrics>();
        foreach (var sale in saleRows)
        {
            var saleItems = itemsBySale.TryGetValue(sale.Id, out var loadedItems)
                ? loadedItems
                : Array.Empty<RealizedSaleItemRow>();
            var saleReturns = returnItemsBySale.TryGetValue(sale.Id, out var loadedReturns)
                ? loadedReturns
                : Array.Empty<RealizedReturnItemRow>();

            metricsBySale[sale.Id] = BuildRealizedSaleMetrics(
                sale,
                saleItems,
                saleReturns,
                returnedQuantityBySaleItem,
                productIds,
                hasProductFilter,
                isSingle);
        }

        var knownCollected = 0m;
        var unknownCollected = 0m;
        var realizedCogs = 0m;

        foreach (var row in collectionRows)
        {
            if (!metricsBySale.TryGetValue(row.SaleId, out var metrics))
            {
                continue;
            }

            var amount = isSingle ? row.AmountLocal : row.AmountUsd;
            if (metrics.HasFullCoverage)
            {
                knownCollected += amount * metrics.PaymentShare;
                realizedCogs += amount * metrics.CogsShare;
            }
            else
            {
                unknownCollected += amount * metrics.PaymentShare;
            }
        }

        foreach (var refund in refundRows)
        {
            if (!metricsBySale.TryGetValue(refund.SaleId, out var metrics))
            {
                continue;
            }

            var amount = isSingle ? refund.RefundedBsS : refund.RefundedUsd;
            var attributedRefund = amount * metrics.RefundShare;
            if (metrics.HasFullCoverage)
            {
                knownCollected -= attributedRefund;
            }
            else
            {
                unknownCollected -= attributedRefund;
            }
        }

        var saleFeeRows = await (
                from payment in salePaymentSource
                join sale in filteredSales on payment.SaleId equals sale.Id
                where payment.CreatedAt >= fromUtc && payment.CreatedAt < toUtc
                select new RealizedFeeRow
                {
                    FeeUsd = payment.FeeAmountUsd,
                    FeeLocal = payment.FeeAmountLocal
                })
            .ToListAsync(cancellationToken);

        var receivableFeeRows = await receivablePaymentSource
            .Where(payment => payment.ReceivedAtUtc >= fromUtc && payment.ReceivedAtUtc < toUtc)
            .Select(payment => new RealizedFeeRow
            {
                FeeUsd = payment.FeeAmountUsd,
                FeeLocal = payment.FeeAmountLocal
            })
            .ToListAsync(cancellationToken);

        // S10: an issued sale cancelled inside the window reverses exactly the fees frozen on its
        // original SalePayments. Merchandise returns do not reverse the acquirer fee because the
        // electronic collection itself is not reversed.
        var feeReversalRows = await (
                from payment in salePaymentSource
                join sale in filteredSales on payment.SaleId equals sale.Id
                where sale.InvoiceNumber != null
                    && sale.CancelledAtUtc >= fromUtc
                    && sale.CancelledAtUtc < toUtc
                select new RealizedFeeRow
                {
                    FeeUsd = payment.FeeAmountUsd,
                    FeeLocal = payment.FeeAmountLocal
                })
            .ToListAsync(cancellationToken);

        // FxDifference is summed once per ReceivablePayment (not per allocation), stays an
        // informational realized line and obeys the same PaymentMethodIds filter as fees and
        // allocations. A ReceivablePayment without a method (legacy) is excluded when filtering.
        var fxRows = await receivablePaymentSource
            .Where(payment => payment.ReceivedAtUtc >= fromUtc && payment.ReceivedAtUtc < toUtc)
            .Select(payment => new RealizedFxRow
            {
                FxDifferenceBsS = payment.FxDifferenceBsS,
                PaymentRate = payment.PaymentRate
            })
            .ToListAsync(cancellationToken);

        var serviceCommissions = await LoadServiceCommissionsAsync(
            filteredSales, fromUtc, toUtc, isSingle, cancellationToken);
        var pettyCashExpenses = await LoadPettyCashExpensesAsync(
            fromUtc, toUtc, isSingle, cancellationToken);
        var fxDifference = fxRows.Sum(row => RealizedFx(row, isSingle));
        var paymentMethodFees = saleFeeRows.Sum(row => RealizedFee(row, isSingle))
            + receivableFeeRows.Sum(row => RealizedFee(row, isSingle))
            - feeReversalRows.Sum(row => RealizedFee(row, isSingle));

        var collectedRevenue = knownCollected + unknownCollected;
        var realizedGrossProfit = knownCollected - realizedCogs;
        var realizedGrossMarginPercent = knownCollected > 0m
            ? realizedGrossProfit / knownCollected * 100m
            : 0m;
        var netProfit = realizedGrossProfit + serviceCommissions + fxDifference
            - pettyCashExpenses - paymentMethodFees;
        var netMarginPercent = knownCollected > 0m
            ? netProfit / knownCollected * 100m
            : 0m;

        return new ProfitabilityRealizedDto
        {
            CollectedRevenue = collectedRevenue,
            KnownCostCollectedRevenue = knownCollected,
            UnknownCostCollectedRevenue = unknownCollected,
            RealizedCogs = realizedCogs,
            RealizedGrossProfit = realizedGrossProfit,
            RealizedGrossMarginPercent = realizedGrossMarginPercent,
            ServiceCommissions = serviceCommissions,
            FxDifference = fxDifference,
            PettyCashExpenses = pettyCashExpenses,
            PaymentMethodFees = paymentMethodFees,
            NetProfit = netProfit,
            NetMarginPercent = netMarginPercent
        };
    }

    private static RealizedSaleMetrics BuildRealizedSaleMetrics(
        RealizedSaleRow sale,
        IReadOnlyList<RealizedSaleItemRow> items,
        IReadOnlyList<RealizedReturnItemRow> returnItems,
        IReadOnlyDictionary<int, decimal> returnedQuantityBySaleItem,
        IReadOnlyList<int> productIds,
        bool hasProductFilter,
        bool isSingle)
    {
        var saleAmount = isSingle
            ? sale.TotalBsS
            : PricingCalculator.EffectiveUsdFromBsS(sale.TotalBsS, sale.AppliedRate, sale.TotalUSD);

        // Sale-level coverage: a sale without item rows has no historical cost evidence and is
        // reported as unknown instead of fabricating a 100% margin (F2-S8).
        var hasFullCoverage = items.Count > 0
            && items.All(item => isSingle ? item.UnitCostLocal.HasValue : item.UnitCostUSD.HasValue);

        var netCogs = 0m;
        var selectedCogs = 0m;
        var selectedGrossRevenue = 0m;

        foreach (var item in items)
        {
            returnedQuantityBySaleItem.TryGetValue(item.Id, out var returnedQuantity);
            var netQuantity = item.Quantity - returnedQuantity;
            var unitCost = isSingle ? item.UnitCostLocal : item.UnitCostUSD;
            if (unitCost.HasValue)
            {
                var itemCogs = unitCost.Value * netQuantity;
                netCogs += itemCogs;
                if (!hasProductFilter || productIds.Contains(item.ProductId))
                {
                    selectedCogs += itemCogs;
                }
            }

            if (!hasProductFilter || productIds.Contains(item.ProductId))
            {
                selectedGrossRevenue += isSingle ? item.SubtotalBsS : item.Subtotal;
            }
        }

        var productIdBySaleItem = items.ToDictionary(item => item.Id, item => item.ProductId);
        var totalReturnedRevenue = 0m;
        var selectedReturnedRevenue = 0m;

        foreach (var returnItem in returnItems)
        {
            // Returned revenue is the invoice's frozen net-revenue basis: USD in Dual, and the
            // sale's AppliedRate in Single. CreditReturn.RefundRate is the latest payment rate
            // used for the cash refund and can differ from the sale snapshot, so it must not
            // revalue the returned invoice line.
            var returnedRevenue = isSingle
                ? returnItem.AmountUSD * sale.AppliedRate
                : returnItem.AmountUSD;
            totalReturnedRevenue += returnedRevenue;

            if (!hasProductFilter)
            {
                selectedReturnedRevenue += returnedRevenue;
            }
            else if (productIdBySaleItem.TryGetValue(returnItem.SaleItemId, out var productId)
                && productIds.Contains(productId))
            {
                selectedReturnedRevenue += returnedRevenue;
            }
        }

        var netTotal = saleAmount - totalReturnedRevenue;

        // The payment share uses the original selected revenue against the original invoice
        // amount (before returns), while the returned revenue is subtracted separately through
        // refundShare. Prorating the payment by net selected revenue would double-count the
        // return and make a fully returned selected product go negative. Without a product filter
        // the whole collection belongs to the sale (share 1) so payments and refunds cancel in
        // the same bucket.
        var paymentShare = !hasProductFilter
            ? 1m
            : saleAmount > 0m
                ? selectedGrossRevenue / saleAmount
                : 0m;

        var cogsBasis = hasProductFilter ? selectedCogs : netCogs;
        // F2-S3: a fully returned invoice has NetTotal <= 0 and no realized CMV.
        var cogsShare = hasFullCoverage && netTotal > 0m
            ? cogsBasis / netTotal
            : 0m;

        // With a product filter the refund is allocated by the returned item revenue share of the
        // selected products. A pure overpayment refund (no returned item revenue) has no product
        // basis and allocates 0; without a filter the full refund is a cash outflow.
        var refundShare = !hasProductFilter
            ? 1m
            : totalReturnedRevenue > 0m
                ? selectedReturnedRevenue / totalReturnedRevenue
                : 0m;

        return new RealizedSaleMetrics(hasFullCoverage, paymentShare, cogsShare, refundShare);
    }

    private static decimal RealizedFee(RealizedFeeRow row, bool isSingle)
    {
        return isSingle ? row.FeeLocal : row.FeeUsd;
    }

    private static decimal RealizedFx(RealizedFxRow row, bool isSingle)
    {
        if (isSingle)
        {
            return row.FxDifferenceBsS;
        }

        if (row.PaymentRate <= 0m)
        {
            return 0m;
        }

        return Math.Round(row.FxDifferenceBsS / row.PaymentRate, 4, MidpointRounding.AwayFromZero);
    }

    private sealed record RealizedSaleMetrics(
        bool HasFullCoverage,
        decimal PaymentShare,
        decimal CogsShare,
        decimal RefundShare);

    private sealed class RealizedCollectionRow
    {
        public int SaleId { get; init; }

        public decimal AmountUsd { get; init; }

        public decimal AmountLocal { get; init; }
    }

    private sealed class RealizedRefundRow
    {
        public int SaleId { get; init; }

        public decimal RefundedUsd { get; init; }

        public decimal RefundedBsS { get; init; }
    }

    private sealed class RealizedSaleRow
    {
        public int Id { get; init; }

        public decimal TotalUSD { get; init; }

        public decimal TotalBsS { get; init; }

        public decimal AppliedRate { get; init; }
    }

    private sealed class RealizedSaleItemRow
    {
        public int Id { get; init; }

        public int SaleId { get; init; }

        public int ProductId { get; init; }

        public decimal Quantity { get; init; }

        public decimal Subtotal { get; init; }

        public decimal SubtotalBsS { get; init; }

        public decimal? UnitCostUSD { get; init; }

        public decimal? UnitCostLocal { get; init; }
    }

    private sealed class RealizedReturnItemRow
    {
        public int SaleId { get; init; }

        public int SaleItemId { get; init; }

        public decimal Quantity { get; init; }

        public decimal AmountUSD { get; init; }
    }

    private sealed class RealizedFeeRow
    {
        public decimal FeeUsd { get; init; }

        public decimal FeeLocal { get; init; }
    }

    private sealed class RealizedFxRow
    {
        public decimal FxDifferenceBsS { get; init; }

        public decimal PaymentRate { get; init; }
    }
}
