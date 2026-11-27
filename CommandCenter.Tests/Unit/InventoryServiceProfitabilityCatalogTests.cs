using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 14 (F2-WU4, F2-S4): catálogo de apoyo para los filtros y desgloses de rentabilidad.
/// El subárbol de categorías se resuelve en memoria (BFS) y el mapa producto-categoría elige la
/// primera categoría por Id para mantener el reporte determinista.
/// </summary>
public class InventoryServiceProfitabilityCatalogTests
{
    [Fact]
    public async Task GetProductIdsByCategoryAsync_IncludeSubtree_ResolvesRootAndDescendants()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        SeedCatalog(context);
        var service = new InventoryService(context);

        var productIds = await service.GetProductIdsByCategoryAsync(new[] { 1 }, includeSubtree: true);

        Assert.Equal(new[] { 10, 11, 13 }, productIds.OrderBy(id => id));
    }

    [Fact]
    public async Task GetProductIdsByCategoryAsync_WithoutSubtree_OnlyDirectAssignments()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        SeedCatalog(context);
        var service = new InventoryService(context);

        var productIds = await service.GetProductIdsByCategoryAsync(new[] { 1 }, includeSubtree: false);

        Assert.Equal(new[] { 13 }, productIds);
    }

    [Fact]
    public async Task GetProductIdsByCategoryAsync_MultipleCategories_DeduplicatesProducts()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        SeedCatalog(context);
        var service = new InventoryService(context);

        var productIds = await service.GetProductIdsByCategoryAsync(new[] { 1, 2 }, includeSubtree: false);

        Assert.Equal(new[] { 10, 13 }, productIds.OrderBy(id => id));
    }

    [Fact]
    public async Task GetProductIdsByCategoryAsync_UnknownCategory_ReturnsEmpty()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        SeedCatalog(context);
        var service = new InventoryService(context);

        var productIds = await service.GetProductIdsByCategoryAsync(new[] { 99 }, includeSubtree: true);

        Assert.Empty(productIds);
    }

    [Fact]
    public async Task GetProductCategoryInfoAsync_WithoutCategory_ReturnsNullCategory()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        SeedCatalog(context);
        var service = new InventoryService(context);

        var info = await service.GetProductCategoryInfoAsync(new[] { 14 });

        Assert.Single(info);
        Assert.Equal("Sin categoría", info[14].ProductName);
        Assert.Null(info[14].CategoryId);
        Assert.Null(info[14].CategoryName);
    }

    [Fact]
    public async Task GetProductCategoryInfoAsync_MultipleCategories_ChoosesFirstByCategoryId()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        SeedCatalog(context);
        var service = new InventoryService(context);

        var info = await service.GetProductCategoryInfoAsync(new[] { 10, 11, 14 });

        Assert.Equal("Leche", info[10].ProductName);
        // Product 10 belongs to categories 2 and 4: the report must choose the lowest Id.
        Assert.Equal(2, info[10].CategoryId);
        Assert.Equal("Lácteos", info[10].CategoryName);

        Assert.Equal(3, info[11].CategoryId);
        Assert.Equal("Descremados", info[11].CategoryName);

        Assert.Null(info[14].CategoryId);
        Assert.Null(info[14].CategoryName);
    }

    private static void SeedCatalog(InventoryDbContext context)
    {
        context.Categories.AddRange(
            new Category { Id = 1, Name = "Alimentos", NormalizedName = "ALIMENTOS" },
            new Category { Id = 2, Name = "Lácteos", NormalizedName = "LÁCTEOS", ParentCategoryId = 1 },
            new Category { Id = 3, Name = "Descremados", NormalizedName = "DESCREMADOS", ParentCategoryId = 2 },
            new Category { Id = 4, Name = "Bebidas", NormalizedName = "BEBIDAS" },
            new Category { Id = 5, Name = "Snacks", NormalizedName = "SNACKS" });

        context.Products.AddRange(
            new Product { Id = 10, Name = "Leche", SKU = "P-10" },
            new Product { Id = 11, Name = "Yogur", SKU = "P-11" },
            new Product { Id = 12, Name = "Refresco", SKU = "P-12" },
            new Product { Id = 13, Name = "Arroz", SKU = "P-13" },
            new Product { Id = 14, Name = "Sin categoría", SKU = "P-14" },
            new Product { Id = 15, Name = "Papas", SKU = "P-15" });

        context.ProductCategories.AddRange(
            new ProductCategory { ProductId = 10, CategoryId = 2 },
            new ProductCategory { ProductId = 10, CategoryId = 4 },
            new ProductCategory { ProductId = 11, CategoryId = 3 },
            new ProductCategory { ProductId = 12, CategoryId = 4 },
            new ProductCategory { ProductId = 13, CategoryId = 1 },
            new ProductCategory { ProductId = 15, CategoryId = 5 });

        context.SaveChanges();
    }
}
