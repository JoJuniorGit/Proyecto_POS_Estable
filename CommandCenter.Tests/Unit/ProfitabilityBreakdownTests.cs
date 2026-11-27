using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs.Profitability;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 14 (F2-WU4, F2-S4): series temporales, desgloses por método de pago y por
/// producto/categoría/vendedor, alertas de erosión de margen y soporte Single/Dual. El filtro por
/// categoría se resuelve con el catálogo de inventario mockeado; los montos se agregan por el
/// timestamp del evento subyacente, sin prorratear totales.
/// </summary>
public class ProfitabilityBreakdownTests
{
    private const string TimeZoneId = "Profitability/UTC-4";

    private static readonly TimeZoneInfo FixedTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        TimeZoneId,
        TimeSpan.FromHours(-4),
        "Profitability UTC-4",
        "Profitability UTC-4");

    private static readonly DateTime InWindow = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetSummaryAsync_CategoryFilter_UsesInventoryCatalogWithoutThrowing()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        SeedTwoProductSales(context);

        var inventory = CreateInventoryService(
            categoryProductIds: new[] { 10 },
            productInfo: ProductInfo((10, "Leche", 2, "Lácteos"), (20, "Refresco", 4, "Bebidas")));

        var filter = CreateWindowFilter();
        filter.CategoryIds = new[] { 7 };

        var summary = await CreateService(context, MonetaryMode.Dual, inventory.Object)
            .GetSummaryAsync(filter);

        Assert.Equal(100m, summary.Accrued.GrossSales);
        Assert.Equal(100m, summary.Accrued.NetSales);
        // Product 10 sells 2 units with a 30 USD unit cost: 60 USD of COGS and 40 USD of profit.
        Assert.Equal(60m, summary.Accrued.Cogs);
        Assert.Equal(40m, summary.Accrued.GrossProfit);

        var product = Assert.Single(summary.Breakdown.Products);
        Assert.Equal(10, product.ProductId);
        Assert.Equal(2, product.CategoryId!.Value);
        Assert.Equal("Lácteos", product.CategoryName);

        var category = Assert.Single(summary.Breakdown.Categories);
        Assert.Equal(2, category.CategoryId);
        Assert.Equal("Lácteos", category.CategoryName);

        inventory.Verify(service => service.GetProductIdsByCategoryAsync(
            It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 7 })),
            true,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetSummaryAsync_CategoryAndProductFilters_IntersectEffectiveProductIds()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        SeedTwoProductSales(context);

        var inventory = CreateInventoryService(
            categoryProductIds: new[] { 10, 20 },
            productInfo: ProductInfo((10, "Leche", 2, "Lácteos"), (20, "Refresco", 4, "Bebidas")));

        var filter = CreateWindowFilter();
        filter.CategoryIds = new[] { 7 };
        filter.ProductIds = new[] { 20 };

        var summary = await CreateService(context, MonetaryMode.Dual, inventory.Object)
            .GetSummaryAsync(filter);

        Assert.Equal(60m, summary.Accrued.GrossSales);
        Assert.Equal(60m, summary.Accrued.NetSales);
        Assert.Equal(20m, summary.Accrued.Cogs);
        Assert.Equal(40m, summary.Accrued.GrossProfit);

        var product = Assert.Single(summary.Breakdown.Products);
        Assert.Equal(20, product.ProductId);

        inventory.Verify(service => service.GetProductCategoryInfoAsync(
            It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 20 })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetSummaryAsync_SeriesByDay_AttributesEachSaleToItsOwnPeriod()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.PaymentMethods.Add(
            new PaymentMethod { Id = 3, Name = "Punto de Venta", IsCash = false, IsActive = true });

        var firstSale = CreateSale(1, new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
            totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                unitCostUsd: 30m, unitCostLocal: 1500m));
        var secondSale = CreateSale(2, new DateTime(2026, 11, 3, 14, 0, 0, DateTimeKind.Utc),
            totalUsd: 200m, totalBsS: 10000m, rate: 50m,
            items: CreateItem(2, productId: 10, quantity: 1m, subtotalUsd: 200m, subtotalBsS: 10000m,
                unitCostUsd: 100m, unitCostLocal: 5000m));

        context.Sales.AddRange(firstSale, secondSale);
        context.SalePayments.AddRange(
            CreatePayment(1, saleId: 1, createdAt: new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
                amountUsd: 100m, amountBsS: 5000m, rate: 50m, feeUsd: 0m, feeLocal: 0m, feeConfigured: false),
            CreatePayment(2, saleId: 2, createdAt: new DateTime(2026, 11, 3, 14, 0, 0, DateTimeKind.Utc),
                amountUsd: 200m, amountBsS: 10000m, rate: 50m, feeUsd: 0m, feeLocal: 0m, feeConfigured: false));

        await context.SaveChangesAsync();

        var filter = CreateWindowFilter(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3));
        filter.GroupBy = ProfitabilityGroupBy.Day;

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(filter);
        var points = summary.Breakdown.Series;

        Assert.Equal(2, points.Count);
        Assert.Equal(new DateTime(2026, 11, 2, 4, 0, 0, DateTimeKind.Utc), points[0].PeriodStartUtc);
        Assert.Equal(new DateTime(2026, 11, 3, 4, 0, 0, DateTimeKind.Utc), points[1].PeriodStartUtc);

        // Each period receives only its own sale/payment; nothing is proportionally distributed.
        Assert.Equal(100m, points[0].AccruedNetSales);
        Assert.Equal(70m, points[0].AccruedNetProfit);
        Assert.Equal(100m, points[0].RealizedCollectedRevenue);
        Assert.Equal(70m, points[0].RealizedNetProfit);

        Assert.Equal(200m, points[1].AccruedNetSales);
        Assert.Equal(100m, points[1].AccruedNetProfit);
        Assert.Equal(200m, points[1].RealizedCollectedRevenue);
        Assert.Equal(100m, points[1].RealizedNetProfit);

        Assert.False(string.IsNullOrWhiteSpace(points[0].Label));
        Assert.False(string.IsNullOrWhiteSpace(points[1].Label));
    }

    [Fact]
    public async Task GetSummaryAsync_HourSeries_FillsEmptyHoursAndAttributesEvents()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        // Local UTC-4: 12:00Z = 08:00 and 14:00Z = 10:00. The 09:00 hour stays empty.
        context.Sales.AddRange(
            CreateSale(1, new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
                totalUsd: 100m, totalBsS: 5000m, rate: 50m,
                items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                    unitCostUsd: 30m, unitCostLocal: 1500m)),
            CreateSale(2, new DateTime(2026, 11, 2, 14, 0, 0, DateTimeKind.Utc),
                totalUsd: 200m, totalBsS: 10000m, rate: 50m,
                items: CreateItem(2, productId: 10, quantity: 1m, subtotalUsd: 200m, subtotalBsS: 10000m,
                    unitCostUsd: 100m, unitCostLocal: 5000m)));

        await context.SaveChangesAsync();

        var filter = CreateWindowFilter();
        filter.GroupBy = ProfitabilityGroupBy.Hour;

        var points = (await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(filter))
            .Breakdown.Series;

        Assert.Equal(24, points.Count);
        Assert.Equal(new DateTime(2026, 11, 2, 4, 0, 0, DateTimeKind.Utc), points[0].PeriodStartUtc);
        Assert.Equal(100m, points[8].AccruedNetSales);
        Assert.Equal(0m, points[9].AccruedNetSales);
        Assert.Equal(200m, points[10].AccruedNetSales);
    }

    [Fact]
    public async Task GetSummaryAsync_WeekSeries_GroupsByLocalMonday()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        // 2026-11-02 is Monday: the custom window covers two local weeks.
        context.Sales.AddRange(
            CreateSale(1, new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc),
                totalUsd: 100m, totalBsS: 5000m, rate: 50m,
                items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                    unitCostUsd: 30m, unitCostLocal: 1500m)),
            CreateSale(2, new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc),
                totalUsd: 200m, totalBsS: 10000m, rate: 50m,
                items: CreateItem(2, productId: 10, quantity: 1m, subtotalUsd: 200m, subtotalBsS: 10000m,
                    unitCostUsd: 100m, unitCostLocal: 5000m)));

        await context.SaveChangesAsync();

        var filter = CreateWindowFilter(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 15));
        filter.GroupBy = ProfitabilityGroupBy.Week;

        var points = (await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(filter))
            .Breakdown.Series;

        Assert.Equal(2, points.Count);
        Assert.Equal(new DateTime(2026, 11, 2, 4, 0, 0, DateTimeKind.Utc), points[0].PeriodStartUtc);
        Assert.Equal(new DateTime(2026, 11, 9, 4, 0, 0, DateTimeKind.Utc), points[1].PeriodStartUtc);
        Assert.Equal(100m, points[0].AccruedNetSales);
        Assert.Equal(200m, points[1].AccruedNetSales);
    }

    [Fact]
    public async Task GetSummaryAsync_MonthSeries_GroupsByLocalMonth()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.AddRange(
            CreateSale(1, new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc),
                totalUsd: 100m, totalBsS: 5000m, rate: 50m,
                items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                    unitCostUsd: 30m, unitCostLocal: 1500m)),
            CreateSale(2, new DateTime(2026, 12, 15, 12, 0, 0, DateTimeKind.Utc),
                totalUsd: 200m, totalBsS: 10000m, rate: 50m,
                items: CreateItem(2, productId: 10, quantity: 1m, subtotalUsd: 200m, subtotalBsS: 10000m,
                    unitCostUsd: 100m, unitCostLocal: 5000m)));

        await context.SaveChangesAsync();

        var filter = CreateWindowFilter(new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31));
        filter.GroupBy = ProfitabilityGroupBy.Month;

        var points = (await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(filter))
            .Breakdown.Series;

        Assert.Equal(2, points.Count);
        Assert.Equal("2026-11", points[0].Label);
        Assert.Equal("2026-12", points[1].Label);
        Assert.Equal(100m, points[0].AccruedNetSales);
        Assert.Equal(200m, points[1].AccruedNetSales);
    }

    [Fact]
    public async Task GetSummaryAsync_HourSeries_SkipsNonexistentSpringForwardHour()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.Add(CreateSale(1, new DateTime(2026, 3, 8, 12, 0, 0, DateTimeKind.Utc),
            totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                unitCostUsd: 30m, unitCostLocal: 1500m)));
        await context.SaveChangesAsync();

        // US spring-forward 2026: 02:00 local does not exist (jumps to 03:00).
        var eastern = FindEasternTimeZone();
        var filter = CreateWindowFilter(new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 8));
        filter.GroupBy = ProfitabilityGroupBy.Hour;

        var summary = await CreateService(context, MonetaryMode.Dual, timeZone: eastern)
            .GetSummaryAsync(filter);
        var points = summary.Breakdown.Series;

        Assert.Equal(23, points.Count);
        Assert.DoesNotContain(points, point => point.Label.EndsWith("02:00", StringComparison.Ordinal));
        Assert.Contains(points, point => point.Label.EndsWith("03:00", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetSummaryAsync_SingleReturnedRevenue_UsesSaleAppliedRate()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.Add(CreateSale(1, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                unitCostUsd: 30m, unitCostLocal: 1500m)));
        // RefundRate 60 is the cash-refund rate; the invoice snapshot is 50.
        context.CreditReturns.Add(CreateReturn(1, saleId: 1, createdAt: InWindow.AddHours(1),
            returnedUsd: 50m, refundRate: 60m,
            CreateReturnItem(1, saleItemId: 1, quantity: 1m, amountUsd: 50m)));
        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Single).GetSummaryAsync(CreateWindowFilter());

        // The accrued headline follows the same sale snapshot: 50 USD * 50 = 2500 local,
        // not the cash-refund rate (60) used by CreditReturn.RefundRate.
        Assert.Equal(2500m, summary.Accrued.Returns);
        Assert.Equal(2500m, summary.Accrued.NetSales);

        var point = Assert.Single(summary.Breakdown.Series);
        Assert.Equal(2500m, point.AccruedNetSales);

        var seller = Assert.Single(summary.Breakdown.Sellers);
        Assert.Equal(2500m, seller.NetSales);
    }

    [Theory]
    [InlineData(MonetaryMode.Dual, false, false)]
    [InlineData(MonetaryMode.Dual, true, false)]
    [InlineData(MonetaryMode.Dual, false, true)]
    [InlineData(MonetaryMode.Dual, true, true)]
    [InlineData(MonetaryMode.Single, false, false)]
    [InlineData(MonetaryMode.Single, true, false)]
    [InlineData(MonetaryMode.Single, false, true)]
    [InlineData(MonetaryMode.Single, true, true)]
    public async Task GetSummaryAsync_PaymentMethodBreakdown_ReconcilesWithAccruedFees(
        MonetaryMode mode,
        bool includeCancellation,
        bool filterByMethod)
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 3, Name = "Punto de Venta", IsCash = false, IsActive = true },
            new PaymentMethod { Id = 4, Name = "Pago Móvil", IsCash = false, IsActive = true });

        context.Sales.Add(CreateSale(1, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                unitCostUsd: 30m, unitCostLocal: 1500m)));
        context.SalePayments.Add(CreatePayment(1, saleId: 1, createdAt: InWindow,
            amountUsd: 100m, amountBsS: 5000m, rate: 50m, feeUsd: 1m, feeLocal: 50m,
            feeConfigured: true, paymentMethodId: 4));

        if (includeCancellation)
        {
            // Collected before the window and cancelled inside it: only the fee reversal belongs
            // to this period, attributed to the original method (3).
            context.Sales.Add(CreateSale(2, new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc),
                totalUsd: 50m, totalBsS: 2500m, rate: 50m,
                cancelledAtUtc: InWindow.AddHours(3),
                items: CreateItem(2, productId: 20, quantity: 1m, subtotalUsd: 50m, subtotalBsS: 2500m,
                    unitCostUsd: 20m, unitCostLocal: 1000m)));
            context.SalePayments.Add(CreatePayment(2, saleId: 2,
                createdAt: new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc),
                amountUsd: 50m, amountBsS: 2500m, rate: 50m, feeUsd: 1.5m, feeLocal: 75m,
                feeConfigured: true, paymentMethodId: 3));
        }

        await context.SaveChangesAsync();

        var filter = CreateWindowFilter();
        if (filterByMethod)
        {
            filter.PaymentMethodIds = new[] { 3 };
        }

        var summary = await CreateService(context, mode).GetSummaryAsync(filter);
        var breakdownFees = summary.Breakdown.PaymentMethods.Sum(row => row.FeeAmount);

        Assert.Equal(summary.Accrued.PaymentMethodFees, breakdownFees);
        // The same method filter reaches the series events, so breakdown totals reconcile with
        // both headline blocks.
        Assert.Equal(summary.Accrued.NetProfit, summary.Breakdown.Series.Sum(point => point.AccruedNetProfit));
        Assert.Equal(summary.Realized.NetProfit, summary.Breakdown.Series.Sum(point => point.RealizedNetProfit));

        if (includeCancellation && filterByMethod)
        {
            // The reversal is the only activity of method 3 in the period: the row still exists.
            var reversalRow = Assert.Single(summary.Breakdown.PaymentMethods);
            Assert.Equal(3, reversalRow.PaymentMethodId);
            Assert.Equal(0m, reversalRow.CollectedAmount);
            Assert.Equal(mode == MonetaryMode.Single ? -75m : -1.5m, reversalRow.FeeAmount);
        }
    }

    [Fact]
    public async Task GetSummaryAsync_PaymentMethodBreakdown_SeparatesKnownAndCashFees()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo", IsCash = true, IsActive = true },
            new PaymentMethod { Id = 3, Name = "Punto de Venta", IsCash = false, IsActive = true },
            new PaymentMethod { Id = 4, Name = "Pago Móvil", IsCash = false, IsActive = true });

        context.Sales.Add(CreateSale(1, InWindow, totalUsd: 180m, totalBsS: 9000m, rate: 50m,
            items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 180m, subtotalBsS: 9000m,
                unitCostUsd: 60m, unitCostLocal: 3000m)));

        context.SalePayments.AddRange(
            CreatePayment(1, saleId: 1, createdAt: InWindow, amountUsd: 100m, amountBsS: 5000m,
                rate: 50m, feeUsd: 2m, feeLocal: 100m, feeConfigured: true, paymentMethodId: 3),
            CreatePayment(2, saleId: 1, createdAt: InWindow, amountUsd: 50m, amountBsS: 2500m,
                rate: 50m, feeUsd: 0m, feeLocal: 0m, feeConfigured: false, paymentMethodId: 1),
            CreatePayment(3, saleId: 1, createdAt: InWindow, amountUsd: 30m, amountBsS: 1500m,
                rate: 50m, feeUsd: 0m, feeLocal: 0m, feeConfigured: false, paymentMethodId: 4));

        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(CreateWindowFilter());
        var breakdown = summary.Breakdown.PaymentMethods;

        var card = breakdown.Single(row => row.PaymentMethodId == 3);
        Assert.Equal("Punto de Venta", card.PaymentMethodName);
        Assert.Equal(100m, card.CollectedAmount);
        Assert.Equal(2m, card.FeeAmount);
        Assert.Equal(100m, card.KnownFeeAmount);
        Assert.Equal(0m, card.UnknownFeeAmount);
        Assert.Equal(100m, card.FeeCoveragePercent);

        var cash = breakdown.Single(row => row.PaymentMethodId == 1);
        Assert.Equal("Efectivo", cash.PaymentMethodName);
        Assert.Equal(50m, cash.CollectedAmount);
        Assert.Equal(0m, cash.FeeAmount);
        Assert.Equal(0m, cash.KnownFeeAmount);
        Assert.Equal(0m, cash.UnknownFeeAmount);
        Assert.Equal(100m, cash.FeeCoveragePercent);

        var mobile = breakdown.Single(row => row.PaymentMethodId == 4);
        Assert.Equal(30m, mobile.CollectedAmount);
        Assert.Equal(0m, mobile.KnownFeeAmount);
        Assert.Equal(30m, mobile.UnknownFeeAmount);
        Assert.Equal(0m, mobile.FeeCoveragePercent);

        var filtered = CreateWindowFilter();
        filtered.PaymentMethodIds = new[] { 3 };
        var filteredSummary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(filtered);
        var filteredRow = Assert.Single(filteredSummary.Breakdown.PaymentMethods);
        Assert.Equal(3, filteredRow.PaymentMethodId);
    }

    [Fact]
    public async Task GetSummaryAsync_ProductAndCategoryBreakdown_RanksByGrossProfit()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.Add(CreateSale(1, InWindow, totalUsd: 250m, totalBsS: 12500m, rate: 50m,
            items: new[]
            {
                CreateItem(1, productId: 10, quantity: 2m, subtotalUsd: 100m, subtotalBsS: 5000m,
                    unitCostUsd: 30m, unitCostLocal: 1500m),
                CreateItem(2, productId: 20, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                    unitCostUsd: 80m, unitCostLocal: 4000m),
                CreateItem(3, productId: 30, quantity: 1m, subtotalUsd: 50m, subtotalBsS: 2500m,
                    unitCostUsd: null, unitCostLocal: null)
            }));
        await context.SaveChangesAsync();

        var inventory = CreateInventoryService(productInfo: ProductInfo(
            (10, "Leche", 2, "Lácteos"),
            (20, "Refresco", 4, "Bebidas")));

        var summary = await CreateService(context, MonetaryMode.Dual, inventory.Object)
            .GetSummaryAsync(CreateWindowFilter());

        var products = summary.Breakdown.Products;
        Assert.Equal(new[] { 10, 20, 30 }, products.Select(product => product.ProductId));

        var milk = products[0];
        Assert.Equal("Leche", milk.ProductName);
        Assert.Equal(2m, milk.QuantitySold);
        Assert.Equal(100m, milk.Revenue);
        Assert.Equal(60m, milk.Cogs);
        Assert.Equal(40m, milk.GrossProfit);
        Assert.Equal(40m, milk.GrossMarginPercent);
        Assert.Equal(2, milk.CategoryId!.Value);
        Assert.Equal("Lácteos", milk.CategoryName);

        var soda = products[1];
        Assert.Equal(100m, soda.Revenue);
        Assert.Equal(80m, soda.Cogs);
        Assert.Equal(20m, soda.GrossProfit);
        Assert.Equal(20m, soda.GrossMarginPercent);

        // Unknown-cost revenue stays outside the margin, exactly like the accrued engine.
        var uncategorized = products[2];
        Assert.Equal(50m, uncategorized.Revenue);
        Assert.Equal(0m, uncategorized.Cogs);
        Assert.Equal(0m, uncategorized.GrossProfit);
        Assert.Equal(0m, uncategorized.GrossMarginPercent);
        Assert.Null(uncategorized.CategoryId);
        Assert.Equal("Sin categoría", uncategorized.CategoryName);

        var categories = summary.Breakdown.Categories;
        Assert.Equal(3, categories.Count);
        Assert.Equal(2, categories[0].CategoryId);
        Assert.Equal(40m, categories[0].GrossProfit);
        Assert.Equal(40m, categories[0].GrossMarginPercent);
        Assert.Equal(Math.Round(40m / 60m * 100m, 4), Math.Round(categories[0].ContributionPercent, 4));

        Assert.Equal(4, categories[1].CategoryId);
        Assert.Equal(20m, categories[1].GrossProfit);
        Assert.Equal(Math.Round(20m / 60m * 100m, 4), Math.Round(categories[1].ContributionPercent, 4));

        Assert.Equal(0, categories[2].CategoryId);
        Assert.Equal("Sin categoría", categories[2].CategoryName);
        Assert.Equal(0m, categories[2].ContributionPercent);
    }

    [Fact]
    public async Task GetSummaryAsync_SellerBreakdown_GroupsAccruedSalesByCashier()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Users.AddRange(
            new User { Id = 101, Cedula = "V-101", Name = "Ana", Username = "ana" },
            new User { Id = 102, Cedula = "V-102", Name = "Beto", Username = "beto" });

        context.Sales.AddRange(
            CreateSale(1, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m, cashierId: 101,
                items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                    unitCostUsd: 30m, unitCostLocal: 1500m)),
            CreateSale(2, InWindow, totalUsd: 60m, totalBsS: 3000m, rate: 50m, cashierId: 102,
                items: CreateItem(2, productId: 20, quantity: 1m, subtotalUsd: 60m, subtotalBsS: 3000m,
                    unitCostUsd: 20m, unitCostLocal: 1000m)),
            CreateSale(3, InWindow, totalUsd: 40m, totalBsS: 2000m, rate: 50m,
                items: CreateItem(3, productId: 30, quantity: 1m, subtotalUsd: 40m, subtotalBsS: 2000m,
                    unitCostUsd: 10m, unitCostLocal: 500m)));

        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(CreateWindowFilter());
        var sellers = summary.Breakdown.Sellers;

        var ana = sellers.Single(seller => seller.CashierId == 101);
        Assert.Equal("Ana", ana.CashierName);
        Assert.Equal(100m, ana.NetSales);
        Assert.Equal(70m, ana.GrossProfit);

        var beto = sellers.Single(seller => seller.CashierId == 102);
        Assert.Equal("Beto", beto.CashierName);
        Assert.Equal(60m, beto.NetSales);
        Assert.Equal(40m, beto.GrossProfit);

        var unassigned = sellers.Single(seller => seller.CashierId == null);
        Assert.Equal("Sin cajero", unassigned.CashierName);
        Assert.Equal(40m, unassigned.NetSales);
        Assert.Equal(30m, unassigned.GrossProfit);
    }

    [Theory]
    [InlineData(80, ProfitabilityAlertSeverity.Critical, 20)]
    [InlineData(70, ProfitabilityAlertSeverity.Critical, 10)]
    [InlineData(65, ProfitabilityAlertSeverity.Warning, 5)]
    public async Task GetSummaryAsync_MarginErosion_AlertSeverityFollowsDelta(
        int currentCostUsd,
        ProfitabilityAlertSeverity expectedSeverity,
        int expectedDelta)
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.AddRange(
            // Previous period (local Nov 1): revenue 100, cost 60 -> 40% gross margin.
            CreateSale(1, new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
                totalUsd: 100m, totalBsS: 5000m, rate: 50m,
                items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                    unitCostUsd: 60m, unitCostLocal: 3000m)),
            // Current period (local Nov 2): same revenue, eroded cost/margin.
            CreateSale(2, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
                items: CreateItem(2, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                    unitCostUsd: currentCostUsd, unitCostLocal: currentCostUsd * 50m)));

        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(CreateWindowFilter());

        var alert = Assert.Single(summary.Alerts);
        Assert.Equal("MARGIN_EROSION", alert.Code);
        Assert.Equal(expectedSeverity, alert.Severity);
        Assert.Equal(40m, alert.PreviousValue);
        Assert.Equal(100m - currentCostUsd, alert.CurrentValue);
        Assert.Equal((decimal)expectedDelta, alert.Delta);
        Assert.False(string.IsNullOrWhiteSpace(alert.Message));
    }

    [Fact]
    public async Task GetSummaryAsync_MarginErosion_WithoutPreviousRevenue_DoesNotAlert()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.Add(CreateSale(1, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                unitCostUsd: 80m, unitCostLocal: 4000m)));
        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(CreateWindowFilter());

        Assert.Empty(summary.Alerts);
    }

    [Fact]
    public async Task GetSummaryAsync_DualBreakdowns_UseUsdAmounts()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        SeedSingleCurrencySale(context);

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(CreateWindowFilter());

        var point = Assert.Single(summary.Breakdown.Series);
        Assert.Equal(100m, point.AccruedNetSales);
        Assert.Equal(68m, point.AccruedNetProfit);
        Assert.Equal(100m, point.RealizedCollectedRevenue);
        Assert.Equal(68m, point.RealizedNetProfit);

        var payment = Assert.Single(summary.Breakdown.PaymentMethods);
        Assert.Equal(100m, payment.CollectedAmount);
        Assert.Equal(2m, payment.FeeAmount);

        var product = Assert.Single(summary.Breakdown.Products);
        Assert.Equal(100m, product.Revenue);
        Assert.Equal(30m, product.Cogs);
        Assert.Equal(70m, product.GrossProfit);
    }

    [Fact]
    public async Task GetSummaryAsync_SingleBreakdowns_UseLocalSnapshots()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        SeedSingleCurrencySale(context);

        var summary = await CreateService(context, MonetaryMode.Single).GetSummaryAsync(CreateWindowFilter());

        Assert.Equal("COP", summary.ReportCurrencyCode);

        var point = Assert.Single(summary.Breakdown.Series);
        Assert.Equal(5000m, point.AccruedNetSales);
        Assert.Equal(3400m, point.AccruedNetProfit);
        Assert.Equal(5000m, point.RealizedCollectedRevenue);
        Assert.Equal(3400m, point.RealizedNetProfit);

        var payment = Assert.Single(summary.Breakdown.PaymentMethods);
        Assert.Equal(5000m, payment.CollectedAmount);
        Assert.Equal(100m, payment.FeeAmount);

        var product = Assert.Single(summary.Breakdown.Products);
        Assert.Equal(5000m, product.Revenue);
        Assert.Equal(1500m, product.Cogs);
        Assert.Equal(3500m, product.GrossProfit);
    }

    private static void SeedTwoProductSales(SalesDbContext context)
    {
        context.PaymentMethods.Add(
            new PaymentMethod { Id = 3, Name = "Punto de Venta", IsCash = false, IsActive = true });

        var firstSale = CreateSale(1, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: CreateItem(1, productId: 10, quantity: 2m, subtotalUsd: 100m, subtotalBsS: 5000m,
                unitCostUsd: 30m, unitCostLocal: 1500m));
        var secondSale = CreateSale(2, InWindow, totalUsd: 60m, totalBsS: 3000m, rate: 50m,
            items: CreateItem(2, productId: 20, quantity: 1m, subtotalUsd: 60m, subtotalBsS: 3000m,
                unitCostUsd: 20m, unitCostLocal: 1000m));

        context.Sales.AddRange(firstSale, secondSale);
        context.SalePayments.AddRange(
            CreatePayment(1, saleId: 1, createdAt: InWindow, amountUsd: 100m, amountBsS: 5000m,
                rate: 50m, feeUsd: 0m, feeLocal: 0m, feeConfigured: false),
            CreatePayment(2, saleId: 2, createdAt: InWindow, amountUsd: 60m, amountBsS: 3000m,
                rate: 50m, feeUsd: 0m, feeLocal: 0m, feeConfigured: false));

        context.SaveChanges();
    }

    private static void SeedSingleCurrencySale(SalesDbContext context)
    {
        context.PaymentMethods.Add(
            new PaymentMethod { Id = 3, Name = "Punto de Venta", IsCash = false, IsActive = true });

        context.Sales.Add(CreateSale(1, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                unitCostUsd: 30m, unitCostLocal: 1500m)));
        context.SalePayments.Add(CreatePayment(1, saleId: 1, createdAt: InWindow,
            amountUsd: 100m, amountBsS: 5000m, rate: 50m, feeUsd: 2m, feeLocal: 100m,
            feeConfigured: true, paymentMethodId: 3));

        context.SaveChanges();
    }

    private static ProfitabilityFilterDto CreateWindowFilter(DateOnly? fromDate = null, DateOnly? toDate = null) => new()
    {
        Preset = ProfitabilityPeriodPreset.Custom,
        FromDate = fromDate ?? new DateOnly(2026, 11, 2),
        ToDate = toDate ?? new DateOnly(2026, 11, 2)
    };

    private static ProfitabilityService CreateService(
        SalesDbContext context,
        MonetaryMode mode,
        IInventoryService? inventoryService = null,
        TimeZoneInfo? timeZone = null)
    {
        var profile = mode == MonetaryMode.Single
            ? new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "COP",
                PrimaryCurrencySymbol = "COL$",
                SecondaryCurrencyCode = null,
                SecondaryCurrencySymbol = null
            }
            : new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = "USD",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = "VES",
                SecondaryCurrencySymbol = "Bs.S"
            };

        var profileService = new Mock<IMonetaryProfileService>();
        profileService
            .Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        return new ProfitabilityService(
            context,
            new StubTimeZoneProvider(timeZone ?? FixedTimeZone),
            profileService.Object,
            inventoryService);
    }

    private static Mock<IInventoryService> CreateInventoryService(
        IReadOnlyList<int>? categoryProductIds = null,
        IReadOnlyDictionary<int, ProfitabilityProductInfoDto>? productInfo = null)
    {
        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(service => service.GetProductIdsByCategoryAsync(
                It.IsAny<IReadOnlyCollection<int>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<int>)(categoryProductIds ?? Array.Empty<int>()));
        inventory
            .Setup(service => service.GetProductCategoryInfoAsync(
                It.IsAny<IReadOnlyCollection<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, ProfitabilityProductInfoDto>)(
                productInfo ?? new Dictionary<int, ProfitabilityProductInfoDto>()));
        return inventory;
    }

    private static IReadOnlyDictionary<int, ProfitabilityProductInfoDto> ProductInfo(
        params (int ProductId, string ProductName, int? CategoryId, string? CategoryName)[] entries)
    {
        return entries.ToDictionary(
            entry => entry.ProductId,
            entry => new ProfitabilityProductInfoDto
            {
                ProductId = entry.ProductId,
                ProductName = entry.ProductName,
                CategoryId = entry.CategoryId,
                CategoryName = entry.CategoryName
            });
    }

    private static Sale CreateSale(
        int id,
        DateTime date,
        decimal totalUsd,
        decimal totalBsS,
        decimal rate,
        DateTime? cancelledAtUtc = null,
        int? cashierId = null,
        string priceListType = "Retail",
        decimal commissionUsd = 0m,
        decimal commissionLocal = 0m,
        SaleType saleType = SaleType.Merchandise,
        bool hasInvoice = true,
        params SaleItem[] items)
    {
        var sale = new Sale
        {
            Id = id,
            Date = date,
            Status = cancelledAtUtc.HasValue ? SaleStatus.Cancelled : SaleStatus.Completed,
            SaleType = saleType,
            InvoiceNumber = hasInvoice ? id : null,
            TotalUSD = totalUsd,
            TotalBsS = totalBsS,
            Subtotal = totalUsd,
            SubtotalBsS = totalBsS,
            AppliedRate = rate,
            CancelledAtUtc = cancelledAtUtc,
            CashierId = cashierId,
            PriceListType = priceListType,
            CommissionAmountUSD = commissionUsd,
            CommissionAmountLocal = commissionLocal
        };

        foreach (var item in items)
        {
            item.SaleId = id;
            sale.Items.Add(item);
        }

        return sale;
    }

    private static SaleItem CreateItem(
        int id,
        int productId,
        decimal quantity,
        decimal subtotalUsd,
        decimal subtotalBsS,
        decimal? unitCostUsd,
        decimal? unitCostLocal) => new()
    {
        Id = id,
        ProductId = productId,
        ProductName = $"Product {productId}",
        Quantity = quantity,
        UnitPrice = quantity == 0m ? 0m : subtotalUsd / quantity,
        UnitPriceBsS = quantity == 0m ? 0m : subtotalBsS / quantity,
        Subtotal = subtotalUsd,
        SubtotalBsS = subtotalBsS,
        UnitCostUSD = unitCostUsd,
        UnitCostLocal = unitCostLocal
    };

    private static SalePayment CreatePayment(
        int id,
        int saleId,
        DateTime createdAt,
        decimal amountUsd,
        decimal amountBsS,
        decimal rate,
        decimal feeUsd,
        decimal feeLocal,
        bool feeConfigured,
        int paymentMethodId = 3) => new()
    {
        Id = id,
        SaleId = saleId,
        PaymentMethodId = paymentMethodId,
        Amount = amountUsd,
        AmountBsS = amountBsS,
        ExchangeRate = rate,
        CreatedAt = createdAt,
        FeeAmountUsd = feeUsd,
        FeeAmountLocal = feeLocal,
        FeeRate = feeConfigured ? rate : 0m,
        FeeCurrencyCode = feeConfigured ? "VES" : null,
        FeeConfigured = feeConfigured
    };

    private static CreditReturn CreateReturn(
        int id,
        int saleId,
        DateTime createdAt,
        decimal returnedUsd,
        decimal? refundRate,
        params CreditReturnItem[] items)
    {
        var creditReturn = new CreditReturn
        {
            Id = id,
            SaleId = saleId,
            ReceivableId = 1,
            CustomerId = 1,
            CreatedAtUtc = createdAt,
            ReturnedUSD = returnedUsd,
            RefundRate = refundRate
        };

        foreach (var item in items)
        {
            item.CreditReturnId = id;
            creditReturn.Items.Add(item);
        }

        return creditReturn;
    }

    private static CreditReturnItem CreateReturnItem(
        int id,
        int saleItemId,
        decimal quantity,
        decimal amountUsd) => new()
    {
        Id = id,
        SaleItemId = saleItemId,
        Quantity = quantity,
        AmountUSD = amountUsd
    };

    private static TimeZoneInfo FindEasternTimeZone()
        => TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    private sealed class StubTimeZoneProvider : ITimeZoneProvider
    {
        private readonly TimeZoneInfo _timeZone;

        public StubTimeZoneProvider(TimeZoneInfo timeZone)
        {
            _timeZone = timeZone;
        }

        public Task<TimeZoneInfo> GetTimeZoneInfoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_timeZone);

        public Task<string> GetTimeZoneIdAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_timeZone.Id);

        public void Invalidate()
        {
        }
    }
}

