using Core.DTOs;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 15 (S1-S4): la calculadora de rotacion debe ser determinista, pura y consistente entre el
/// detalle por producto, el resumen ejecutivo y el desglose por categoria.
/// </summary>
public class InventoryRotationCalculatorTests
{
    private static readonly DateTime NowUtc = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildUniverse_ClassifiesHealthAndImmobilizedCapital()
    {
        var products = new List<InventoryRotationProductSnapshotDto>
        {
            Product(1, "Leche", "SKU-1", categoryId: 1, categoryName: "Lacteos", stock: 10m, cost: 2m, createdDaysAgo: 300),
            Product(2, "Queso", "SKU-2", categoryId: 1, categoryName: "Lacteos", stock: 40m, cost: 5m, createdDaysAgo: 300),
            Product(3, "Harina", "SKU-3", categoryId: null, categoryName: null, stock: 5m, cost: 1m, createdDaysAgo: 100),
            Product(4, "Arroz", "SKU-4", categoryId: 2, categoryName: "Abarrotes", stock: 20m, cost: 3m, createdDaysAgo: 300),
            Product(5, "Atun", "SKU-5", categoryId: 2, categoryName: "Abarrotes", stock: 100m, cost: 1m, createdDaysAgo: 20),
            Product(6, "Cafe", "SKU-6", categoryId: 2, categoryName: "Abarrotes", stock: 0m, cost: 10m, createdDaysAgo: 300),
            Product(7, "Sal", "SKU-7", categoryId: 1, categoryName: "Lacteos", stock: 10m, cost: 2m, createdDaysAgo: 300)
        };

        var sales = new List<InventoryRotationSalesAggregateDto>
        {
            Sale(1, units: 30m, revenue: 90m),
            Sale(2, units: 30m, revenue: 180m),
            Sale(4, units: 60m, revenue: 240m),
            Sale(7, units: 15m, revenue: 45m)
        };

        var lastSales = new List<InventoryRotationLastSaleDto>
        {
            LastSale(1, daysAgo: 10),
            LastSale(2, daysAgo: 70),
            LastSale(4, daysAgo: 5),
            LastSale(6, daysAgo: 200),
            LastSale(7, daysAgo: 40)
        };

        var filter = new InventoryRotationFilterDto
        {
            SalesWindowDays = 90,
            DeadStockDays = 60,
            Page = 1,
            PageSize = 10
        };

        var universe = InventoryRotationCalculator.BuildUniverse(products, sales, lastSales, filter, NowUtc);

        Assert.Equal(InventoryRotationClass.High, ById(universe, 1).RotationClass);
        Assert.Equal(30m, ById(universe, 1).AverageInventoryDays);
        Assert.Equal(InventoryRotationClass.DeadStock, ById(universe, 2).RotationClass);
        Assert.Equal(70, ById(universe, 2).DaysSinceLastSale);
        Assert.Equal(205m, universe.Summary.ImmobilizedCapitalUsd);
        Assert.True(ById(universe, 2).IsDeadStock);

        Assert.Equal(InventoryRotationClass.DeadStock, ById(universe, 3).RotationClass);
        Assert.Equal(100, ById(universe, 3).DaysSinceLastSale);
        Assert.Null(ById(universe, 3).LastSaleDateUtc);

        Assert.Equal(InventoryRotationClass.High, ById(universe, 4).RotationClass);
        Assert.Equal(30m, ById(universe, 4).AverageInventoryDays);

        Assert.Equal(InventoryRotationClass.Low, ById(universe, 5).RotationClass);
        Assert.Null(ById(universe, 5).AverageInventoryDays);

        Assert.Equal(InventoryRotationClass.Low, ById(universe, 6).RotationClass);
        Assert.False(ById(universe, 6).IsDeadStock);

        Assert.Equal(InventoryRotationClass.Medium, ById(universe, 7).RotationClass);
        Assert.Equal(60m, ById(universe, 7).AverageInventoryDays);

        Assert.Equal(7, universe.Summary.TotalProducts);
        Assert.Equal(2, universe.Summary.HighRotationCount);
        Assert.Equal(1, universe.Summary.MediumRotationCount);
        Assert.Equal(2, universe.Summary.LowRotationCount);
        Assert.Equal(2, universe.Summary.DeadStockCount);
        Assert.Equal(405m, universe.Summary.TotalInventoryValueUsd);
        Assert.Equal(205m, universe.Summary.ImmobilizedCapitalUsd);
        Assert.Equal(50.62m, universe.Summary.ImmobilizedCapitalPercent);
        Assert.Equal(28.57m, universe.Summary.DeadStockPercent);
    }

    [Fact]
    public void BuildUniverse_AggregatesCategoriesAndGroupsWithoutCategory()
    {
        var products = new List<InventoryRotationProductSnapshotDto>
        {
            Product(1, "Leche", "SKU-1", categoryId: 1, categoryName: "Lacteos", stock: 10m, cost: 2m, createdDaysAgo: 300),
            Product(2, "Queso", "SKU-2", categoryId: 1, categoryName: "Lacteos", stock: 40m, cost: 5m, createdDaysAgo: 300),
            Product(3, "Harina", "SKU-3", categoryId: null, categoryName: null, stock: 5m, cost: 1m, createdDaysAgo: 100),
            Product(4, "Arroz", "SKU-4", categoryId: 2, categoryName: "Abarrotes", stock: 20m, cost: 3m, createdDaysAgo: 300),
            Product(5, "Atun", "SKU-5", categoryId: 2, categoryName: "Abarrotes", stock: 100m, cost: 1m, createdDaysAgo: 20)
        };

        var sales = new List<InventoryRotationSalesAggregateDto>
        {
            Sale(1, 30m, 90m),
            Sale(2, 30m, 180m),
            Sale(4, 60m, 240m)
        };

        var lastSales = new List<InventoryRotationLastSaleDto>
        {
            LastSale(1, 10),
            LastSale(2, 70),
            LastSale(4, 5)
        };

        var universe = InventoryRotationCalculator.BuildUniverse(
            products,
            sales,
            lastSales,
            new InventoryRotationFilterDto { SalesWindowDays = 90, DeadStockDays = 60, PageSize = 10 },
            NowUtc);

        var dairy = Assert.Single(universe.Categories, category => category.CategoryId == 1);
        Assert.Equal("Lacteos", dairy.CategoryName);
        Assert.Equal(2, dairy.ProductCount);
        Assert.Equal(50m, dairy.StockQuantity);
        Assert.Equal(220m, dairy.InventoryValueUsd);
        Assert.Equal(60m, dairy.UnitsSold);
        Assert.Equal(270m, dairy.SalesRevenueUsd);
        Assert.Equal(75m, dairy.AverageInventoryDays);
        Assert.Equal(1, dairy.DeadStockCount);
        Assert.Equal(200m, dairy.ImmobilizedCapitalUsd);

        var groceries = Assert.Single(universe.Categories, category => category.CategoryId == 2);
        Assert.Equal(180m, groceries.AverageInventoryDays);

        var uncategorized = Assert.Single(universe.Categories, category => category.CategoryId == null);
        Assert.Equal("Sin categoria", uncategorized.CategoryName);
        Assert.Equal(1, uncategorized.DeadStockCount);
        Assert.Null(uncategorized.AverageInventoryDays);
    }

    [Fact]
    public void Build_AppliesClassFilterAndPaginationAfterGlobalSummary()
    {
        var products = new List<InventoryRotationProductSnapshotDto>
        {
            Product(1, "Leche", "SKU-1", 1, "Lacteos", 10m, 2m, 300),
            Product(2, "Queso", "SKU-2", 1, "Lacteos", 40m, 5m, 300),
            Product(3, "Harina", "SKU-3", null, null, 5m, 1m, 100),
            Product(4, "Arroz", "SKU-4", 2, "Abarrotes", 20m, 3m, 300)
        };

        var sales = new List<InventoryRotationSalesAggregateDto>
        {
            Sale(1, 30m, 90m),
            Sale(2, 30m, 180m),
            Sale(4, 60m, 240m)
        };

        var lastSales = new List<InventoryRotationLastSaleDto>
        {
            LastSale(1, 10),
            LastSale(2, 70),
            LastSale(4, 5)
        };

        var filter = new InventoryRotationFilterDto
        {
            SalesWindowDays = 90,
            DeadStockDays = 60,
            RotationClass = InventoryRotationClass.DeadStock,
            SortBy = "immobilizedCapital",
            Descending = true,
            Page = 2,
            PageSize = 1
        };

        var report = InventoryRotationCalculator.Build(products, sales, lastSales, filter, NowUtc);

        Assert.Equal(2, report.TotalCount);
        Assert.Equal(2, report.TotalPages);
        Assert.Single(report.Products);
        Assert.Equal(3, report.Products[0].ProductId);
        Assert.Equal(4, report.Summary.TotalProducts);
        Assert.Equal(2, report.Summary.DeadStockCount);
    }

    [Fact]
    public void BuildDeadStockSelection_TruncatesAtBulkPriceMaximum()
    {
        var products = Enumerable.Range(1, 1005)
            .Select(id => Product(id, $"Producto {id}", $"SKU-{id}", null, null, 1m, 1m, 100))
            .ToList();

        var universe = InventoryRotationCalculator.BuildUniverse(
            products,
            Array.Empty<InventoryRotationSalesAggregateDto>(),
            Array.Empty<InventoryRotationLastSaleDto>(),
            new InventoryRotationFilterDto { SalesWindowDays = 90, DeadStockDays = 60, PageSize = 10 },
            NowUtc);

        var selection = InventoryRotationCalculator.BuildDeadStockSelection(universe);

        Assert.Equal(1005, selection.TotalCount);
        Assert.True(selection.Truncated);
        Assert.Equal(1000, selection.ProductIds.Count);
    }

    [Fact]
    public void Build_RejectsInvalidFilters()
    {
        var products = Array.Empty<InventoryRotationProductSnapshotDto>();
        var sales = Array.Empty<InventoryRotationSalesAggregateDto>();
        var lastSales = Array.Empty<InventoryRotationLastSaleDto>();

        Assert.Throws<ArgumentException>(() => InventoryRotationCalculator.Build(
            products, sales, lastSales,
            new InventoryRotationFilterDto { SalesWindowDays = 0 },
            NowUtc));

        Assert.Throws<ArgumentException>(() => InventoryRotationCalculator.Build(
            products, sales, lastSales,
            new InventoryRotationFilterDto { DeadStockDays = 366 },
            NowUtc));

        Assert.Throws<ArgumentException>(() => InventoryRotationCalculator.Build(
            products, sales, lastSales,
            new InventoryRotationFilterDto { PageSize = 201 },
            NowUtc));

        Assert.Throws<ArgumentException>(() => InventoryRotationCalculator.Build(
            products, sales, lastSales,
            new InventoryRotationFilterDto { RotationClass = "Unknown" },
            NowUtc));
    }

    private static InventoryRotationProductDto ById(InventoryRotationUniverse universe, int id) =>
        Assert.Single(universe.Products, product => product.ProductId == id);

    private static InventoryRotationProductSnapshotDto Product(
        int id,
        string name,
        string sku,
        int? categoryId,
        string? categoryName,
        decimal stock,
        decimal cost,
        int createdDaysAgo) => new()
    {
        ProductId = id,
        Name = name,
        SKU = sku,
        CategoryId = categoryId,
        CategoryName = categoryName,
        IsActive = true,
        StockQuantity = stock,
        CostPriceUsd = cost,
        CreatedAtUtc = NowUtc.AddDays(-createdDaysAgo)
    };

    private static InventoryRotationSalesAggregateDto Sale(int productId, decimal units, decimal revenue) => new()
    {
        ProductId = productId,
        UnitsSold = units,
        SalesRevenueUsd = revenue
    };

    private static InventoryRotationLastSaleDto LastSale(int productId, int daysAgo) => new()
    {
        ProductId = productId,
        LastSaleDateUtc = NowUtc.AddDays(-daysAgo)
    };
}
