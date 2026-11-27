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
/// Paso 14 (F2-WU4, F2-S4): ventas netas y utilidad bruta por cajero. La atribución es por la
/// venta que originó el ingreso (incluidas sus devoluciones y anulaciones del período); una venta
/// sin cajero se agrupa como "Sin cajero".
/// </summary>
public partial class ProfitabilityService
{
    private async Task<IReadOnlyList<SellerBreakdownDto>> BuildSellerBreakdownAsync(
        ProfitabilityFilterDto filter,
        ProfitabilityTimeWindowResult window,
        bool isSingle,
        CancellationToken cancellationToken)
    {
        var fromUtc = window.FromUtc;
        var toUtc = window.ToUtc;
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
        var saleIds = grossSales.Select(sale => sale.Id)
            .Concat(cancelledSales.Select(sale => sale.Id))
            .Distinct()
            .ToList();
        var saleItems = await LoadBreakdownItemRowsAsync(saleIds, filter.ProductIds, cancellationToken);
        var itemsBySale = saleItems
            .GroupBy(item => item.SaleId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<BreakdownSaleItemRow>)group.ToList());

        var returnRows = await LoadBreakdownReturnRowsAsync(merchandiseSales, fromUtc, toUtc, cancellationToken);
        var returnItems = await LoadBreakdownReturnItemRowsAsync(
            merchandiseSales, fromUtc, toUtc, filter.ProductIds, cancellationToken);
        var returnItemsByReturn = returnItems
            .GroupBy(item => item.CreditReturnId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<BreakdownReturnItemRow>)group.ToList());

        var aggregates = new Dictionary<int, SellerAggregate>();
        foreach (var sale in grossSales)
        {
            var (netSales, grossProfit) = SaleContribution(
                sale, itemsBySale.GetValueOrDefault(sale.Id), HasProductFilter(filter), isSingle);
            var aggregate = GetSellerAggregate(aggregates, sale.CashierId);
            aggregate.NetSales += netSales;
            aggregate.GrossProfit += grossProfit;
        }

        foreach (var sale in cancelledSales)
        {
            var (netSales, grossProfit) = SaleContribution(
                sale, itemsBySale.GetValueOrDefault(sale.Id), HasProductFilter(filter), isSingle);
            var aggregate = GetSellerAggregate(aggregates, sale.CashierId);
            aggregate.NetSales -= netSales;
            aggregate.GrossProfit -= grossProfit;
        }

        foreach (var creditReturn in returnRows)
        {
            var items = returnItemsByReturn.GetValueOrDefault(creditReturn.CreditReturnId);
            var netSales = HasProductFilter(filter)
                ? items?.Sum(item => ReturnedItemRevenue(item, isSingle)) ?? 0m
                : ReturnedSaleAmount(creditReturn.ReturnedUSD, creditReturn.AppliedRate, isSingle);
            var grossProfit = items?.Sum(item => ReturnedItemGrossProfit(item, isSingle)) ?? 0m;
            var aggregate = GetSellerAggregate(aggregates, creditReturn.CashierId);
            aggregate.NetSales -= netSales;
            aggregate.GrossProfit -= grossProfit;
        }

        var cashierIds = aggregates.Keys.Where(cashierId => cashierId != 0).Distinct().ToList();
        var cashierRows = cashierIds.Count == 0
            ? new List<CashierNameRow>()
            : await _context.Users.AsNoTracking()
                .Where(user => cashierIds.Contains(user.Id))
                .Select(user => new CashierNameRow
                {
                    Id = user.Id,
                    Name = user.Name,
                    FullName = user.FullName
                })
                .ToListAsync(cancellationToken);
        var cashierNames = cashierRows.ToDictionary(
            row => row.Id,
            row => string.IsNullOrWhiteSpace(row.Name) ? row.FullName : row.Name);

        return aggregates
            .Where(pair => pair.Value.NetSales != 0m || pair.Value.GrossProfit != 0m)
            .Select(pair =>
            {
                var name = pair.Key != 0
                    && cashierNames.TryGetValue(pair.Key, out var loadedName)
                    && !string.IsNullOrWhiteSpace(loadedName)
                        ? loadedName
                        : pair.Key != 0
                            ? $"Cajero {pair.Key}"
                            : "Sin cajero";
                return new SellerBreakdownDto
                {
                    CashierId = pair.Key == 0 ? null : pair.Key,
                    CashierName = name,
                    NetSales = pair.Value.NetSales,
                    GrossProfit = pair.Value.GrossProfit
                };
            })
            .OrderByDescending(seller => seller.NetSales)
            .ThenBy(seller => seller.CashierName, StringComparer.Ordinal)
            .ThenBy(seller => seller.CashierId ?? 0)
            .ToList();
    }

    private static SellerAggregate GetSellerAggregate(
        IDictionary<int, SellerAggregate> aggregates,
        int? cashierId)
    {
        // Sale.CashierId is nullable; 0 is the synthetic bucket for sales without a cashier
        // because Dictionary<TKey, ...> cannot use a nullable key.
        var key = cashierId ?? 0;
        if (!aggregates.TryGetValue(key, out var aggregate))
        {
            aggregate = new SellerAggregate();
            aggregates[key] = aggregate;
        }

        return aggregate;
    }

    private sealed class SellerAggregate
    {
        public decimal NetSales { get; set; }

        public decimal GrossProfit { get; set; }
    }

    private sealed class CashierNameRow
    {
        public int Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string? FullName { get; init; }
    }
}
