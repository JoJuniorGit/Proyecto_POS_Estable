using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 15 (S1/S7): el servicio debe aplicar la semantica de ventas validas en SQL y no contar
/// avances, pendientes, anuladas ni ventas sin factura. La calculadora ya esta cubierta aparte.
/// </summary>
public class InventoryRotationServiceTests
{
    private static readonly DateTime NowUtc = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetReportAsync_CountsOnlyIssuedCompletedMerchandiseInWindow()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using var _ = context;
        using var __ = connection;
        await SeedSalesAsync(context);

        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(service => service.GetInventoryRotationProductsAsync(
                It.IsAny<IReadOnlyCollection<int>?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSnapshots());

        var service = new InventoryRotationService(context, inventory.Object, new FixedTimeProvider(NowUtc));
        var report = await service.GetReportAsync(new InventoryRotationFilterDto
        {
            SalesWindowDays = 90,
            DeadStockDays = 60,
            PageSize = 20
        });

        var p1 = Assert.Single(report.Products, product => product.ProductId == 1);
        Assert.Equal(5m, p1.UnitsSold);
        Assert.Equal(50m, p1.SalesRevenueUsd);
        Assert.Equal(NowUtc.AddDays(-10).Date, p1.LastSaleDateUtc?.Date);

        var p2 = Assert.Single(report.Products, product => product.ProductId == 2);
        Assert.Equal(0m, p2.UnitsSold);
        Assert.Equal(NowUtc.AddDays(-100).Date, p2.LastSaleDateUtc?.Date);
        Assert.Equal(InventoryRotationClass.DeadStock, p2.RotationClass);

        var p3 = Assert.Single(report.Products, product => product.ProductId == 3);
        Assert.Equal(0m, p3.UnitsSold);

        Assert.Equal(0m, Assert.Single(report.Products, product => product.ProductId == 4).UnitsSold);
        Assert.Equal(0m, Assert.Single(report.Products, product => product.ProductId == 5).UnitsSold);
        Assert.Equal(0m, Assert.Single(report.Products, product => product.ProductId == 6).UnitsSold);
    }

    [Fact]
    public async Task GetReportAsync_ResolvesCategoryFilterBeforeSalesQuery()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using var _ = context;
        using var __ = connection;
        await SeedSalesAsync(context);

        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(service => service.GetProductIdsByCategoryAsync(
                It.IsAny<IReadOnlyCollection<int>>(),
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { 1, 2 });
        inventory
            .Setup(service => service.GetInventoryRotationProductsAsync(
                It.Is<IReadOnlyCollection<int>?>(ids => ids != null && ids.SequenceEqual(new[] { 1, 2 })),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSnapshots().Where(product => product.ProductId is 1 or 2).ToList());

        var service = new InventoryRotationService(context, inventory.Object, new FixedTimeProvider(NowUtc));
        var report = await service.GetReportAsync(new InventoryRotationFilterDto
        {
            CategoryId = 7,
            IncludeCategorySubtree = true,
            PageSize = 20
        });

        Assert.Equal(2, report.TotalCount);
        Assert.Equal(2, report.Summary.TotalProducts);
        inventory.Verify(service => service.GetInventoryRotationProductsAsync(
            It.Is<IReadOnlyCollection<int>?>(ids => ids != null && ids.SequenceEqual(new[] { 1, 2 })),
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetDeadStockProductIdsAsync_ReturnsSelectionFromSameUniverse()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using var _ = context;
        using var __ = connection;
        await SeedSalesAsync(context);

        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(service => service.GetInventoryRotationProductsAsync(
                It.IsAny<IReadOnlyCollection<int>?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSnapshots());

        var service = new InventoryRotationService(context, inventory.Object, new FixedTimeProvider(NowUtc));
        var selection = await service.GetDeadStockProductIdsAsync(new InventoryRotationFilterDto
        {
            SalesWindowDays = 90,
            DeadStockDays = 60
        });

        Assert.Contains(2, selection.ProductIds);
        Assert.False(selection.Truncated);
        Assert.Equal(selection.ProductIds.Count, selection.TotalCount);
    }

    [Fact]
    public async Task GetReportAsync_RejectsInvalidFilterBeforeCallingInventory()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using var _ = context;
        using var __ = connection;

        var inventory = new Mock<IInventoryService>(MockBehavior.Strict);
        var service = new InventoryRotationService(context, inventory.Object, new FixedTimeProvider(NowUtc));

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetReportAsync(
            new InventoryRotationFilterDto { SalesWindowDays = 0 }));

        inventory.VerifyNoOtherCalls();
    }

    private static async Task SeedSalesAsync(SalesDbContext context)
    {
        context.Sales.AddRange(
            Sale(1, NowUtc.AddDays(-10), SaleStatus.Completed, SaleType.Merchandise, invoiceNumber: 1001,
                items: new[] { Item(1, 5m, 50m) }),
            Sale(2, NowUtc.AddDays(-100), SaleStatus.Completed, SaleType.Merchandise, invoiceNumber: 1002,
                items: new[] { Item(2, 7m, 70m) }),
            Sale(3, NowUtc.AddDays(-5), SaleStatus.Completed, SaleType.CashAdvance, invoiceNumber: 1003,
                items: new[] { Item(3, 100m, 100m) }),
            Sale(4, NowUtc.AddDays(-5), SaleStatus.Completed, SaleType.Merchandise, invoiceNumber: null,
                items: new[] { Item(4, 100m, 100m) }),
            Sale(5, NowUtc.AddDays(-5), SaleStatus.Cancelled, SaleType.Merchandise, invoiceNumber: 1005,
                items: new[] { Item(5, 100m, 100m) }, cancelledAtUtc: NowUtc.AddDays(-1)),
            Sale(6, NowUtc.AddDays(-5), SaleStatus.Pending, SaleType.Merchandise, invoiceNumber: null,
                items: new[] { Item(6, 100m, 100m) }));

        await context.SaveChangesAsync();
    }

    private static Sale Sale(
        int id,
        DateTime date,
        SaleStatus status,
        SaleType saleType,
        int? invoiceNumber,
        IEnumerable<SaleItem> items,
        DateTime? cancelledAtUtc = null)
    {
        var sale = new Sale
        {
            Id = id,
            Date = date,
            Status = status,
            SaleType = saleType,
            InvoiceNumber = invoiceNumber,
            CancelledAtUtc = cancelledAtUtc,
            Subtotal = items.Sum(item => item.Subtotal),
            TotalUSD = items.Sum(item => item.Subtotal),
            TotalBsS = items.Sum(item => item.Subtotal),
            FinalPaidAmountBsS = items.Sum(item => item.Subtotal)
        };

        foreach (var item in items)
        {
            item.SaleId = id;
            sale.Items.Add(item);
        }

        return sale;
    }

    private static SaleItem Item(int productId, decimal quantity, decimal subtotal) => new()
    {
        ProductId = productId,
        ProductName = $"Producto {productId}",
        Quantity = quantity,
        Subtotal = subtotal,
        UnitPrice = quantity == 0m ? 0m : subtotal / quantity,
        UnitPriceBsS = quantity == 0m ? 0m : subtotal / quantity,
        SubtotalBsS = subtotal
    };

    private static IReadOnlyList<InventoryRotationProductSnapshotDto> BuildSnapshots() => new[]
    {
        Snapshot(1, stock: 10m, createdDaysAgo: 300),
        Snapshot(2, stock: 5m, createdDaysAgo: 300),
        Snapshot(3, stock: 1m, createdDaysAgo: 300),
        Snapshot(4, stock: 1m, createdDaysAgo: 300),
        Snapshot(5, stock: 1m, createdDaysAgo: 300),
        Snapshot(6, stock: 1m, createdDaysAgo: 300)
    };

    private static InventoryRotationProductSnapshotDto Snapshot(int id, decimal stock, int createdDaysAgo) => new()
    {
        ProductId = id,
        Name = $"Producto {id}",
        SKU = $"SKU-{id}",
        IsActive = true,
        StockQuantity = stock,
        CostPriceUsd = 1m,
        CreatedAtUtc = NowUtc.AddDays(-createdDaysAgo)
    };

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTime nowUtc)
        {
            _now = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
