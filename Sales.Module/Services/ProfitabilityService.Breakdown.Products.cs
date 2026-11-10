using Core.DTOs;
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
/// Paso 14 (F2-WU4, F2-S4): ranking devengado de productos y categorías. El ingreso con costo
/// desconocido queda en <c>Revenue</c> pero fuera de <c>GrossProfit</c>/<c>GrossMarginPercent</c>,
/// igual que el motor devengado; las líneas globales no se prorratean por artículo.
/// </summary>
public partial class ProfitabilityService
{
    private async Task<(IReadOnlyList<ProductBreakdownDto> Products, IReadOnlyList<CategoryBreakdownDto> Categories)>
        BuildProductAndCategoryBreakdownAsync(
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

        var grossItems = await LoadBreakdownItemRowsAsync(
            await LoadSaleIdsAsync(grossSalesQuery, cancellationToken), filter.ProductIds, cancellationToken);
        var cancelledItems = await LoadBreakdownItemRowsAsync(
            await LoadSaleIdsAsync(cancelledSalesQuery, cancellationToken), filter.ProductIds, cancellationToken);
        var returnItems = await LoadBreakdownReturnItemRowsAsync(
            merchandiseSales, fromUtc, toUtc, filter.ProductIds, cancellationToken);

        var aggregates = new Dictionary<int, ProductAggregate>();
        foreach (var item in grossItems)
        {
            AddProductSaleItem(aggregates, item, sign: 1m, isSingle);
        }

        foreach (var item in returnItems)
        {
            AddProductReturnItem(aggregates, item, isSingle);
        }

        foreach (var item in cancelledItems)
        {
            AddProductSaleItem(aggregates, item, sign: -1m, isSingle);
        }

        if (aggregates.Count == 0)
        {
            return (Array.Empty<ProductBreakdownDto>(), Array.Empty<CategoryBreakdownDto>());
        }

        var productIds = aggregates.Keys.ToList();
        IReadOnlyDictionary<int, ProfitabilityProductInfoDto> productInfo =
            _inventoryService is null
                ? new Dictionary<int, ProfitabilityProductInfoDto>()
                : await _inventoryService.GetProductCategoryInfoAsync(productIds, cancellationToken);

        var products = new List<ProductBreakdownDto>(aggregates.Count);
        foreach (var (productId, aggregate) in aggregates)
        {
            productInfo.TryGetValue(productId, out var info);
            var categoryId = info?.CategoryId;
            var categoryName = categoryId.HasValue ? info?.CategoryName : null;
            products.Add(new ProductBreakdownDto
            {
                ProductId = productId,
                ProductName = info?.ProductName ?? aggregate.ProductName ?? $"Producto {productId}",
                CategoryId = categoryId,
                CategoryName = categoryId.HasValue
                    ? (string.IsNullOrWhiteSpace(categoryName) ? "Sin categoría" : categoryName)
                    : "Sin categoría",
                QuantitySold = aggregate.Quantity,
                Revenue = aggregate.Revenue,
                Cogs = aggregate.Cogs,
                GrossProfit = aggregate.KnownRevenue - aggregate.Cogs,
                GrossMarginPercent = aggregate.KnownRevenue > 0m
                    ? (aggregate.KnownRevenue - aggregate.Cogs) / aggregate.KnownRevenue * 100m
                    : 0m
            });
        }

        // Deterministic ranking: monetary contribution first, then name and id as tie-breakers.
        products = products
            .OrderByDescending(product => product.GrossProfit)
            .ThenBy(product => product.ProductName, StringComparer.Ordinal)
            .ThenBy(product => product.ProductId)
            .ToList();

        var totalGrossProfit = aggregates.Values.Sum(aggregate => aggregate.KnownRevenue - aggregate.Cogs);
        var categories = aggregates
            .GroupBy(pair =>
            {
                productInfo.TryGetValue(pair.Key, out var info);
                return info?.CategoryId ?? 0;
            })
            .Select(group =>
            {
                var categoryId = group.Key;
                var knownRevenue = group.Sum(pair => pair.Value.KnownRevenue);
                var cogs = group.Sum(pair => pair.Value.Cogs);
                var grossProfit = knownRevenue - cogs;
                var categoryName = categoryId == 0
                    ? "Sin categoría"
                    : group
                        .Select(pair =>
                        {
                            productInfo.TryGetValue(pair.Key, out var info);
                            return info?.CategoryName;
                        })
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                        ?? "Sin categoría";

                return new CategoryBreakdownDto
                {
                    CategoryId = categoryId,
                    CategoryName = categoryName,
                    Revenue = group.Sum(pair => pair.Value.Revenue),
                    Cogs = cogs,
                    GrossProfit = grossProfit,
                    GrossMarginPercent = knownRevenue > 0m ? grossProfit / knownRevenue * 100m : 0m,
                    ContributionPercent = totalGrossProfit != 0m
                        ? grossProfit / totalGrossProfit * 100m
                        : 0m
                };
            })
            .OrderByDescending(category => category.GrossProfit)
            .ThenBy(category => category.CategoryName, StringComparer.Ordinal)
            .ThenBy(category => category.CategoryId)
            .ToList();

        return (products, categories);
    }

    private async Task<Dictionary<int, RealizedSaleMetrics>> LoadRealizedSaleMetricsAsync(
        IReadOnlyList<int> saleIds,
        IReadOnlyList<int>? productIds,
        bool hasProductFilter,
        bool isSingle,
        CancellationToken cancellationToken)
    {
        if (saleIds.Count == 0)
        {
            return new Dictionary<int, RealizedSaleMetrics>();
        }

        var saleRows = await _context.Sales.AsNoTracking()
            .Where(sale => saleIds.Contains(sale.Id))
            .Select(sale => new RealizedSaleRow
            {
                Id = sale.Id,
                TotalUSD = sale.TotalUSD,
                TotalBsS = sale.TotalBsS,
                AppliedRate = sale.AppliedRate
            })
            .ToListAsync(cancellationToken);

        var itemRows = await _context.SaleItems.AsNoTracking()
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

        var returnItemRows = await (
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

        var metrics = new Dictionary<int, RealizedSaleMetrics>(saleRows.Count);
        var selectedProductIds = productIds ?? Array.Empty<int>();
        foreach (var sale in saleRows)
        {
            metrics[sale.Id] = BuildRealizedSaleMetrics(
                sale,
                itemsBySale.GetValueOrDefault(sale.Id) ?? Array.Empty<RealizedSaleItemRow>(),
                returnItemsBySale.GetValueOrDefault(sale.Id) ?? Array.Empty<RealizedReturnItemRow>(),
                returnedQuantityBySaleItem,
                selectedProductIds,
                hasProductFilter,
                isSingle);
        }

        return metrics;
    }

    private static void AddProductSaleItem(
        IDictionary<int, ProductAggregate> aggregates,
        BreakdownSaleItemRow item,
        decimal sign,
        bool isSingle)
    {
        var revenue = ItemRevenue(item.Subtotal, item.SubtotalBsS, isSingle);
        var aggregate = GetProductAggregate(aggregates, item.ProductId, item.ProductName);
        aggregate.Quantity += sign * item.Quantity;
        aggregate.Revenue += sign * revenue;
        if (HasKnownCost(item.UnitCostUSD, item.UnitCostLocal, isSingle))
        {
            aggregate.KnownRevenue += sign * revenue;
            aggregate.Cogs += sign * UnitCost(item.UnitCostUSD, item.UnitCostLocal, isSingle) * item.Quantity;
        }
    }

    private static void AddProductReturnItem(
        IDictionary<int, ProductAggregate> aggregates,
        BreakdownReturnItemRow item,
        bool isSingle)
    {
        var revenue = ReturnedItemRevenue(item, isSingle);
        var aggregate = GetProductAggregate(aggregates, item.ProductId, item.ProductName);
        aggregate.Quantity -= item.Quantity;
        aggregate.Revenue -= revenue;
        if (HasKnownCost(item.UnitCostUSD, item.UnitCostLocal, isSingle))
        {
            aggregate.KnownRevenue -= revenue;
            aggregate.Cogs -= UnitCost(item.UnitCostUSD, item.UnitCostLocal, isSingle) * item.Quantity;
        }
    }

    private static ProductAggregate GetProductAggregate(
        IDictionary<int, ProductAggregate> aggregates,
        int productId,
        string? productName)
    {
        if (!aggregates.TryGetValue(productId, out var aggregate))
        {
            aggregate = new ProductAggregate();
            aggregates[productId] = aggregate;
        }

        if (string.IsNullOrWhiteSpace(aggregate.ProductName) && !string.IsNullOrWhiteSpace(productName))
        {
            aggregate.ProductName = productName;
        }

        return aggregate;
    }

    private sealed class ProductAggregate
    {
        public string? ProductName { get; set; }

        public decimal Quantity { get; set; }

        public decimal Revenue { get; set; }

        public decimal KnownRevenue { get; set; }

        public decimal Cogs { get; set; }
    }
}
