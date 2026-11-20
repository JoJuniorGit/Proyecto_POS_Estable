using Core.DTOs;

namespace Sales.Module.Services;

/// <summary>
/// Paso 15: universo calculado antes de paginar. El reporte y el endpoint de IDs comparten esta
/// misma reduccion para que el puente al Paso 9 no dependa de la pagina visible.
/// </summary>
public sealed record InventoryRotationUniverse(
    InventoryRotationSummaryDto Summary,
    IReadOnlyList<InventoryRotationProductDto> Products,
    IReadOnlyList<InventoryRotationCategoryDto> Categories);

/// <summary>
/// Paso 15 (S1-S4): calculadora pura, sin EF ni acceso a datos. Recibe el snapshot de catalogo y
/// las agregaciones de ventas ya proyectadas por el servicio.
/// </summary>
public static class InventoryRotationCalculator
{
    public const int DefaultSalesWindowDays = 90;
    public const int DefaultDeadStockDays = 60;
    public const int MinDays = 1;
    public const int MaxDays = 365;
    public const int MaxPageSize = 200;
    public const int MaxDeadStockSelection = 1000;

    private static readonly string[] ValidSorts =
    {
        "immobilizedCapital",
        "daysSinceLastSale",
        "averageInventoryDays",
        "unitsSold",
        "name",
        "stock"
    };

    public static InventoryRotationUniverse BuildUniverse(
        IReadOnlyList<InventoryRotationProductSnapshotDto> products,
        IReadOnlyList<InventoryRotationSalesAggregateDto> sales,
        IReadOnlyList<InventoryRotationLastSaleDto> lastSales,
        InventoryRotationFilterDto filter,
        DateTime nowUtc)
    {
        ValidateFilter(filter);
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(sales);
        ArgumentNullException.ThrowIfNull(lastSales);

        var salesByProduct = sales
            .GroupBy(row => row.ProductId)
            .ToDictionary(
                group => group.Key,
                group => new SalesRow(
                    group.Sum(row => row.UnitsSold),
                    group.Sum(row => row.SalesRevenueUsd)));

        var lastSaleByProduct = lastSales
            .GroupBy(row => row.ProductId)
            .ToDictionary(group => group.Key, group => group.Max(row => row.LastSaleDateUtc));

        var rows = products
            .Select(product => BuildProductRow(
                product,
                salesByProduct,
                lastSaleByProduct,
                filter,
                nowUtc))
            .ToList();

        var summary = BuildSummary(rows);
        var categories = BuildCategories(rows, filter);
        var sorted = SortRows(rows, filter);

        return new InventoryRotationUniverse(summary, sorted, categories);
    }

    public static InventoryRotationReportDto Build(
        IReadOnlyList<InventoryRotationProductSnapshotDto> products,
        IReadOnlyList<InventoryRotationSalesAggregateDto> sales,
        IReadOnlyList<InventoryRotationLastSaleDto> lastSales,
        InventoryRotationFilterDto filter,
        DateTime nowUtc)
    {
        var universe = BuildUniverse(products, sales, lastSales, filter, nowUtc);
        return BuildReport(universe, filter, nowUtc);
    }

    public static InventoryRotationReportDto BuildReport(
        InventoryRotationUniverse universe,
        InventoryRotationFilterDto filter,
        DateTime nowUtc)
    {
        ValidateFilter(filter);
        ArgumentNullException.ThrowIfNull(universe);

        IEnumerable<InventoryRotationProductDto> filtered = universe.Products;
        if (!string.IsNullOrWhiteSpace(filter.RotationClass))
        {
            var normalizedClass = NormalizeClass(filter.RotationClass);
            filtered = filtered.Where(product => product.RotationClass == normalizedClass);
        }

        var filteredRows = filtered.ToList();
        var totalCount = filteredRows.Count;
        var totalPages = totalCount == 0
            ? 0
            : (int)Math.Ceiling(totalCount / (decimal)filter.PageSize);

        var pageRows = filteredRows
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToList();

        return new InventoryRotationReportDto
        {
            GeneratedAtUtc = nowUtc,
            FromUtc = nowUtc.AddDays(-filter.SalesWindowDays),
            ToUtc = nowUtc,
            SalesWindowDays = filter.SalesWindowDays,
            DeadStockDays = filter.DeadStockDays,
            Summary = universe.Summary,
            Products = pageRows,
            Categories = universe.Categories,
            Page = filter.Page,
            PageSize = filter.PageSize,
            TotalCount = totalCount,
            TotalPages = totalPages
        };
    }

    public static DeadStockSelectionDto BuildDeadStockSelection(InventoryRotationUniverse universe)
    {
        ArgumentNullException.ThrowIfNull(universe);

        var deadStock = universe.Products
            .Where(product => product.IsDeadStock)
            .OrderByDescending(product => product.InventoryValueUsd)
            .ThenBy(product => product.ProductId)
            .ToList();

        return new DeadStockSelectionDto
        {
            ProductIds = deadStock.Take(MaxDeadStockSelection).Select(product => product.ProductId).ToList(),
            TotalCount = deadStock.Count,
            Truncated = deadStock.Count > MaxDeadStockSelection
        };
    }

    private static InventoryRotationProductDto BuildProductRow(
        InventoryRotationProductSnapshotDto product,
        IReadOnlyDictionary<int, SalesRow> salesByProduct,
        IReadOnlyDictionary<int, DateTime> lastSaleByProduct,
        InventoryRotationFilterDto filter,
        DateTime nowUtc)
    {
        salesByProduct.TryGetValue(product.ProductId, out var sale);
        decimal unitsSold = sale is null ? 0m : (decimal)sale.UnitsSold;
        decimal revenue = sale is null ? 0m : (decimal)sale.SalesRevenueUsd;

        var velocity = unitsSold / filter.SalesWindowDays;
        decimal? averageInventoryDays = velocity > 0m
            ? Math.Round(product.StockQuantity / velocity, 2, MidpointRounding.AwayFromZero)
            : null;

        lastSaleByProduct.TryGetValue(product.ProductId, out var lastSale);
        DateTime? lastSaleUtc = lastSale == default
            ? null
            : DateTime.SpecifyKind(lastSale, DateTimeKind.Utc);

        var referenceDate = lastSaleUtc ?? product.CreatedAtUtc;
        var daysSinceLastSale = Math.Max(0, (nowUtc.Date - referenceDate.Date).Days);
        var rotationClass = ResolveClass(
            product.StockQuantity,
            averageInventoryDays,
            daysSinceLastSale,
            filter.DeadStockDays);

        var inventoryValue = Math.Round(
            product.StockQuantity * product.CostPriceUsd,
            2,
            MidpointRounding.AwayFromZero);

        return new InventoryRotationProductDto
        {
            ProductId = product.ProductId,
            SKU = product.SKU,
            Name = product.Name,
            CategoryId = product.CategoryId,
            CategoryName = string.IsNullOrWhiteSpace(product.CategoryName)
                ? "Sin categoria"
                : product.CategoryName,
            IsActive = product.IsActive,
            StockQuantity = product.StockQuantity,
            CostPriceUsd = product.CostPriceUsd,
            InventoryValueUsd = inventoryValue,
            UnitsSold = unitsSold,
            SalesRevenueUsd = Math.Round(revenue, 2, MidpointRounding.AwayFromZero),
            LastSaleDateUtc = lastSaleUtc,
            DaysSinceLastSale = daysSinceLastSale,
            SalesVelocityPerDay = Math.Round(velocity, 4, MidpointRounding.AwayFromZero),
            AverageInventoryDays = averageInventoryDays,
            RotationClass = rotationClass
        };
    }

    private static InventoryRotationSummaryDto BuildSummary(IReadOnlyList<InventoryRotationProductDto> rows)
    {
        var totalProducts = rows.Count;
        var high = rows.Count(row => row.RotationClass == InventoryRotationClass.High);
        var medium = rows.Count(row => row.RotationClass == InventoryRotationClass.Medium);
        var low = rows.Count(row => row.RotationClass == InventoryRotationClass.Low);
        var dead = rows.Count(row => row.RotationClass == InventoryRotationClass.DeadStock);
        var totalInventoryValue = rows.Sum(row => row.InventoryValueUsd);
        var immobilizedCapital = rows
            .Where(row => row.IsDeadStock)
            .Sum(row => row.InventoryValueUsd);

        return new InventoryRotationSummaryDto
        {
            TotalProducts = totalProducts,
            HighRotationCount = high,
            MediumRotationCount = medium,
            LowRotationCount = low,
            DeadStockCount = dead,
            HighRotationPercent = Percent(high, totalProducts),
            MediumRotationPercent = Percent(medium, totalProducts),
            LowRotationPercent = Percent(low, totalProducts),
            DeadStockPercent = Percent(dead, totalProducts),
            TotalInventoryValueUsd = Math.Round(totalInventoryValue, 2, MidpointRounding.AwayFromZero),
            ImmobilizedCapitalUsd = Math.Round(immobilizedCapital, 2, MidpointRounding.AwayFromZero),
            ImmobilizedCapitalPercent = Percent(immobilizedCapital, totalInventoryValue)
        };
    }

    private static List<InventoryRotationCategoryDto> BuildCategories(
        IReadOnlyList<InventoryRotationProductDto> rows,
        InventoryRotationFilterDto filter)
    {
        return rows
            .GroupBy(row => new { row.CategoryId, row.CategoryName })
            .Select(group =>
            {
                var stock = group.Sum(row => row.StockQuantity);
                var unitsSold = group.Sum(row => row.UnitsSold);
                var velocity = unitsSold / filter.SalesWindowDays;
                decimal? averageDays = velocity > 0m
                    ? Math.Round(stock / velocity, 2, MidpointRounding.AwayFromZero)
                    : null;

                return new InventoryRotationCategoryDto
                {
                    CategoryId = group.Key.CategoryId,
                    CategoryName = group.Key.CategoryName,
                    ProductCount = group.Count(),
                    StockQuantity = stock,
                    InventoryValueUsd = Math.Round(
                        group.Sum(row => row.InventoryValueUsd),
                        2,
                        MidpointRounding.AwayFromZero),
                    UnitsSold = unitsSold,
                    SalesRevenueUsd = Math.Round(
                        group.Sum(row => row.SalesRevenueUsd),
                        2,
                        MidpointRounding.AwayFromZero),
                    AverageInventoryDays = averageDays,
                    DeadStockCount = group.Count(row => row.IsDeadStock),
                    ImmobilizedCapitalUsd = Math.Round(
                        group.Where(row => row.IsDeadStock).Sum(row => row.InventoryValueUsd),
                        2,
                        MidpointRounding.AwayFromZero),
                    RotationClass = ResolveAggregateClass(averageDays)
                };
            })
            .OrderByDescending(category => category.ImmobilizedCapitalUsd)
            .ThenByDescending(category => category.InventoryValueUsd)
            .ThenBy(category => category.CategoryName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<InventoryRotationProductDto> SortRows(
        List<InventoryRotationProductDto> rows,
        InventoryRotationFilterDto filter)
    {
        var sortBy = string.IsNullOrWhiteSpace(filter.SortBy)
            ? "immobilizedCapital"
            : filter.SortBy.Trim();

        IOrderedEnumerable<InventoryRotationProductDto> ordered = sortBy.ToLowerInvariant() switch
        {
            "dayssincelastsale" => filter.Descending
                ? rows.OrderByDescending(row => row.DaysSinceLastSale)
                : rows.OrderBy(row => row.DaysSinceLastSale),
            "averageinventorydays" => filter.Descending
                ? rows.OrderBy(row => row.AverageInventoryDays.HasValue ? 0 : 1)
                    .ThenByDescending(row => row.AverageInventoryDays)
                : rows.OrderBy(row => row.AverageInventoryDays.HasValue ? 0 : 1)
                    .ThenBy(row => row.AverageInventoryDays),
            "unitssold" => filter.Descending
                ? rows.OrderByDescending(row => row.UnitsSold)
                : rows.OrderBy(row => row.UnitsSold),
            "name" => filter.Descending
                ? rows.OrderByDescending(row => row.Name, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
            "stock" => filter.Descending
                ? rows.OrderByDescending(row => row.StockQuantity)
                : rows.OrderBy(row => row.StockQuantity),
            _ => filter.Descending
                ? rows.OrderByDescending(row => row.InventoryValueUsd)
                : rows.OrderBy(row => row.InventoryValueUsd)
        };

        return ordered
            .ThenBy(row => row.ProductId)
            .ToList();
    }

    public static void ValidateFilter(InventoryRotationFilterDto filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (filter.SalesWindowDays is < MinDays or > MaxDays)
        {
            throw new ArgumentException(
                $"La ventana de ventas debe estar entre {MinDays} y {MaxDays} dias.",
                nameof(filter));
        }

        if (filter.DeadStockDays is < MinDays or > MaxDays)
        {
            throw new ArgumentException(
                $"El umbral de stock muerto debe estar entre {MinDays} y {MaxDays} dias.",
                nameof(filter));
        }

        if (filter.Page < 1)
        {
            throw new ArgumentException("La pagina debe ser mayor o igual a 1.", nameof(filter));
        }

        if (filter.PageSize is < 1 or > MaxPageSize)
        {
            throw new ArgumentException(
                $"El tamano de pagina debe estar entre 1 y {MaxPageSize}.",
                nameof(filter));
        }

        if (filter.CategoryId is <= 0)
        {
            throw new ArgumentException("La categoria debe ser un id positivo.", nameof(filter));
        }

        if (!string.IsNullOrWhiteSpace(filter.SortBy) &&
            !ValidSorts.Contains(filter.SortBy.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Criterio de orden invalido: {filter.SortBy}. Valores validos: {string.Join(", ", ValidSorts)}.",
                nameof(filter));
        }

        if (!string.IsNullOrWhiteSpace(filter.RotationClass))
        {
            _ = NormalizeClass(filter.RotationClass);
        }
    }

    private static string NormalizeClass(string value)
    {
        var normalized = value.Trim();
        if (string.Equals(normalized, InventoryRotationClass.High, StringComparison.OrdinalIgnoreCase))
        {
            return InventoryRotationClass.High;
        }

        if (string.Equals(normalized, InventoryRotationClass.Medium, StringComparison.OrdinalIgnoreCase))
        {
            return InventoryRotationClass.Medium;
        }

        if (string.Equals(normalized, InventoryRotationClass.Low, StringComparison.OrdinalIgnoreCase))
        {
            return InventoryRotationClass.Low;
        }

        if (string.Equals(normalized, InventoryRotationClass.DeadStock, StringComparison.OrdinalIgnoreCase))
        {
            return InventoryRotationClass.DeadStock;
        }

        throw new ArgumentException(
            $"Clase de rotacion invalida: {value}. Valores validos: {InventoryRotationClass.High}, " +
            $"{InventoryRotationClass.Medium}, {InventoryRotationClass.Low}, {InventoryRotationClass.DeadStock}.");
    }

    private static string ResolveClass(
        decimal stock,
        decimal? averageInventoryDays,
        int daysSinceLastSale,
        int deadStockDays)
    {
        if (stock > 0m && daysSinceLastSale >= deadStockDays)
        {
            return InventoryRotationClass.DeadStock;
        }

        if (averageInventoryDays is <= 30m)
        {
            return InventoryRotationClass.High;
        }

        if (averageInventoryDays is <= 60m)
        {
            return InventoryRotationClass.Medium;
        }

        return InventoryRotationClass.Low;
    }

    private static string ResolveAggregateClass(decimal? averageInventoryDays)
    {
        if (averageInventoryDays is <= 30m)
        {
            return InventoryRotationClass.High;
        }

        if (averageInventoryDays is <= 60m)
        {
            return InventoryRotationClass.Medium;
        }

        return InventoryRotationClass.Low;
    }

    private static decimal Percent(decimal numerator, decimal denominator) =>
        denominator == 0m
            ? 0m
            : Math.Round(numerator / denominator * 100m, 2, MidpointRounding.AwayFromZero);

    private sealed record SalesRow(decimal UnitsSold, decimal SalesRevenueUsd);
}
