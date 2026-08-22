using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Core.Events;
using Inventory.Module.Data;
using Inventory.Module.EventHandlers;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests;

public class PerformanceAndOptimizationSprint3Tests
{
    private InventoryDbContext GetInMemoryInventoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    private SalesDbContext GetInMemorySalesDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new SalesDbContext(options);
    }

    [Fact]
    public async Task GetProductsByIdsAsync_ReturnsOnlyRequestedProductsInBatch()
    {
        using var db = GetInMemoryInventoryDbContext();
        var service = new InventoryService(db);

        db.Products.AddRange(
            new Product { Id = 1, SKU = "P01", Name = "Producto 1", PriceRetailUSD = 10, CostPriceUSD = 5, StockQuantity = 10 },
            new Product { Id = 2, SKU = "P02", Name = "Producto 2", PriceRetailUSD = 20, CostPriceUSD = 10, StockQuantity = 20 },
            new Product { Id = 3, SKU = "P03", Name = "Producto 3", PriceRetailUSD = 30, CostPriceUSD = 15, StockQuantity = 30 }
        );
        await db.SaveChangesAsync();

        var results = await service.GetProductsByIdsAsync(new[] { 1, 3 });

        Assert.Equal(2, results.Count);
        Assert.Contains(results, p => p.Id == 1);
        Assert.Contains(results, p => p.Id == 3);
        Assert.DoesNotContain(results, p => p.Id == 2);
    }

    [Fact]
    public async Task GetCashAdvanceProductAsync_ReturnsCashAdvanceProductDirectly()
    {
        using var db = GetInMemoryInventoryDbContext();
        var service = new InventoryService(db);

        db.Products.AddRange(
            new Product { Id = 1, SKU = "P01", Name = "Normal", PriceRetailUSD = 10, IsCashAdvance = false, IsActive = true },
            new Product { Id = 2, SKU = "ADV-001", Name = "Adelanto Efectivo", PriceRetailUSD = 0, IsCashAdvance = true, IsActive = true }
        );
        await db.SaveChangesAsync();

        var advProduct = await service.GetCashAdvanceProductAsync();

        Assert.NotNull(advProduct);
        Assert.Equal(2, advProduct.Id);
        Assert.True(advProduct.IsCashAdvance);
    }

    [Fact]
    public async Task InventorySaleMadeEventHandler_IsIdempotent_DoesNotDoubleDeductStock()
    {
        using var db = GetInMemoryInventoryDbContext();
        var service = new InventoryService(db);
        var handler = new InventorySaleMadeEventHandler(service, db);

        var product = new Product
        {
            Id = 5,
            SKU = "BEV-001",
            Name = "Refresco 2L",
            PriceRetailUSD = 2.50m,
            CostPriceUSD = 1.50m,
            StockQuantity = 20.000m
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var saleEvent = new SaleMadeEvent(
            SaleId: 999,
            SaleDate: DateTime.UtcNow,
            Items: new List<SaleItemSnapshot>
            {
                new SaleItemSnapshot(ProductId: 5, Quantity: 3.000m)
            }
        );

        // First execution: deducts 3 items (20 -> 17)
        await handler.Handle(saleEvent, CancellationToken.None);

        var refreshed = await service.GetProductByIdAsync(5);
        Assert.Equal(17.000m, refreshed!.StockQuantity);

        // Second execution with same SaleId: must be skipped by idempotency check!
        await handler.Handle(saleEvent, CancellationToken.None);

        refreshed = await service.GetProductByIdAsync(5);
        Assert.Equal(17.000m, refreshed!.StockQuantity); // Still 17, not 14!
    }

    [Fact]
    public void DbContexts_ModelConfigurations_HaveRequiredIndices()
    {
        using var salesDb = GetInMemorySalesDbContext();
        var saleEntity = salesDb.Model.FindEntityType(typeof(Sale));
        Assert.NotNull(saleEntity);

        var indexes = saleEntity.GetIndexes().ToList();
        // Check for Status + Date index
        Assert.Contains(indexes, idx => idx.Properties.Any(p => p.Name == "Status") && idx.Properties.Any(p => p.Name == "Date"));
        // Check for CustomerId index
        Assert.Contains(indexes, idx => idx.Properties.Any(p => p.Name == "CustomerId"));
    }
}
