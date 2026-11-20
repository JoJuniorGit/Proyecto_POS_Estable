using CommandCenter.Tests.Builders;
using Core.Entities;
using Inventory.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 15 (S1-S3): el snapshot de catalogo debe excluir productos no vendibles (borrados,
/// cabeceras de grupo y avances) y resolver la primera categoria sin cargar entidades completas.
/// </summary>
public class InventoryRotationCatalogQueryTests
{
    [Fact]
    public async Task Snapshot_ExcludesNonSaleableProductsAndResolvesFirstCategory()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using var _ = context;
        using var __ = connection;

        var categoryA = new Category { Id = 1, Name = "Abarrotes", NormalizedName = "ABARROTES" };
        var categoryB = new Category { Id = 2, Name = "Lacteos", NormalizedName = "LACTEOS" };
        context.Categories.AddRange(categoryA, categoryB);
        context.Products.AddRange(
            new Product
            {
                Id = 1,
                Name = "Arroz",
                SKU = "ARROZ-1",
                IsActive = true,
                StockQuantity = 10m,
                CostPriceUSD = 2m,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new Product
            {
                Id = 2,
                Name = "Grupo",
                SKU = "GRP-2",
                IsActive = true,
                IsGroupHeader = true
            },
            new Product
            {
                Id = 3,
                Name = "Avance",
                SKU = "ADV-3",
                IsActive = true,
                IsCashAdvance = true
            },
            new Product
            {
                Id = 4,
                Name = "Borrado",
                SKU = "DEL-4",
                IsActive = true,
                IsDeleted = true
            });
        context.ProductCategories.AddRange(
            new ProductCategory { Id = 1, ProductId = 1, CategoryId = 2 },
            new ProductCategory { Id = 2, ProductId = 1, CategoryId = 1 });
        await context.SaveChangesAsync();

        var service = new InventoryService(context);
        var snapshot = await service.GetInventoryRotationProductsAsync(null, null);

        var product = Assert.Single(snapshot);
        Assert.Equal(1, product.ProductId);
        Assert.Equal(1, product.CategoryId);
        Assert.Equal("Abarrotes", product.CategoryName);
        Assert.Equal(10m, product.StockQuantity);
        Assert.Equal(2m, product.CostPriceUsd);
    }

    [Fact]
    public async Task Snapshot_FiltersExplicitIdsAndSearch()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using var _ = context;
        using var __ = connection;

        context.Products.AddRange(
            new Product { Id = 1, Name = "Arroz", SKU = "ARROZ-1", IsActive = true },
            new Product { Id = 2, Name = "Queso", SKU = "QUESO-2", IsActive = true });
        await context.SaveChangesAsync();

        var service = new InventoryService(context);

        var byIds = await service.GetInventoryRotationProductsAsync(new[] { 2 }, null);
        Assert.Equal(2, Assert.Single(byIds).ProductId);

        var bySearch = await service.GetInventoryRotationProductsAsync(null, "ques");
        Assert.Equal(2, Assert.Single(bySearch).ProductId);

        var empty = await service.GetInventoryRotationProductsAsync(Array.Empty<int>(), null);
        Assert.Empty(empty);
    }
}
