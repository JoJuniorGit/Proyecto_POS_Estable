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
/// Paso 14 (F2-WU4, F2-S4): orquestación de los desgloses y cargas compartidas del bloque
/// devengado. Los parciales especializados viven en
/// <c>ProfitabilityService.Breakdown.*.cs</c>; los montos respetan la moneda de reporte (USD en
/// Dual, snapshots locales en Single).
/// </summary>
public partial class ProfitabilityService
{
    private async Task<ProfitabilityBreakdownDto> BuildBreakdownAsync(
        ProfitabilityFilterDto filter,
        ProfitabilityTimeWindowResult window,
        ReportCurrencyContext reportCurrency,
        CancellationToken cancellationToken)
    {
        var isSingle = reportCurrency.Mode == MonetaryMode.Single;
        var timeZone = await _timeZoneProvider.GetTimeZoneInfoAsync(cancellationToken);

        var series = await BuildSeriesAsync(filter, window, timeZone, isSingle, cancellationToken);
        var paymentMethods = await BuildPaymentMethodBreakdownAsync(filter, window, isSingle, cancellationToken);
        var (products, categories) = await BuildProductAndCategoryBreakdownAsync(
            filter, window, isSingle, cancellationToken);
        var sellers = await BuildSellerBreakdownAsync(filter, window, isSingle, cancellationToken);

        return new ProfitabilityBreakdownDto
        {
            Series = series,
            PaymentMethods = paymentMethods,
            Categories = categories,
            Products = products,
            Sellers = sellers
        };
    }

    private static async Task<List<BreakdownSaleRow>> LoadBreakdownSaleRowsAsync(
        IQueryable<Sale> query,
        CancellationToken cancellationToken)
    {
        return await query
            .Select(sale => new BreakdownSaleRow
            {
                Id = sale.Id,
                Date = sale.Date,
                CancelledAtUtc = sale.CancelledAtUtc,
                CashierId = sale.CashierId,
                TotalUSD = sale.TotalUSD,
                TotalBsS = sale.TotalBsS,
                AppliedRate = sale.AppliedRate
            })
            .ToListAsync(cancellationToken);
    }

    private async Task<List<int>> LoadSaleIdsAsync(
        IQueryable<Sale> query,
        CancellationToken cancellationToken)
    {
        return await query.Select(sale => sale.Id).ToListAsync(cancellationToken);
    }

    private async Task<List<BreakdownSaleItemRow>> LoadBreakdownItemRowsAsync(
        IReadOnlyCollection<int> saleIds,
        IReadOnlyList<int>? productIds,
        CancellationToken cancellationToken)
    {
        if (saleIds.Count == 0)
        {
            return new List<BreakdownSaleItemRow>();
        }

        var productFilter = productIds ?? Array.Empty<int>();
        var hasProductFilter = productFilter.Count > 0;
        var query =
            from saleItem in _context.SaleItems.AsNoTracking()
            where saleIds.Contains(saleItem.SaleId)
            select new BreakdownSaleItemRow
            {
                SaleId = saleItem.SaleId,
                ProductId = saleItem.ProductId,
                ProductName = saleItem.ProductName,
                Quantity = saleItem.Quantity,
                Subtotal = saleItem.Subtotal,
                SubtotalBsS = saleItem.SubtotalBsS,
                UnitCostUSD = saleItem.UnitCostUSD,
                UnitCostLocal = saleItem.UnitCostLocal
            };

        if (hasProductFilter)
        {
            query = query.Where(row => productFilter.Contains(row.ProductId));
        }

        return await query.ToListAsync(cancellationToken);
    }

    private async Task<List<BreakdownReturnRow>> LoadBreakdownReturnRowsAsync(
        IQueryable<Sale> merchandiseSalesQuery,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken)
    {
        return await (
                from creditReturn in _context.CreditReturns.AsNoTracking()
                join sale in merchandiseSalesQuery on creditReturn.SaleId equals sale.Id
                where creditReturn.CreatedAtUtc >= fromUtc && creditReturn.CreatedAtUtc < toUtc
                select new BreakdownReturnRow
                {
                    CreditReturnId = creditReturn.Id,
                    SaleId = sale.Id,
                    CreatedAtUtc = creditReturn.CreatedAtUtc,
                    ReturnedUSD = creditReturn.ReturnedUSD,
                    AppliedRate = sale.AppliedRate,
                    CashierId = sale.CashierId
                })
            .ToListAsync(cancellationToken);
    }

    private async Task<List<BreakdownReturnItemRow>> LoadBreakdownReturnItemRowsAsync(
        IQueryable<Sale> merchandiseSalesQuery,
        DateTime fromUtc,
        DateTime toUtc,
        IReadOnlyList<int>? productIds,
        CancellationToken cancellationToken)
    {
        var productFilter = productIds ?? Array.Empty<int>();
        var hasProductFilter = productFilter.Count > 0;
        var query =
            from returnItem in _context.CreditReturnItems.AsNoTracking()
            join creditReturn in _context.CreditReturns.AsNoTracking()
                on returnItem.CreditReturnId equals creditReturn.Id
            join sale in merchandiseSalesQuery on creditReturn.SaleId equals sale.Id
            join saleItem in _context.SaleItems.AsNoTracking()
                on returnItem.SaleItemId equals saleItem.Id
            where creditReturn.CreatedAtUtc >= fromUtc && creditReturn.CreatedAtUtc < toUtc
            select new BreakdownReturnItemRow
            {
                CreditReturnId = creditReturn.Id,
                SaleId = sale.Id,
                ProductId = saleItem.ProductId,
                ProductName = saleItem.ProductName,
                Quantity = returnItem.Quantity,
                AmountUSD = returnItem.AmountUSD,
                ItemQuantity = saleItem.Quantity,
                ItemSubtotal = saleItem.Subtotal,
                ItemSubtotalBsS = saleItem.SubtotalBsS,
                UnitCostUSD = saleItem.UnitCostUSD,
                UnitCostLocal = saleItem.UnitCostLocal
            };

        if (hasProductFilter)
        {
            query = query.Where(row => productFilter.Contains(row.ProductId));
        }

        return await query.ToListAsync(cancellationToken);
    }

    private static (decimal NetSales, decimal GrossProfit) SaleContribution(
        BreakdownSaleRow sale,
        IReadOnlyList<BreakdownSaleItemRow>? items,
        bool hasProductFilter,
        bool isSingle)
    {
        var knownRevenue = 0m;
        var knownCogs = 0m;
        var selectedRevenue = 0m;
        if (items is not null)
        {
            foreach (var item in items)
            {
                var revenue = ItemRevenue(item.Subtotal, item.SubtotalBsS, isSingle);
                selectedRevenue += revenue;
                if (HasKnownCost(item.UnitCostUSD, item.UnitCostLocal, isSingle))
                {
                    knownRevenue += revenue;
                    knownCogs += UnitCost(item.UnitCostUSD, item.UnitCostLocal, isSingle) * item.Quantity;
                }
            }
        }

        var netSales = hasProductFilter
            ? selectedRevenue
            : SaleAmount(sale.TotalBsS, sale.AppliedRate, sale.TotalUSD, isSingle);
        return (netSales, knownRevenue - knownCogs);
    }

    private static bool HasProductFilter(ProfitabilityFilterDto filter)
        => filter.ProductIds is { Count: > 0 };

    private static decimal ItemRevenue(decimal subtotalUsd, decimal subtotalBsS, bool isSingle)
        => isSingle ? subtotalBsS : subtotalUsd;

    private static decimal SaleAmount(decimal totalBsS, decimal appliedRate, decimal totalUsd, bool isSingle)
        => isSingle ? totalBsS : PricingCalculator.EffectiveUsdFromBsS(totalBsS, appliedRate, totalUsd);

    /// <summary>
    /// F2-WU4: get the returned invoice revenue in the report currency. Single uses the immutable
    /// sale snapshot (<c>Sale.AppliedRate</c>), not <c>CreditReturn.RefundRate</c>, because the
    /// latter is the rate of the cash refund and can differ from the invoice snapshot (same rule
    /// as the realized engine, WU3).
    /// </summary>
    private static decimal ReturnedSaleAmount(decimal returnedUsd, decimal appliedRate, bool isSingle)
        => isSingle ? returnedUsd * appliedRate : returnedUsd;

    private static decimal ReturnedItemRevenue(BreakdownReturnItemRow item, bool isSingle)
    {
        if (item.ItemQuantity <= 0m)
        {
            return 0m;
        }

        var ratio = item.Quantity / item.ItemQuantity;
        return isSingle ? item.ItemSubtotalBsS * ratio : item.ItemSubtotal * ratio;
    }

    private static decimal ReturnedItemGrossProfit(BreakdownReturnItemRow item, bool isSingle)
    {
        var revenue = ReturnedItemRevenue(item, isSingle);
        if (!HasKnownCost(item.UnitCostUSD, item.UnitCostLocal, isSingle))
        {
            return 0m;
        }

        return revenue - UnitCost(item.UnitCostUSD, item.UnitCostLocal, isSingle) * item.Quantity;
    }

    private sealed class BreakdownSaleRow
    {
        public int Id { get; init; }

        public DateTime Date { get; init; }

        public DateTime? CancelledAtUtc { get; init; }

        public int? CashierId { get; init; }

        public decimal TotalUSD { get; init; }

        public decimal TotalBsS { get; init; }

        public decimal AppliedRate { get; init; }
    }

    private sealed class BreakdownSaleItemRow
    {
        public int SaleId { get; init; }

        public int ProductId { get; init; }

        public string? ProductName { get; init; }

        public decimal Quantity { get; init; }

        public decimal Subtotal { get; init; }

        public decimal SubtotalBsS { get; init; }

        public decimal? UnitCostUSD { get; init; }

        public decimal? UnitCostLocal { get; init; }
    }

    private sealed class BreakdownReturnRow
    {
        public int CreditReturnId { get; init; }

        public int SaleId { get; init; }

        public DateTime CreatedAtUtc { get; init; }

        public decimal ReturnedUSD { get; init; }

        public decimal AppliedRate { get; init; }

        public int? CashierId { get; init; }
    }

    private sealed class BreakdownReturnItemRow
    {
        public int CreditReturnId { get; init; }

        public int SaleId { get; init; }

        public int ProductId { get; init; }

        public string? ProductName { get; init; }

        public decimal Quantity { get; init; }

        public decimal AmountUSD { get; init; }

        public decimal ItemQuantity { get; init; }

        public decimal ItemSubtotal { get; init; }

        public decimal ItemSubtotalBsS { get; init; }

        public decimal? UnitCostUSD { get; init; }

        public decimal? UnitCostLocal { get; init; }
    }
}
