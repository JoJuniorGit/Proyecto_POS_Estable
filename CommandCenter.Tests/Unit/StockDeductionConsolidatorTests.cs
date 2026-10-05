using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.149-T1 (SRE-01): deterministic lock order + consolidation for stock deduction batches.
/// </summary>
public class StockDeductionConsolidatorTests
{
    private static StockDeductionProductInfo ProductInfo(
        int id,
        string sku = "SKU",
        string? name = null,
        bool isCashAdvance = false,
        decimal conversionFactor = 0m,
        int? parentId = null,
        bool parentIsStockShared = false,
        string? parentName = null,
        string? parentSku = null)
        => new()
        {
            Id = id,
            Name = name ?? $"Producto {id}",
            SKU = sku,
            IsCashAdvance = isCashAdvance,
            ConversionFactor = conversionFactor,
            ParentId = parentId,
            ParentIsStockShared = parentIsStockShared,
            ParentName = parentName,
            ParentSKU = parentSku
        };

    private static InventoryDbContext CreateInMemoryInventoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    private static InventoryService CreateService(InventoryDbContext context)
    {
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("USER-TEST");
        return new InventoryService(context, mockUser.Object);
    }

    [Fact]
    public void ResolveAndConsolidate_ReversedInputOrder_ProducesSameOrderedTargetsAndTotals()
    {
        // Arrange: same batch in opposite orders (target 1 shows duplicates too).
        var products = new Dictionary<int, StockDeductionProductInfo>
        {
            [1] = ProductInfo(1),
            [2] = ProductInfo(2),
            [3] = ProductInfo(3)
        };

        var forward = new[]
        {
            new StockDeductionRequest(3, -2m, "Venta", 77),
            new StockDeductionRequest(1, -5m, "Venta", 77),
            new StockDeductionRequest(2, -10m, "Venta", 77),
            new StockDeductionRequest(1, -1m, "Venta", 77)
        };
        var reversed = forward.Reverse().ToArray();

        // Act
        var planForward = StockDeductionConsolidator.ResolveAndConsolidate(forward, products);
        var planReversed = StockDeductionConsolidator.ResolveAndConsolidate(reversed, products);

        // Assert: identical ordered target sequence and identical summed quantities per target.
        Assert.Equal(new[] { 1, 2, 3 }, planForward.Deductions.Select(d => d.TargetProductId));
        Assert.Equal(
            planForward.Deductions.Select(d => (d.TargetProductId, d.QuantityChange)),
            planReversed.Deductions.Select(d => (d.TargetProductId, d.QuantityChange)));
        Assert.Equal(new[] { -6m, -10m, -2m }, planForward.Deductions.Select(d => d.QuantityChange));
    }

    [Fact]
    public void ResolveAndConsolidate_DuplicateTargetReasonSale_ConsolidatesIntoOneWithSummedQuantity()
    {
        var products = new Dictionary<int, StockDeductionProductInfo> { [7] = ProductInfo(7) };
        var requests = new[]
        {
            new StockDeductionRequest(7, -3m, "Sale #100", 100),
            new StockDeductionRequest(7, -4m, "Sale #100", 100)
        };

        var plan = StockDeductionConsolidator.ResolveAndConsolidate(requests, products);

        var deduction = Assert.Single(plan.Deductions);
        Assert.Equal(7, deduction.TargetProductId);
        Assert.Equal(-7m, deduction.QuantityChange);
        Assert.Equal("Sale #100", deduction.Reason);
        Assert.Equal(100, deduction.SaleId);
    }

    [Fact]
    public void ResolveAndConsolidate_NRequestsAcrossTargets_PreservesPerTargetTotals_IncludingSharedStockConversion()
    {
        // id 10: shared-stock parent; id 20: variant of 10 (factor 0.5); id 30: standalone.
        var products = new Dictionary<int, StockDeductionProductInfo>
        {
            [10] = ProductInfo(10, sku: "GRP-10", name: "Café Pool"),
            [20] = ProductInfo(20, sku: "VAR-20", name: "Café Fino", parentId: 10, parentIsStockShared: true, parentName: "Café Pool", parentSku: "GRP-10", conversionFactor: 0.5m),
            [30] = ProductInfo(30, sku: "SKU-30")
        };

        var requests = new[]
        {
            new StockDeductionRequest(20, -4m, "Sale #200", 200), // resolves to parent 10, -4 * 0.5 = -2
            new StockDeductionRequest(10, -3m, "Sale #200", 200),
            new StockDeductionRequest(30, -1m, "Sale #200", 200),
            new StockDeductionRequest(10, -2m, "Sale #200", 200)  // consolidates with the -3 group -> -5
        };

        var plan = StockDeductionConsolidator.ResolveAndConsolidate(requests, products);

        // Per-target delta equals the sum of resolved request deltas.
        Assert.Equal(-7m, plan.Deductions.Where(d => d.TargetProductId == 10).Sum(d => d.QuantityChange));
        Assert.Equal(-1m, plan.Deductions.Where(d => d.TargetProductId == 30).Sum(d => d.QuantityChange));
        Assert.DoesNotContain(plan.Deductions, d => d.TargetProductId == 20);

        // Variant conversion is applied exactly as the legacy method did, preserving the reason format.
        var variantDeduction = Assert.Single(plan.Deductions, d => d.TargetProductId == 10 && d.QuantityChange == -2m);
        string factorText = 0.5m.ToString("G29", CultureInfo.CurrentCulture);
        Assert.Equal($"Variante: Café Fino (SKU: VAR-20, Factor: {factorText}) | Sale #200", variantDeduction.Reason);

        // Movements/updates are ordered ascending by target product id.
        Assert.Equal(new[] { 10, 10, 30 }, plan.Deductions.Select(d => d.TargetProductId));

        // Cache invalidation still covers the variant and the parent SKUs.
        Assert.Contains("VAR-20", plan.SkusToInvalidate);
        Assert.Contains("GRP-10", plan.SkusToInvalidate);
        Assert.Contains("SKU-30", plan.SkusToInvalidate);
    }

    [Fact]
    public void ResolveAndConsolidate_ZeroQuantityRequest_IsIgnoredWithoutProductResolution()
    {
        var products = new Dictionary<int, StockDeductionProductInfo> { [1] = ProductInfo(1) };

        // Unknown product id with zero change must stay a no-op (legacy filtered these first).
        var plan = StockDeductionConsolidator.ResolveAndConsolidate(
            new[] { new StockDeductionRequest(999, 0m, "Noop") },
            products);

        Assert.Empty(plan.Deductions);
    }

    [Fact]
    public void ResolveAndConsolidate_NetZeroGroup_ProducesNoDeduction()
    {
        var products = new Dictionary<int, StockDeductionProductInfo> { [1] = ProductInfo(1) };
        var requests = new[]
        {
            new StockDeductionRequest(1, -5m, "Adjust", null),
            new StockDeductionRequest(1, 5m, "Adjust", null)
        };

        var plan = StockDeductionConsolidator.ResolveAndConsolidate(requests, products);

        // A group that nets to zero applies no stock delta, so it emits no update and no movement.
        Assert.Empty(plan.Deductions);
    }

    [Fact]
    public void ResolveAndConsolidate_UnknownProduct_ThrowsLegacyKeyNotFoundException()
    {
        var ex = Assert.Throws<KeyNotFoundException>(() =>
            StockDeductionConsolidator.ResolveAndConsolidate(
                new[] { new StockDeductionRequest(42, -1m, "Sale") },
                new Dictionary<int, StockDeductionProductInfo>()));

        Assert.Equal("Producto 42 no encontrado", ex.Message);
    }

    [Fact]
    public void ResolveAndConsolidate_CashAdvanceProduct_ThrowsLegacyInvalidOperationException()
    {
        var products = new Dictionary<int, StockDeductionProductInfo>
        {
            [5] = ProductInfo(5, name: "Adelanto Efectivo", isCashAdvance: true)
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            StockDeductionConsolidator.ResolveAndConsolidate(
                new[] { new StockDeductionRequest(5, -1m, "Sale") },
                products));

        Assert.Equal(
            "El producto 'Adelanto Efectivo' es un servicio de adelanto de efectivo y no maneja inventario físico.",
            ex.Message);
    }

    [Fact]
    public async Task UpdateStockBatchAsync_DuplicateTargetsSameReasonAndSale_ProducesSingleMovementWithSummedQuantity()
    {
        using var context = CreateInMemoryInventoryDbContext();
        context.Products.Add(new Product { Id = 1, Name = "Producto A", SKU = "SKU-A", StockQuantity = 50m, IsActive = true });
        context.Products.Add(new Product { Id = 2, Name = "Producto B", SKU = "SKU-B", StockQuantity = 20m, IsActive = true });
        await context.SaveChangesAsync();

        var service = CreateService(context);

        await service.UpdateStockBatchAsync(new List<StockDeductionRequest>
        {
            new(2, -3m, "Sale #500", 500),
            new(1, -5m, "Sale #500", 500),
            new(1, -2m, "Sale #500", 500),
            new(2, -7m, "Sale #500", 500)
        }, userId: "USER-TEST");

        var updatedP1 = await context.Products.FindAsync(1);
        var updatedP2 = await context.Products.FindAsync(2);
        Assert.NotNull(updatedP1);
        Assert.NotNull(updatedP2);
        Assert.Equal(43m, updatedP1.StockQuantity);
        Assert.Equal(10m, updatedP2.StockQuantity);

        // One movement per consolidated (target, reason, sale) group with the summed quantity.
        var movements = await context.StockMovements.ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Contains(movements, m => m.ProductId == 1 && m.QuantityChange == -7m && m.NewStockLevel == 43m && m.SaleId == 500);
        Assert.Contains(movements, m => m.ProductId == 2 && m.QuantityChange == -10m && m.NewStockLevel == 10m && m.SaleId == 500);
    }

    [Fact]
    public async Task UpdateStockBatchAsync_SharedStockVariant_ConsolidatesConvertedQuantitiesOnParent()
    {
        using var context = CreateInMemoryInventoryDbContext();
        var parent = new Product
        {
            Id = 10,
            Name = "Café Pool",
            SKU = "GRP-10",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 40m,
            IsActive = true
        };
        context.Products.Add(parent);
        context.Products.Add(new Product
        {
            Id = 20,
            Name = "Café Fino",
            SKU = "VAR-20",
            ParentProductId = 10,
            ParentProduct = parent,
            ConversionFactor = 0.5m,
            StockQuantity = 0m,
            IsActive = true
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);

        await service.UpdateStockBatchAsync(new List<StockDeductionRequest>
        {
            new(20, -4m, "Sale #600", 600),
            new(20, -2m, "Sale #600", 600)
        });

        var updatedParent = await context.Products.FindAsync(10);
        Assert.NotNull(updatedParent);
        Assert.Equal(37m, updatedParent.StockQuantity); // 40 - (4 + 2) * 0.5

        var movement = Assert.Single(await context.StockMovements.ToListAsync());
        Assert.Equal(10, movement.ProductId);
        Assert.Equal(-3m, movement.QuantityChange);
        Assert.Equal(37m, movement.NewStockLevel);
        Assert.Equal(600, movement.SaleId);
        Assert.Contains("Variante: Café Fino", movement.Reason);
    }
}
