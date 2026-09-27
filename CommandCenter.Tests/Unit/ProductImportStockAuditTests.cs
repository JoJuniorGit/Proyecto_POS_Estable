using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ProductImportStockAuditTests
{
    private static InventoryDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    private static ProductImportDto BuildStockDto(string sku, string name, decimal stock) => new()
    {
        SKU = sku,
        Name = name,
        CostPriceUSD = 2.00m,
        ProfitMarginRetail = 50.00m,
        StockQuantity = stock,
        UnitOfMeasure = "Und",
        IsValid = true
    };

    private static Product BuildExistingProduct(string sku, string name, decimal stock) => new()
    {
        SKU = sku,
        Name = name,
        CostPriceUSD = 2.00m,
        ProfitMarginRetail = 50.00m,
        PriceRetailUSD = 3.00m,
        StockQuantity = stock,
        LowStockThreshold = 5m,
        UnitOfMeasure = UnitOfMeasureType.Und,
        IsActive = true
    };

    [Fact]
    public async Task BulkImport_NewProductWithStock_WritesSingleImportMovement()
    {
        using var db = CreateInMemoryDbContext();
        var service = new InventoryService(db);

        var (added, updated) = await service.BulkImportProductsAsync(
            new List<ProductImportDto> { BuildStockDto("AUD23-NEW-42", "Alta con Stock", 42m) },
            overwriteMerge: false);

        Assert.Equal(1, added);
        Assert.Equal(0, updated);

        var product = await db.Products.SingleAsync(p => p.SKU == "AUD23-NEW-42");
        var movement = Assert.Single(await db.StockMovements.ToListAsync());
        Assert.Equal(product.Id, movement.ProductId);
        Assert.Equal(42m, movement.QuantityChange);
        Assert.Equal(42m, movement.NewStockLevel);
        Assert.Null(movement.SaleId);
        Assert.Contains("Importación masiva", movement.Reason);
        Assert.Contains("IMP-", movement.Reason);
    }

    [Fact]
    public async Task BulkImport_ExistingProductWithStockIncrease_WritesMovementForTheDelta()
    {
        using var db = CreateInMemoryDbContext();
        db.Products.Add(BuildExistingProduct("AUD23-EXIST-50", "Existente con Stock", 50m));
        await db.SaveChangesAsync();

        var service = new InventoryService(db);
        var (added, updated) = await service.BulkImportProductsAsync(
            new List<ProductImportDto> { BuildStockDto("AUD23-EXIST-50", "Existente con Stock", 20m) },
            overwriteMerge: true);

        Assert.Equal(0, added);
        Assert.Equal(1, updated);

        var product = await db.Products.SingleAsync(p => p.SKU == "AUD23-EXIST-50");
        Assert.Equal(70m, product.StockQuantity);

        var movement = Assert.Single(await db.StockMovements.ToListAsync());
        Assert.Equal(product.Id, movement.ProductId);
        Assert.Equal(20m, movement.QuantityChange);
        Assert.Equal(70m, movement.NewStockLevel);
        Assert.Null(movement.SaleId);
        Assert.Contains("IMP-", movement.Reason);
    }

    [Fact]
    public async Task BulkImport_ExistingProductWithZeroImportStock_WritesNoMovement()
    {
        using var db = CreateInMemoryDbContext();
        db.Products.Add(BuildExistingProduct("AUD23-EXIST-00", "Existente sin Cambio", 50m));
        await db.SaveChangesAsync();

        var service = new InventoryService(db);
        var (added, updated) = await service.BulkImportProductsAsync(
            new List<ProductImportDto> { BuildStockDto("AUD23-EXIST-00", "Existente sin Cambio", 0m) },
            overwriteMerge: true);

        Assert.Equal(0, added);
        Assert.Equal(1, updated);

        var product = await db.Products.SingleAsync(p => p.SKU == "AUD23-EXIST-00");
        Assert.Equal(50m, product.StockQuantity);
        Assert.Empty(await db.StockMovements.ToListAsync());
    }

    [Fact]
    public async Task BulkImport_MixedBatch_SharesSingleImportIdentifierAcrossMovements()
    {
        using var db = CreateInMemoryDbContext();
        db.Products.Add(BuildExistingProduct("AUD23-MIX-EXIST", "Existente del Lote", 10m));
        await db.SaveChangesAsync();

        var service = new InventoryService(db);
        var (added, updated) = await service.BulkImportProductsAsync(new List<ProductImportDto>
        {
            BuildStockDto("AUD23-MIX-NEW", "Alta del Lote", 5m),
            BuildStockDto("AUD23-MIX-EXIST", "Existente del Lote", 7m)
        }, overwriteMerge: true);

        Assert.Equal(1, added);
        Assert.Equal(1, updated);

        var movements = await db.StockMovements.ToListAsync();
        Assert.Equal(2, movements.Count);
        var reason = Assert.Single(movements.Select(m => m.Reason).Distinct());
        Assert.Contains("Importación masiva", reason);
        Assert.Contains("IMP-", reason);
    }

    [Fact]
    public async Task BulkImport_NewGroupWithoutSharedStock_ForcesZeroAndWritesNoMovement()
    {
        using var db = CreateInMemoryDbContext();
        var service = new InventoryService(db);

        var groupDto = BuildStockDto("AUD23-GRP-0", "Grupo sin Stock Compartido", 50m);
        groupDto.ProductType = "Grupo";
        groupDto.GroupNameOrKey = "GRP-AUD23";
        groupDto.IsStockShared = false;

        var (added, _) = await service.BulkImportProductsAsync(new List<ProductImportDto> { groupDto }, overwriteMerge: false);

        Assert.Equal(1, added);
        var product = await db.Products.SingleAsync(p => p.SKU == "AUD23-GRP-0");
        Assert.True(product.IsGroupHeader);

        // Decisión AUD-21/AUD-23: el forzado a 0 por regla no emite movimiento (silencio intencional).
        Assert.Equal(0m, product.StockQuantity);
        Assert.Empty(await db.StockMovements.ToListAsync());
    }

    [Fact]
    public async Task BulkImport_WithActingUser_StampsUserIdOnMovement()
    {
        using var db = CreateInMemoryDbContext();
        var actingUser = new MockCurrentUserService { UserRole = UserRole.Admin, UserId = "importer-7" };
        var service = new InventoryService(db, actingUser);

        await service.BulkImportProductsAsync(
            new List<ProductImportDto> { BuildStockDto("AUD23-USER-01", "Producto con Usuario", 3m) },
            overwriteMerge: false);

        var movement = Assert.Single(await db.StockMovements.ToListAsync());
        Assert.Equal("importer-7", movement.UserId);
    }

    [Fact]
    public async Task BulkImport_WithOverwriteMerge_DuplicateSkuInSameBatch_MergesStockAndReferencesSameProduct()
    {
        // AUD-23 follow-up: en un lote con SKU duplicado, el segundo renglón actualiza un producto
        // creado en el mismo lote (Id aún sin asignar). Con FK explícito 0 el proveedor relacional
        // rechaza el INSERT; la referencia por navegación lo resuelve (SQLite reproduce el caso).
        var (db, connection) = Builders.TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using (connection)
        using (db)
        {
            var service = new InventoryService(db);
            var (added, updated) = await service.BulkImportProductsAsync(new List<ProductImportDto>
            {
                BuildStockDto("AUD23-DUP-SKU", "Duplicado en Lote", 10m),
                BuildStockDto("AUD23-DUP-SKU", "Duplicado en Lote", 5m)
            }, overwriteMerge: true);

            Assert.Equal(1, added);
            Assert.Equal(1, updated);

            var product = await db.Products.SingleAsync(p => p.SKU == "AUD23-DUP-SKU");
            Assert.Equal(15m, product.StockQuantity);

            var movements = await db.StockMovements.OrderBy(m => m.Id).ToListAsync();
            Assert.Equal(2, movements.Count);
            Assert.All(movements, m => Assert.Equal(product.Id, m.ProductId));
            Assert.Equal(10m, movements[0].QuantityChange);
            Assert.Equal(5m, movements[1].QuantityChange);
        }
    }
}
