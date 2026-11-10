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
/// Paso 14 (F2-WU2, F2-S2): motor devengado. Todos los cortes son comparaciones directas
/// <c>&gt;= fromUtc</c> / <c>&lt; toUtc</c> sobre columnas indexadas; no se usan funciones SQL de
/// fecha. En Dual el reporte se expresa en USD con la inversa efectiva de la venta; en Single se
/// usan los snapshots locales (TotalBsS/SubtotalBsS, UnitCostLocal, CommissionAmountLocal,
/// FeeAmountLocal, AmountBsS).
/// </summary>
public partial class ProfitabilityService
{
    private async Task<AccruedResult> BuildAccruedAsync(
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

        var filteredSales = ApplySaleFilters(_context.Sales.AsNoTracking(), filter);

        var grossSalesQuery = filteredSales.Where(sale =>
            sale.SaleType == SaleType.Merchandise
            // F2-S2: InvoiceNumber is the immutable discriminator between an issued sale and a
            // Pending/OnHold order. Status cannot be used because a completed sale that is later
            // cancelled keeps its invoice but changes Status, which would drop it from gross
            // sales while the cancellation line still subtracts it.
            && sale.InvoiceNumber != null
            && sale.Date >= fromUtc
            && sale.Date < toUtc
            && (sale.CancelledAtUtc == null || sale.CancelledAtUtc >= fromUtc));

        // A sale issued before the period and cancelled inside it is not part of gross sales, but
        // its reversal belongs to the event period (same accrual rule as returns). A sale whose
        // cancellation is itself caused by a credit return is represented by the Returns line, so
        // it is excluded here to avoid subtracting the same event twice.
        var cancelledSalesQuery = filteredSales.Where(sale =>
            sale.SaleType == SaleType.Merchandise
            && sale.InvoiceNumber != null
            && sale.CancelledAtUtc >= fromUtc
            && sale.CancelledAtUtc < toUtc
            && !_context.CreditReturns.Any(creditReturn =>
                creditReturn.SaleId == sale.Id
                && creditReturn.CreatedAtUtc >= fromUtc
                && creditReturn.CreatedAtUtc < toUtc));

        var merchandiseSalesQuery = filteredSales.Where(sale => sale.SaleType == SaleType.Merchandise);

        var grossSaleRows = hasProductFilter
            ? new List<SaleAmountRow>()
            : await LoadSaleAmountsAsync(grossSalesQuery, cancellationToken);
        var grossItems = await LoadSaleItemsAsync(
            grossSalesQuery, productIds, hasProductFilter, cancellationToken);

        var returnHeaders = hasProductFilter
            ? new List<ReturnAmountRow>()
            : await LoadReturnAmountsAsync(merchandiseSalesQuery, fromUtc, toUtc, cancellationToken);
        var returnItems = await LoadReturnItemsAsync(
            merchandiseSalesQuery, fromUtc, toUtc, productIds, hasProductFilter, cancellationToken);

        var cancelledSaleRows = hasProductFilter
            ? new List<SaleAmountRow>()
            : await LoadSaleAmountsAsync(cancelledSalesQuery, cancellationToken);
        var cancelledItems = await LoadSaleItemsAsync(
            cancelledSalesQuery, productIds, hasProductFilter, cancellationToken);

        var revenue = AggregateItemRevenue(grossItems, returnItems, cancelledItems, isSingle);

        var grossSales = hasProductFilter
            ? grossItems.Sum(item => ItemRevenue(item, isSingle))
            : grossSaleRows.Sum(row => SaleAmount(row, isSingle));
        var cancellations = hasProductFilter
            ? cancelledItems.Sum(item => ItemRevenue(item, isSingle))
            : cancelledSaleRows.Sum(row => SaleAmount(row, isSingle));
        var returns = hasProductFilter
            ? returnItems.Sum(row => ReturnAmount(row, isSingle))
            : returnHeaders.Sum(row => ReturnAmount(row, isSingle));

        var serviceCommissions = await LoadServiceCommissionsAsync(
            filteredSales, fromUtc, toUtc, isSingle, cancellationToken);
        var pettyCashExpenses = await LoadPettyCashExpensesAsync(
            fromUtc, toUtc, isSingle, cancellationToken);
        // F2-WU4: the method filter must reach the fee headline so a method-filtered report
        // reconciles with the realized/breakdown semantics. Without a filter the behavior is
        // unchanged.
        var paymentMethodIds = filter.PaymentMethodIds ?? Array.Empty<int>();
        var hasPaymentMethodFilter = paymentMethodIds.Count > 0;
        var feeRows = await LoadFeeRowsAsync(
            fromUtc, toUtc, paymentMethodIds, hasPaymentMethodFilter, cancellationToken);
        var reversalRows = await LoadFeeReversalRowsAsync(
            fromUtc, toUtc, paymentMethodIds, hasPaymentMethodFilter, cancellationToken);

        var paymentMethodFees = feeRows.Sum(row => FeeAmount(row, isSingle))
            - reversalRows.Sum(row => FeeAmount(row, isSingle));
        var fxDifference = feeRows.Sum(row => FxDifference(row, isSingle));

        var netSales = grossSales - cancellations - returns;
        var grossProfit = revenue.KnownRevenue - revenue.KnownCogs;
        var netProfit = grossProfit + serviceCommissions - pettyCashExpenses - paymentMethodFees;
        var grossMarginPercent = revenue.KnownRevenue > 0m
            ? grossProfit / revenue.KnownRevenue * 100m
            : 0m;
        var netMarginPercent = revenue.KnownRevenue > 0m
            ? netProfit / revenue.KnownRevenue * 100m
            : 0m;

        var totalRevenue = revenue.KnownRevenue + revenue.UnknownRevenue;
        var costCoveragePercent = totalRevenue > 0m
            ? revenue.KnownRevenue / totalRevenue * 100m
            : 0m;

        var knownFeePaymentAmount = 0m;
        var unknownFeePaymentAmount = 0m;
        var unknownFeePaymentsCount = 0;
        foreach (var row in feeRows)
        {
            // Cash payments have no acquirer fee by definition (FeeConfigured stays false without
            // meaning "unknown"); they must not dilute fee coverage.
            if (row.IsCash)
            {
                continue;
            }

            if (row.FeeConfigured)
            {
                knownFeePaymentAmount += PaymentAmount(row, isSingle);
            }
            else
            {
                unknownFeePaymentAmount += PaymentAmount(row, isSingle);
                unknownFeePaymentsCount++;
            }
        }

        var feePaymentAmount = knownFeePaymentAmount + unknownFeePaymentAmount;
        var feeCoveragePercent = feePaymentAmount > 0m
            ? knownFeePaymentAmount / feePaymentAmount * 100m
            : 0m;

        return new AccruedResult(
            new ProfitabilityAccruedDto
            {
                GrossSales = grossSales,
                Cancellations = cancellations,
                Returns = returns,
                NetSales = netSales,
                KnownCostRevenue = revenue.KnownRevenue,
                UnknownCostRevenue = revenue.UnknownRevenue,
                Cogs = revenue.KnownCogs,
                GrossProfit = grossProfit,
                GrossMarginPercent = grossMarginPercent,
                ServiceCommissions = serviceCommissions,
                FxDifferenceInformational = fxDifference,
                PettyCashExpenses = pettyCashExpenses,
                PaymentMethodFees = paymentMethodFees,
                NetProfit = netProfit,
                NetMarginPercent = netMarginPercent
            },
            new ProfitabilityCoverageDto
            {
                CostCoveragePercent = costCoveragePercent,
                KnownCostRevenue = revenue.KnownRevenue,
                KnownCogs = revenue.KnownCogs,
                UnknownCostRevenue = revenue.UnknownRevenue,
                UnknownCostLinesCount = revenue.UnknownLinesCount,
                FeeCoveragePercent = feeCoveragePercent,
                KnownFeePaymentAmount = knownFeePaymentAmount,
                UnknownFeePaymentAmount = unknownFeePaymentAmount,
                UnknownFeePaymentsCount = unknownFeePaymentsCount
            });
    }

    private static IQueryable<Sale> ApplySaleFilters(IQueryable<Sale> query, ProfitabilityFilterDto filter)
    {
        if (filter.CashierId.HasValue)
        {
            query = query.Where(sale => sale.CashierId == filter.CashierId.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.PriceListType))
        {
            query = query.Where(sale => sale.PriceListType == filter.PriceListType);
        }

        if (filter.SaleType.HasValue)
        {
            query = query.Where(sale => sale.SaleType == filter.SaleType.Value);
        }

        return query;
    }

    private static async Task<List<SaleAmountRow>> LoadSaleAmountsAsync(
        IQueryable<Sale> salesQuery,
        CancellationToken cancellationToken)
    {
        return await salesQuery
            .Select(sale => new SaleAmountRow
            {
                TotalUSD = sale.TotalUSD,
                TotalBsS = sale.TotalBsS,
                AppliedRate = sale.AppliedRate
            })
            .ToListAsync(cancellationToken);
    }

    private async Task<List<SaleItemCostRow>> LoadSaleItemsAsync(
        IQueryable<Sale> salesQuery,
        IReadOnlyList<int> productIds,
        bool hasProductFilter,
        CancellationToken cancellationToken)
    {
        var query =
            from sale in salesQuery
            join item in _context.SaleItems.AsNoTracking() on sale.Id equals item.SaleId
            select new SaleItemCostRow
            {
                Id = item.Id,
                ProductId = item.ProductId,
                Quantity = item.Quantity,
                Subtotal = item.Subtotal,
                SubtotalBsS = item.SubtotalBsS,
                UnitCostUSD = item.UnitCostUSD,
                UnitCostLocal = item.UnitCostLocal
            };

        if (hasProductFilter)
        {
            query = query.Where(row => productIds.Contains(row.ProductId));
        }

        return await query.ToListAsync(cancellationToken);
    }

    private async Task<List<ReturnAmountRow>> LoadReturnAmountsAsync(
        IQueryable<Sale> merchandiseSalesQuery,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken)
    {
        var query =
            from creditReturn in _context.CreditReturns.AsNoTracking()
            join sale in merchandiseSalesQuery on creditReturn.SaleId equals sale.Id
            where creditReturn.CreatedAtUtc >= fromUtc && creditReturn.CreatedAtUtc < toUtc
            select new ReturnAmountRow
            {
                ReturnedUSD = creditReturn.ReturnedUSD,
                AppliedRate = sale.AppliedRate
            };

        return await query.ToListAsync(cancellationToken);
    }

    private async Task<List<ReturnItemRow>> LoadReturnItemsAsync(
        IQueryable<Sale> merchandiseSalesQuery,
        DateTime fromUtc,
        DateTime toUtc,
        IReadOnlyList<int> productIds,
        bool hasProductFilter,
        CancellationToken cancellationToken)
    {
        var query =
            from returnItem in _context.CreditReturnItems.AsNoTracking()
            join creditReturn in _context.CreditReturns.AsNoTracking()
                on returnItem.CreditReturnId equals creditReturn.Id
            join sale in merchandiseSalesQuery on creditReturn.SaleId equals sale.Id
            join saleItem in _context.SaleItems.AsNoTracking()
                on returnItem.SaleItemId equals saleItem.Id
            where creditReturn.CreatedAtUtc >= fromUtc && creditReturn.CreatedAtUtc < toUtc
            select new ReturnItemRow
            {
                SaleItemId = returnItem.SaleItemId,
                ProductId = saleItem.ProductId,
                Quantity = returnItem.Quantity,
                AmountUSD = returnItem.AmountUSD,
                AppliedRate = sale.AppliedRate,
                ItemQuantity = saleItem.Quantity,
                ItemSubtotal = saleItem.Subtotal,
                ItemSubtotalBsS = saleItem.SubtotalBsS,
                UnitCostUSD = saleItem.UnitCostUSD,
                UnitCostLocal = saleItem.UnitCostLocal
            };

        if (hasProductFilter)
        {
            query = query.Where(row => productIds.Contains(row.ProductId));
        }

        return await query.ToListAsync(cancellationToken);
    }

    private async Task<decimal> LoadServiceCommissionsAsync(
        IQueryable<Sale> filteredSales,
        DateTime fromUtc,
        DateTime toUtc,
        bool isSingle,
        CancellationToken cancellationToken)
    {
        var earnedQuery = filteredSales.Where(sale =>
            sale.SaleType == SaleType.CashAdvance
            // Same invoice/completion pattern as merchandise: a cash advance completed and
            // cancelled inside the window stays in the earned set and nets against its reversal.
            && sale.InvoiceNumber != null
            && sale.Date >= fromUtc
            && sale.Date < toUtc
            && (sale.CancelledAtUtc == null || sale.CancelledAtUtc >= fromUtc));
        var earned = await LoadCommissionAmountsAsync(earnedQuery, cancellationToken);

        // S10-style event-period reversal: the commission recognized in a previous period is
        // subtracted when the cash advance is cancelled inside the current window.
        var cancelledQuery = filteredSales.Where(sale =>
            sale.SaleType == SaleType.CashAdvance
            && sale.InvoiceNumber != null
            && sale.CancelledAtUtc >= fromUtc
            && sale.CancelledAtUtc < toUtc);
        var cancelled = await LoadCommissionAmountsAsync(cancelledQuery, cancellationToken);

        return SumCommissions(earned, isSingle) - SumCommissions(cancelled, isSingle);
    }

    private static async Task<List<CommissionAmountRow>> LoadCommissionAmountsAsync(
        IQueryable<Sale> salesQuery,
        CancellationToken cancellationToken)
    {
        return await salesQuery
            .Select(sale => new CommissionAmountRow
            {
                CommissionUSD = sale.CommissionAmountUSD,
                CommissionLocal = sale.CommissionAmountLocal
            })
            .ToListAsync(cancellationToken);
    }

    private static decimal SumCommissions(List<CommissionAmountRow> rows, bool isSingle)
    {
        return rows.Sum(row => isSingle ? row.CommissionLocal : row.CommissionUSD);
    }

    private async Task<decimal> LoadPettyCashExpensesAsync(
        DateTime fromUtc,
        DateTime toUtc,
        bool isSingle,
        CancellationToken cancellationToken)
    {
        var activeRows = await _context.PettyCashExpenses
            .AsNoTracking()
            .Where(expense =>
                expense.Status == PettyCashStatus.Active
                && expense.CreatedAtUtc >= fromUtc
                && expense.CreatedAtUtc < toUtc)
            .Select(expense => new PettyCashAmountRow
            {
                AmountUsd = expense.AmountUsd,
                AmountBsS = expense.AmountBsS
            })
            .ToListAsync(cancellationToken);

        // A cancellation reverses an expense recognized in an earlier period; an expense created
        // and cancelled inside the same window was never counted (Status is already Cancelled).
        var reversedRows = await _context.PettyCashExpenses
            .AsNoTracking()
            .Where(expense =>
                expense.Status == PettyCashStatus.Cancelled
                && expense.CancelledAtUtc >= fromUtc
                && expense.CancelledAtUtc < toUtc
                && expense.CreatedAtUtc < fromUtc)
            .Select(expense => new PettyCashAmountRow
            {
                AmountUsd = expense.AmountUsd,
                AmountBsS = expense.AmountBsS
            })
            .ToListAsync(cancellationToken);

        return activeRows.Sum(row => isSingle ? row.AmountBsS : row.AmountUsd)
            - reversedRows.Sum(row => isSingle ? row.AmountBsS : row.AmountUsd);
    }

    private async Task<List<FeeRow>> LoadFeeRowsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        IReadOnlyList<int> paymentMethodIds,
        bool hasPaymentMethodFilter,
        CancellationToken cancellationToken)
    {
        var salePaymentSource = _context.SalePayments.AsNoTracking();
        if (hasPaymentMethodFilter)
        {
            salePaymentSource = salePaymentSource.Where(payment =>
                paymentMethodIds.Contains(payment.PaymentMethodId));
        }

        var salePayments = await (
                from payment in salePaymentSource
                join method in _context.PaymentMethods.AsNoTracking()
                    on payment.PaymentMethodId equals method.Id into methods
                from method in methods.DefaultIfEmpty()
                where payment.CreatedAt >= fromUtc && payment.CreatedAt < toUtc
                select new FeeRow
                {
                    PaymentMethodId = payment.PaymentMethodId,
                    IsCash = method != null && method.IsCash,
                    Amount = payment.Amount,
                    AmountLocal = payment.AmountBsS,
                    FeeUsd = payment.FeeAmountUsd,
                    FeeLocal = payment.FeeAmountLocal,
                    FeeConfigured = payment.FeeConfigured,
                    FxDifferenceBsS = 0m,
                    PaymentRate = 0m
                })
            .ToListAsync(cancellationToken);

        var receivablePaymentSource = _context.ReceivablePayments.AsNoTracking();
        if (hasPaymentMethodFilter)
        {
            receivablePaymentSource = receivablePaymentSource.Where(payment =>
                payment.PaymentMethodId.HasValue
                && paymentMethodIds.Contains(payment.PaymentMethodId.Value));
        }

        var receivablePayments = await (
                from payment in receivablePaymentSource
                join method in _context.PaymentMethods.AsNoTracking()
                    on payment.PaymentMethodId equals (int?)method.Id into methods
                from method in methods.DefaultIfEmpty()
                where payment.ReceivedAtUtc >= fromUtc && payment.ReceivedAtUtc < toUtc
                select new FeeRow
                {
                    PaymentMethodId = payment.PaymentMethodId,
                    IsCash = method != null && method.IsCash,
                    Amount = payment.AmountUSD,
                    AmountLocal = payment.AmountBsS,
                    FeeUsd = payment.FeeAmountUsd,
                    FeeLocal = payment.FeeAmountLocal,
                    FeeConfigured = payment.FeeConfigured,
                    FxDifferenceBsS = payment.FxDifferenceBsS,
                    PaymentRate = payment.PaymentRate
                })
            .ToListAsync(cancellationToken);

        return salePayments.Concat(receivablePayments).ToList();
    }

    private async Task<List<FeeRow>> LoadFeeReversalRowsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        IReadOnlyList<int> paymentMethodIds,
        bool hasPaymentMethodFilter,
        CancellationToken cancellationToken)
    {
        // F2-S2/S10: exactly the SalePayments of issued sales whose CancelledAtUtc falls in the
        // window. Merchandise returns do not reverse the acquirer fee because the electronic
        // collection itself is not reversed, and cancelled OnHold orders without an invoice never
        // had a completed collection to reverse. The payment-method filter applies to the original
        // payment method of the reversal.
        var paymentSource = _context.SalePayments.AsNoTracking();
        if (hasPaymentMethodFilter)
        {
            paymentSource = paymentSource.Where(payment =>
                paymentMethodIds.Contains(payment.PaymentMethodId));
        }

        return await (
                from payment in paymentSource
                join sale in _context.Sales.AsNoTracking() on payment.SaleId equals sale.Id
                where sale.InvoiceNumber != null
                    && sale.CancelledAtUtc >= fromUtc
                    && sale.CancelledAtUtc < toUtc
                select new FeeRow
                {
                    PaymentMethodId = payment.PaymentMethodId,
                    Amount = 0m,
                    AmountLocal = 0m,
                    FeeUsd = payment.FeeAmountUsd,
                    FeeLocal = payment.FeeAmountLocal,
                    FeeConfigured = payment.FeeConfigured,
                    FxDifferenceBsS = 0m,
                    PaymentRate = 0m
                })
            .ToListAsync(cancellationToken);
    }

    private static ItemRevenueAggregate AggregateItemRevenue(
        IReadOnlyList<SaleItemCostRow> grossItems,
        IReadOnlyList<ReturnItemRow> returnItems,
        IReadOnlyList<SaleItemCostRow> cancelledItems,
        bool isSingle)
    {
        var knownRevenue = 0m;
        var unknownRevenue = 0m;
        var knownCogs = 0m;
        // F2-S2 coverage: count the unknown-cost lines present in the gross qualifying sales.
        // Returns and cancellations reduce UnknownCostRevenue proportionally, but they do not
        // erase the historical line from the coverage denominator.
        var unknownLinesCount = 0;

        foreach (var item in grossItems)
        {
            var revenue = ItemRevenue(item, isSingle);
            if (HasKnownCost(item.UnitCostUSD, item.UnitCostLocal, isSingle))
            {
                knownRevenue += revenue;
                knownCogs += UnitCost(item.UnitCostUSD, item.UnitCostLocal, isSingle) * item.Quantity;
            }
            else
            {
                unknownRevenue += revenue;
                unknownLinesCount++;
            }
        }

        foreach (var row in returnItems)
        {
            var revenue = ReturnItemRevenue(row, isSingle);
            if (HasKnownCost(row.UnitCostUSD, row.UnitCostLocal, isSingle))
            {
                knownRevenue -= revenue;
                knownCogs -= UnitCost(row.UnitCostUSD, row.UnitCostLocal, isSingle) * row.Quantity;
            }
            else
            {
                unknownRevenue -= revenue;
            }
        }

        foreach (var item in cancelledItems)
        {
            var revenue = ItemRevenue(item, isSingle);
            if (HasKnownCost(item.UnitCostUSD, item.UnitCostLocal, isSingle))
            {
                knownRevenue -= revenue;
                knownCogs -= UnitCost(item.UnitCostUSD, item.UnitCostLocal, isSingle) * item.Quantity;
            }
            else
            {
                unknownRevenue -= revenue;
            }
        }

        return new ItemRevenueAggregate(knownRevenue, unknownRevenue, knownCogs, unknownLinesCount);
    }

    private static decimal SaleAmount(SaleAmountRow row, bool isSingle)
    {
        return isSingle
            ? row.TotalBsS
            : PricingCalculator.EffectiveUsdFromBsS(row.TotalBsS, row.AppliedRate, row.TotalUSD);
    }

    private static decimal ItemRevenue(SaleItemCostRow item, bool isSingle)
    {
        return isSingle ? item.SubtotalBsS : item.Subtotal;
    }

    private static decimal ReturnAmount(ReturnAmountRow row, bool isSingle)
    {
        // F2-WU4: Single returned invoice revenue uses the immutable sale snapshot
        // (Sale.AppliedRate), not CreditReturn.RefundRate (the cash-refund rate), matching the
        // realized and breakdown engines. RefundedBsS stays the cash refund.
        return isSingle ? row.ReturnedUSD * row.AppliedRate : row.ReturnedUSD;
    }

    private static decimal ReturnAmount(ReturnItemRow row, bool isSingle)
    {
        return isSingle ? row.AmountUSD * row.AppliedRate : row.AmountUSD;
    }

    private static decimal ReturnItemRevenue(ReturnItemRow row, bool isSingle)
    {
        if (row.ItemQuantity <= 0m)
        {
            return 0m;
        }

        var ratio = row.Quantity / row.ItemQuantity;
        return isSingle ? row.ItemSubtotalBsS * ratio : row.ItemSubtotal * ratio;
    }

    private static bool HasKnownCost(decimal? unitCostUsd, decimal? unitCostLocal, bool isSingle)
    {
        return isSingle ? unitCostLocal.HasValue : unitCostUsd.HasValue;
    }

    private static decimal UnitCost(decimal? unitCostUsd, decimal? unitCostLocal, bool isSingle)
    {
        return (isSingle ? unitCostLocal : unitCostUsd) ?? 0m;
    }

    private static decimal FeeAmount(FeeRow row, bool isSingle)
    {
        return isSingle ? row.FeeLocal : row.FeeUsd;
    }

    private static decimal PaymentAmount(FeeRow row, bool isSingle)
    {
        return isSingle ? row.AmountLocal : row.Amount;
    }

    private static decimal FxDifference(FeeRow row, bool isSingle)
    {
        if (isSingle || row.PaymentRate <= 0m)
        {
            return isSingle ? row.FxDifferenceBsS : 0m;
        }

        return Math.Round(row.FxDifferenceBsS / row.PaymentRate, 4, MidpointRounding.AwayFromZero);
    }

    private sealed record AccruedResult(
        ProfitabilityAccruedDto Accrued,
        ProfitabilityCoverageDto Coverage);

    private sealed record ItemRevenueAggregate(
        decimal KnownRevenue,
        decimal UnknownRevenue,
        decimal KnownCogs,
        int UnknownLinesCount);

    private sealed class SaleAmountRow
    {
        public decimal TotalUSD { get; init; }

        public decimal TotalBsS { get; init; }

        public decimal AppliedRate { get; init; }
    }

    private sealed class SaleItemCostRow
    {
        public int Id { get; init; }

        public int ProductId { get; init; }

        public decimal Quantity { get; init; }

        public decimal Subtotal { get; init; }

        public decimal SubtotalBsS { get; init; }

        public decimal? UnitCostUSD { get; init; }

        public decimal? UnitCostLocal { get; init; }
    }

    private sealed class ReturnAmountRow
    {
        public decimal ReturnedUSD { get; init; }

        public decimal AppliedRate { get; init; }
    }

    private sealed class ReturnItemRow
    {
        public int SaleItemId { get; init; }

        public int ProductId { get; init; }

        public decimal Quantity { get; init; }

        public decimal AmountUSD { get; init; }

        public decimal AppliedRate { get; init; }

        public decimal ItemQuantity { get; init; }

        public decimal ItemSubtotal { get; init; }

        public decimal ItemSubtotalBsS { get; init; }

        public decimal? UnitCostUSD { get; init; }

        public decimal? UnitCostLocal { get; init; }
    }

    private sealed class CommissionAmountRow
    {
        public decimal CommissionUSD { get; init; }

        public decimal CommissionLocal { get; init; }
    }

    private sealed class PettyCashAmountRow
    {
        public decimal AmountUsd { get; init; }

        public decimal AmountBsS { get; init; }
    }

    private sealed class FeeRow
    {
        public int? PaymentMethodId { get; init; }

        public bool IsCash { get; init; }

        public decimal Amount { get; init; }

        public decimal AmountLocal { get; init; }

        public decimal FeeUsd { get; init; }

        public decimal FeeLocal { get; init; }

        public bool FeeConfigured { get; init; }

        public decimal FxDifferenceBsS { get; init; }

        public decimal PaymentRate { get; init; }
    }
}
