using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.179-T1 (S1-S5): cobertura del backend de categorías jerárquicas — CRUD validado,
// asignación N:M producto-categoría, filtro por subárbol, import/export con categorías.
public class CategoryServiceTests
{
    private static InventoryDbContext CreateDb() => TestDatabaseFactory.CreateInventoryDbContext();

    private static async Task<CategoryDto> CreateCategoryAsync(CategoryService service, string name, int? parentId = null)
        => await service.CreateCategoryAsync(new CreateCategoryRequestDto { Name = name, ParentCategoryId = parentId });

    private static async Task<ProductDto> CreateProductAsync(InventoryService service, string sku, string name, IReadOnlyList<int>? categoryIds = null, bool isActive = true)
        => await service.CreateProductFromDtoAsync(new CreateProductDto
        {
            Name = name,
            SKU = sku,
            PriceRetailUSD = 2.50m,
            PriceUSD = 2.50m,
            CostPriceUSD = 1.50m,
            ProfitMarginRetail = 66.67m,
            StockQuantity = 0m,
            IsActive = isActive,
            CategoryIds = categoryIds
        });

    // ---------------------------------------------------------------- CRUD

    [Fact]
    public async Task CreateCategory_RootAndDescendants_ComputesPathAndDepth()
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        var root = await CreateCategoryAsync(service, "Alimentos");
        var child = await CreateCategoryAsync(service, "Lácteos", root.Id);
        var grandchild = await CreateCategoryAsync(service, "Quesos", child.Id);

        Assert.Equal("Alimentos", root.Path);
        Assert.Equal(0, root.Depth);
        Assert.Null(root.ParentCategoryId);

        Assert.Equal("Alimentos > Lácteos", child.Path);
        Assert.Equal(1, child.Depth);
        Assert.Equal(root.Id, child.ParentCategoryId);

        Assert.Equal("Alimentos > Lácteos > Quesos", grandchild.Path);
        Assert.Equal(2, grandchild.Depth);

        var all = await service.GetCategoriesAsync(CancellationToken.None);
        Assert.Equal(3, all.Count);
        Assert.Equal(new[] { "Alimentos", "Alimentos > Lácteos", "Alimentos > Lácteos > Quesos" }, all.Select(c => c.Path));
    }

    [Fact]
    public async Task CreateCategory_TrimsAndNormalizesName()
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        var created = await CreateCategoryAsync(service, "  Bebidas  ");

        Assert.Equal("Bebidas", created.Name);
        var entity = await db.Categories.SingleAsync();
        Assert.Equal("BEBIDAS", entity.NormalizedName);
    }

    [Fact]
    public async Task CreateCategory_DuplicateNameUnderSameParent_Throws()
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        await CreateCategoryAsync(service, "Bebidas");

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateCategoryAsync(service, "bebidas"));
    }

    [Fact]
    public async Task CreateCategory_DuplicateRootNameCaseInsensitive_Throws()
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        await CreateCategoryAsync(service, "Alimentos");

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateCategoryAsync(service, " ALIMENTOS "));
    }

    [Fact]
    public async Task CreateCategory_SameNameUnderDifferentParents_IsAllowed()
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        var a = await CreateCategoryAsync(service, "Bebidas");
        var b = await CreateCategoryAsync(service, "Alimentos");

        var first = await CreateCategoryAsync(service, "Jugos", a.Id);
        var second = await CreateCategoryAsync(service, "Jugos", b.Id);

        Assert.Equal("Bebidas > Jugos", first.Path);
        Assert.Equal("Alimentos > Jugos", second.Path);
    }

    [Fact]
    public async Task CreateCategory_MissingParent_ThrowsKeyNotFound()
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => CreateCategoryAsync(service, "Jugos", parentId: 999));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateCategory_EmptyName_ThrowsArgumentException(string name)
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => CreateCategoryAsync(service, name));
    }

    [Fact]
    public async Task CreateCategory_NameLongerThan60_ThrowsArgumentException()
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => CreateCategoryAsync(service, new string('X', 61)));
    }

    [Fact]
    public async Task UpdateCategory_Rename_ReflectsNewSegmentInDescendantPaths()
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        var root = await CreateCategoryAsync(service, "Alimentos");
        var child = await CreateCategoryAsync(service, "Lácteos", root.Id);
        await CreateCategoryAsync(service, "Quesos", child.Id);

        var updated = await service.UpdateCategoryAsync(
            child.Id,
            new UpdateCategoryRequestDto { Name = "Lácteos y Derivados", ParentCategoryId = root.Id },
            CancellationToken.None);

        Assert.Equal("Alimentos > Lácteos y Derivados", updated.Path);

        var all = await service.GetCategoriesAsync(CancellationToken.None);
        Assert.Equal("Alimentos > Lácteos y Derivados > Quesos", all.Single(c => c.Name == "Quesos").Path);
    }

    [Fact]
    public async Task UpdateCategory_ValidMove_ChangesParentAndPath()
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        var a = await CreateCategoryAsync(service, "Alimentos");
        var b = await CreateCategoryAsync(service, "Bebidas");
        var child = await CreateCategoryAsync(service, "Jugos", a.Id);

        var moved = await service.UpdateCategoryAsync(
            child.Id,
            new UpdateCategoryRequestDto { Name = "Jugos", ParentCategoryId = b.Id },
            CancellationToken.None);

        Assert.Equal(b.Id, moved.ParentCategoryId);
        Assert.Equal("Bebidas > Jugos", moved.Path);
        Assert.Equal(1, moved.Depth);
    }

    [Fact]
    public async Task UpdateCategory_MoveUnderOwnDescendant_Throws()
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        var a = await CreateCategoryAsync(service, "A");
        var b = await CreateCategoryAsync(service, "B", a.Id);
        var c = await CreateCategoryAsync(service, "C", b.Id);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateCategoryAsync(
            a.Id,
            new UpdateCategoryRequestDto { Name = "A", ParentCategoryId = c.Id },
            CancellationToken.None));

        Assert.Contains("mover", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateCategory_SetParentToSelf_Throws()
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        var a = await CreateCategoryAsync(service, "A");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateCategoryAsync(
            a.Id,
            new UpdateCategoryRequestDto { Name = "A", ParentCategoryId = a.Id },
            CancellationToken.None));
    }

    [Fact]
    public async Task UpdateCategory_SiblingDuplicateName_Throws()
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        var root = await CreateCategoryAsync(service, "Alimentos");
        await CreateCategoryAsync(service, "Lácteos", root.Id);
        var other = await CreateCategoryAsync(service, "Bebidas", root.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateCategoryAsync(
            other.Id,
            new UpdateCategoryRequestDto { Name = "lácteos", ParentCategoryId = root.Id },
            CancellationToken.None));
    }

    [Fact]
    public async Task UpdateCategory_MissingCategory_ThrowsKeyNotFound()
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.UpdateCategoryAsync(
            404,
            new UpdateCategoryRequestDto { Name = "Fantasma" },
            CancellationToken.None));
    }

    [Fact]
    public async Task DeleteCategory_WithChildren_Throws()
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        var root = await CreateCategoryAsync(service, "Alimentos");
        await CreateCategoryAsync(service, "Lácteos", root.Id);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteCategoryAsync(root.Id, CancellationToken.None));
        Assert.Contains("subcategorías", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteCategory_WithProductsAssigned_Throws()
    {
        using var db = CreateDb();
        var categories = new CategoryService(db);
        var inventory = new InventoryService(db);

        var leaf = await CreateCategoryAsync(categories, "Bebidas");
        await CreateProductAsync(inventory, "DEL-1", "Refresco", new[] { leaf.Id });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => categories.DeleteCategoryAsync(leaf.Id, CancellationToken.None));
        Assert.Contains("productos asignados", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteCategory_UnusedLeaf_RemovesIt()
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        var leaf = await CreateCategoryAsync(service, "Temporal");

        await service.DeleteCategoryAsync(leaf.Id, CancellationToken.None);

        Assert.False(await db.Categories.AnyAsync(c => c.Id == leaf.Id));
    }

    [Fact]
    public async Task DeleteCategory_Missing_ThrowsKeyNotFound()
    {
        using var db = CreateDb();
        var service = new CategoryService(db);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteCategoryAsync(404, CancellationToken.None));
    }

    // -------------------------------------------------- Product assignment

    [Fact]
    public async Task CreateProduct_WithCategoryIds_AssignsAndExposesCategories()
    {
        using var db = CreateDb();
        var categories = new CategoryService(db);
        var inventory = new InventoryService(db);

        var a = await CreateCategoryAsync(categories, "Alimentos");
        var b = await CreateCategoryAsync(categories, "Bebidas");

        var created = await CreateProductAsync(inventory, "CAT-1", "Producto Categorizado", new[] { a.Id, b.Id });

        Assert.Equal(2, created.Categories.Count);
        Assert.Contains(created.Categories, c => c.Id == a.Id && c.Name == "Alimentos");
        Assert.Contains(created.Categories, c => c.Id == b.Id && c.Name == "Bebidas");

        var reloaded = await inventory.GetProductDtoByIdAsync(created.Id, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded!.Categories.Count);
    }

    [Fact]
    public async Task CreateProduct_WithInvalidCategoryId_Throws()
    {
        using var db = CreateDb();
        var inventory = new InventoryService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateProductAsync(inventory, "CAT-2", "Producto Inválido", new[] { 999 }));
    }

    [Fact]
    public async Task UpdateProduct_NullCategoryIds_KeepsExistingAssignments()
    {
        using var db = CreateDb();
        var categories = new CategoryService(db);
        var inventory = new InventoryService(db);

        var a = await CreateCategoryAsync(categories, "Alimentos");
        var created = await CreateProductAsync(inventory, "CAT-3", "Producto", new[] { a.Id });

        await inventory.UpdateProductFromDtoAsync(created.Id, new UpdateProductDto
        {
            Id = created.Id,
            Name = "Producto Renombrado",
            PriceRetailUSD = 2.50m,
            PriceUSD = 2.50m,
            CostPriceUSD = 1.50m,
            ProfitMarginRetail = 66.67m,
            CategoryIds = null
        }, CancellationToken.None);

        var reloaded = await inventory.GetProductDtoByIdAsync(created.Id, CancellationToken.None);
        Assert.Single(reloaded!.Categories);
        Assert.Equal(a.Id, reloaded.Categories[0].Id);
    }

    [Fact]
    public async Task UpdateProduct_EmptyCategoryIds_RemovesAllAssignments()
    {
        using var db = CreateDb();
        var categories = new CategoryService(db);
        var inventory = new InventoryService(db);

        var a = await CreateCategoryAsync(categories, "Alimentos");
        var created = await CreateProductAsync(inventory, "CAT-4", "Producto", new[] { a.Id });

        await inventory.UpdateProductFromDtoAsync(created.Id, new UpdateProductDto
        {
            Id = created.Id,
            Name = "Producto",
            PriceRetailUSD = 2.50m,
            PriceUSD = 2.50m,
            CostPriceUSD = 1.50m,
            ProfitMarginRetail = 66.67m,
            CategoryIds = Array.Empty<int>()
        }, CancellationToken.None);

        var reloaded = await inventory.GetProductDtoByIdAsync(created.Id, CancellationToken.None);
        Assert.Empty(reloaded!.Categories);
        Assert.False(await db.ProductCategories.AnyAsync(pc => pc.ProductId == created.Id));
    }

    [Fact]
    public async Task UpdateProduct_ProvidedList_ReplacesAssignments()
    {
        using var db = CreateDb();
        var categories = new CategoryService(db);
        var inventory = new InventoryService(db);

        var a = await CreateCategoryAsync(categories, "Alimentos");
        var b = await CreateCategoryAsync(categories, "Bebidas");
        var c = await CreateCategoryAsync(categories, "Limpieza");
        var created = await CreateProductAsync(inventory, "CAT-5", "Producto", new[] { a.Id });

        await inventory.UpdateProductFromDtoAsync(created.Id, new UpdateProductDto
        {
            Id = created.Id,
            Name = "Producto",
            PriceRetailUSD = 2.50m,
            PriceUSD = 2.50m,
            CostPriceUSD = 1.50m,
            ProfitMarginRetail = 66.67m,
            CategoryIds = new[] { b.Id, c.Id }
        }, CancellationToken.None);

        var reloaded = await inventory.GetProductDtoByIdAsync(created.Id, CancellationToken.None);
        Assert.Equal(2, reloaded!.Categories.Count);
        Assert.Contains(reloaded.Categories, x => x.Id == b.Id);
        Assert.Contains(reloaded.Categories, x => x.Id == c.Id);
        Assert.DoesNotContain(reloaded.Categories, x => x.Id == a.Id);
    }

    [Fact]
    public async Task UpdateProduct_InvalidCategoryId_ThrowsAndKeepsProductUntouched()
    {
        using var db = CreateDb();
        var categories = new CategoryService(db);
        var inventory = new InventoryService(db);

        var a = await CreateCategoryAsync(categories, "Alimentos");
        var created = await CreateProductAsync(inventory, "CAT-6", "Producto Original", new[] { a.Id });

        await Assert.ThrowsAsync<ArgumentException>(() => inventory.UpdateProductFromDtoAsync(created.Id, new UpdateProductDto
        {
            Id = created.Id,
            Name = "Producto Renombrado",
            PriceRetailUSD = 2.50m,
            PriceUSD = 2.50m,
            CostPriceUSD = 1.50m,
            ProfitMarginRetail = 66.67m,
            CategoryIds = new[] { 999 }
        }, CancellationToken.None));

        var persisted = await db.Products.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal("Producto Original", persisted.Name);
    }

    // -------------------------------------------------- Subtree filtering

    [Fact]
    public async Task GetProductsPaged_FilteredByAncestor_IncludesDescendantProducts()
    {
        using var db = CreateDb();
        var categories = new CategoryService(db);
        var inventory = new InventoryService(db);

        var root = await CreateCategoryAsync(categories, "Alimentos");
        var child = await CreateCategoryAsync(categories, "Lácteos", root.Id);
        var grandchild = await CreateCategoryAsync(categories, "Quesos", child.Id);
        var otherRoot = await CreateCategoryAsync(categories, "Limpieza");

        await CreateProductAsync(inventory, "FIL-1", "Queso Paisa", new[] { grandchild.Id });
        await CreateProductAsync(inventory, "FIL-2", "Detergente", new[] { otherRoot.Id });

        var byRoot = await inventory.GetProductsPagedAsync(null, 1, 10, categoryId: root.Id);
        Assert.Equal(new[] { "FIL-1" }, byRoot.Items.Select(p => p.SKU));

        var byChild = await inventory.GetProductsPagedAsync(null, 1, 10, categoryId: child.Id);
        Assert.Equal(new[] { "FIL-1" }, byChild.Items.Select(p => p.SKU));

        var byOtherBranch = await inventory.GetProductsPagedAsync(null, 1, 10, categoryId: otherRoot.Id);
        Assert.Equal(new[] { "FIL-2" }, byOtherBranch.Items.Select(p => p.SKU));
    }

    [Fact]
    public async Task GetProductsPaged_CategoryFilter_CombinesWithSearchAndStatus()
    {
        using var db = CreateDb();
        var categories = new CategoryService(db);
        var inventory = new InventoryService(db);

        var root = await CreateCategoryAsync(categories, "Alimentos");
        var child = await CreateCategoryAsync(categories, "Lácteos", root.Id);
        var grandchild = await CreateCategoryAsync(categories, "Quesos", child.Id);

        await CreateProductAsync(inventory, "FIL-3", "Queso Fresco", new[] { grandchild.Id }, isActive: true);
        await CreateProductAsync(inventory, "FIL-4", "Queso Añejo", new[] { grandchild.Id }, isActive: false);
        await CreateProductAsync(inventory, "FIL-5", "Jamón", new[] { grandchild.Id }, isActive: true);

        var result = await inventory.GetProductsPagedAsync("queso", 1, 10, statusFilter: "active", categoryId: root.Id);

        Assert.Equal(new[] { "FIL-3" }, result.Items.Select(p => p.SKU));
    }

    [Fact]
    public async Task GetProductsPaged_UnknownCategory_FiltersEverythingOut()
    {
        using var db = CreateDb();
        var inventory = new InventoryService(db);
        await CreateProductAsync(inventory, "FIL-6", "Producto Suelto");

        var result = await inventory.GetProductsPagedAsync(null, 1, 10, categoryId: 12345);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetSuggestions_WithCategoryId_FiltersBySubtree()
    {
        using var db = CreateDb();
        var categories = new CategoryService(db);
        var inventory = new InventoryService(db);

        var root = await CreateCategoryAsync(categories, "Alimentos");
        var child = await CreateCategoryAsync(categories, "Lácteos", root.Id);
        var grandchild = await CreateCategoryAsync(categories, "Quesos", child.Id);
        var other = await CreateCategoryAsync(categories, "Bebidas");

        await CreateProductAsync(inventory, "SUG-1", "Queso Paisa", new[] { grandchild.Id });
        await CreateProductAsync(inventory, "SUG-2", "Queso Bebida", new[] { other.Id });

        var filtered = await inventory.GetSuggestionsAsync("queso", true, CancellationToken.None, root.Id);
        Assert.Equal(new[] { "SUG-1" }, filtered.Select(p => p.SKU));

        var unfiltered = await inventory.GetSuggestionsAsync("queso", true, CancellationToken.None);
        Assert.Equal(2, unfiltered.Count);
    }

    // -------------------------------------------------- Import / Export

    [Fact]
    public async Task BulkImport_WithCategoryPath_AutoCreatesHierarchyAndAssignsLeaf()
    {
        using var db = CreateDb();
        var service = new InventoryService(db);

        var (added, _) = await service.BulkImportProductsAsync(new[]
        {
            new ProductImportDto
            {
                SKU = "IMP-CAT-1",
                Name = "Queso Importado",
                CostPriceUSD = 1.00m,
                ProfitMarginRetail = 30m,
                CategoryPath = "Alimentos > Lácteos"
            }
        }, overwriteMerge: false);

        Assert.Equal(1, added);

        var categories = await db.Categories.ToListAsync();
        Assert.Equal(2, categories.Count);
        var alimentos = categories.Single(c => c.Name == "Alimentos");
        var lacteos = categories.Single(c => c.Name == "Lácteos");
        Assert.Null(alimentos.ParentCategoryId);
        Assert.Equal(alimentos.Id, lacteos.ParentCategoryId);

        var product = await db.Products.SingleAsync(p => p.SKU == "IMP-CAT-1");
        var link = await db.ProductCategories.SingleAsync(pc => pc.ProductId == product.Id);
        Assert.Equal(lacteos.Id, link.CategoryId);
    }

    [Fact]
    public async Task BulkImport_ExistingCategoryPath_ReusesCategoriesCaseInsensitively()
    {
        using var db = CreateDb();
        var categories = new CategoryService(db);
        var root = await CreateCategoryAsync(categories, "Alimentos");
        await CreateCategoryAsync(categories, "Lácteos", root.Id);

        var service = new InventoryService(db);
        var (added, _) = await service.BulkImportProductsAsync(new[]
        {
            new ProductImportDto
            {
                SKU = "IMP-CAT-2",
                Name = "Queso Importado 2",
                CostPriceUSD = 1.00m,
                ProfitMarginRetail = 30m,
                CategoryPath = "  alimentos  >  LÁCTEOS "
            }
        }, overwriteMerge: false);

        Assert.Equal(1, added);
        Assert.Equal(2, await db.Categories.CountAsync());

        var product = await db.Products.SingleAsync(p => p.SKU == "IMP-CAT-2");
        var leaf = await db.Categories.SingleAsync(c => c.Name == "Lácteos");
        var link = await db.ProductCategories.SingleAsync(pc => pc.ProductId == product.Id);
        Assert.Equal(leaf.Id, link.CategoryId);
    }

    [Fact]
    public async Task BulkImport_CategoryPath_ReplacesPreviousAssignments()
    {
        using var db = CreateDb();
        var categories = new CategoryService(db);
        var inventory = new InventoryService(db);

        var previous = await CreateCategoryAsync(categories, "Anterior");
        var created = await CreateProductAsync(inventory, "IMP-CAT-3", "Producto Existente", new[] { previous.Id });

        var (_, updated) = await inventory.BulkImportProductsAsync(new[]
        {
            new ProductImportDto
            {
                SKU = "IMP-CAT-3",
                Name = "Producto Existente",
                CostPriceUSD = 1.50m,
                ProfitMarginRetail = 66.67m,
                CategoryPath = "Nueva > Hoja"
            }
        }, overwriteMerge: true);

        Assert.Equal(1, updated);
        var links = await db.ProductCategories.Where(pc => pc.ProductId == created.Id).ToListAsync();
        var leaf = await db.Categories.SingleAsync(c => c.Name == "Hoja");
        Assert.Single(links);
        Assert.Equal(leaf.Id, links[0].CategoryId);
    }

    [Fact]
    public async Task ExportCsv_IncludesCategoriasColumnWithPaths()
    {
        using var db = CreateDb();
        var categories = new CategoryService(db);
        var inventory = new InventoryService(db);

        var root = await CreateCategoryAsync(categories, "Alimentos");
        var leaf = await CreateCategoryAsync(categories, "Lácteos", root.Id);
        await CreateProductAsync(inventory, "EXP-1", "Queso Exportado", new[] { leaf.Id });

        var bytes = await inventory.ExportProductsAsync("csv", activeOnly: true, cancellationToken: CancellationToken.None);
        var csv = System.Text.Encoding.UTF8.GetString(bytes);
        var header = csv.Split('\n')[0].TrimStart('\uFEFF').Trim();

        Assert.Equal("Categorías", header.Split(';')[^1]);
        Assert.Contains("Alimentos > Lácteos", csv);
    }

    [Fact]
    public async Task ExportXlsx_IncludesCategoriasHeaderAndCell()
    {
        using var db = CreateDb();
        var categories = new CategoryService(db);
        var inventory = new InventoryService(db);

        var root = await CreateCategoryAsync(categories, "Alimentos");
        var leaf = await CreateCategoryAsync(categories, "Lácteos", root.Id);
        await CreateProductAsync(inventory, "EXP-2", "Queso Exportado Xlsx", new[] { leaf.Id });

        var bytes = await inventory.ExportProductsAsync("xlsx", activeOnly: true, cancellationToken: CancellationToken.None);

        using var stream = new System.IO.MemoryStream(bytes);
        using var workbook = new ClosedXML.Excel.XLWorkbook(stream);
        var worksheet = workbook.Worksheet("Productos");

        Assert.Equal("Categorías", worksheet.Cell(1, 16).Value.ToString());
        Assert.Equal("Queso Exportado Xlsx", worksheet.Cell(2, 2).Value.ToString());
        Assert.Equal("Alimentos > Lácteos", worksheet.Cell(2, 16).Value.ToString());
    }

    [Fact]
    public async Task ExportCsv_ProductWithoutCategories_HasEmptyCategoriasCell()
    {
        using var db = CreateDb();
        var inventory = new InventoryService(db);
        await CreateProductAsync(inventory, "EXP-3", "Producto Sin Categoría");

        var bytes = await inventory.ExportProductsAsync("csv", activeOnly: true, cancellationToken: CancellationToken.None);
        var lines = System.Text.Encoding.UTF8.GetString(bytes).Split('\n');
        var dataLine = lines.First(l => l.Contains("EXP-3", StringComparison.Ordinal)).Trim();

        Assert.EndsWith(";", dataLine);
    }

    [Fact]
    public async Task GenerateTemplate_IncludesSingularCategoriaHeader()
    {
        using var db = CreateDb();
        var inventory = new InventoryService(db);

        var csvBytes = await inventory.GenerateTemplateAsync("csv", CancellationToken.None);
        var header = System.Text.Encoding.UTF8.GetString(csvBytes).Split('\n')[0].TrimStart('\uFEFF').Trim();
        Assert.Equal("Categoría", header.Split(';')[^1]);

        var xlsxBytes = await inventory.GenerateTemplateAsync("xlsx", CancellationToken.None);
        using var stream = new System.IO.MemoryStream(xlsxBytes);
        using var workbook = new ClosedXML.Excel.XLWorkbook(stream);
        var worksheet = workbook.Worksheet("Plantilla_Productos");
        Assert.Equal("Categoría", worksheet.Cell(1, 16).Value.ToString());
    }

    // -------------------------------------------------- Controller contract

    [Fact]
    public async Task CategoriesController_DuplicateCreate_MapsTo409()
    {
        var service = new Mock<ICategoryService>();
        service.Setup(s => s.CreateCategoryAsync(It.IsAny<CreateCategoryRequestDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Ya existe una categoría con el nombre 'Bebidas' en este nivel."));
        var user = new Mock<ICurrentUserService>();
        user.Setup(u => u.CanMutateCatalog).Returns(true);
        var controller = new CategoriesController(service.Object, user.Object);

        var result = await controller.CreateAsync(new CreateCategoryRequestDto { Name = "Bebidas" }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task CategoriesController_MissingDelete_MapsTo404()
    {
        var service = new Mock<ICategoryService>();
        service.Setup(s => s.DeleteCategoryAsync(99, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Categoría con ID 99 no encontrada."));
        var user = new Mock<ICurrentUserService>();
        user.Setup(u => u.CanMutateCatalog).Returns(true);
        var controller = new CategoriesController(service.Object, user.Object);

        var result = await controller.DeleteAsync(99, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task CategoriesController_CashierMutation_MapsTo403()
    {
        var service = new Mock<ICategoryService>();
        var user = new Mock<ICurrentUserService>();
        user.Setup(u => u.CanMutateCatalog).Returns(false);
        var controller = new CategoriesController(service.Object, user.Object);

        var result = await controller.CreateAsync(new CreateCategoryRequestDto { Name = "Bebidas" }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, objectResult.StatusCode);
        service.Verify(s => s.CreateCategoryAsync(It.IsAny<CreateCategoryRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CategoriesController_Get_ReturnsOkWithDtos()
    {
        var service = new Mock<ICategoryService>();
        service.Setup(s => s.GetCategoriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CategoryDto> { new(1, "Bebidas", null, 0, "Bebidas") });
        var user = new Mock<ICurrentUserService>();
        var controller = new CategoriesController(service.Object, user.Object);

        var result = await controller.GetAsync(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dtos = Assert.IsAssignableFrom<IReadOnlyList<CategoryDto>>(ok.Value);
        Assert.Single(dtos);
        Assert.Equal("Bebidas", dtos[0].Path);
    }
}
