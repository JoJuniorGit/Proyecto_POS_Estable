using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// WU2: alcance estricto por producto. Cubre validación de exclusividad, persistencia de la tabla
/// puente, rechazo de escaneos/ediciones fuera de alcance, review solo de seleccionados y apply
/// únicamente sobre lo escaneado.
/// </summary>
public sealed class StockCountProductScopeServiceTests
{
    private const string CounterUserId = "counter-wu2";

    [Fact]
    public async Task CreateAsync_ProductScope_PersistsBridgeAndExposesSelectedLabel()
    {
        var (service, context, _) = CreateService();
        var first = await AddProductAsync(context, new ProductBuilder().WithSku("WU2-ONE").WithName("Uno").WithStock(5m));
        var second = await AddProductAsync(context, new ProductBuilder().WithSku("WU2-TWO").WithName("Dos").WithStock(6m));

        var created = await service.CreateAsync(new CreateStockCountRequestDto(
            "Conteo focalizado",
            ScopeType: StockCountScopeType.Products,
            ScopeProductIds: new List<int> { first.Id, second.Id }));

        Assert.Equal(StockCountScopeType.Products, created.ScopeType);
        Assert.Equal(2, created.ScopeProductCount);
        Assert.Equal("Productos seleccionados (2)", created.ScopeDisplay);
        Assert.Null(created.ScopeCategoryId);
        Assert.Equal(0, created.ItemsCount);

        var bridge = await context.StockCountSessionProducts.AsNoTracking()
            .Where(sp => sp.StockCountSessionId == created.Id)
            .ToListAsync();
        Assert.Equal(2, bridge.Count);
        Assert.Contains(bridge, row => row.ProductId == first.Id);
        Assert.Contains(bridge, row => row.ProductId == second.Id);
    }

    [Fact]
    public async Task CreateAsync_LegacyRequests_InfersAllAndCategoryAndKeepsDisplays()
    {
        var (service, context, _) = CreateService();
        var category = await AddCategoryAsync(context, "Abarrotes", null);

        var all = await service.CreateAsync(new CreateStockCountRequestDto("Todo"));
        var byCategory = await service.CreateAsync(new CreateStockCountRequestDto("Categoría", category.Id));

        Assert.Equal(StockCountScopeType.All, all.ScopeType);
        Assert.Equal("Todo el inventario", all.ScopeDisplay);
        Assert.Equal(0, all.ScopeProductCount);

        Assert.Equal(StockCountScopeType.Category, byCategory.ScopeType);
        Assert.Equal("Abarrotes", byCategory.ScopeDisplay);
        Assert.Equal(0, byCategory.ScopeProductCount);
    }

    [Fact]
    public async Task CreateAsync_ProductScope_RejectsInvalidExclusiveSelections()
    {
        var (service, context, _) = CreateService();
        var product = await AddProductAsync(context, new ProductBuilder().WithSku("WU2-VALID").WithStock(1m));
        var inactive = await AddProductAsync(context, new ProductBuilder().WithSku("WU2-INACT").AsInactive().WithStock(1m));
        var cashAdvance = new Product { SKU = "WU2-CASH", Name = "Adelanto", IsCashAdvance = true };
        context.Products.Add(cashAdvance);
        await context.SaveChangesAsync();

        var tooMany = Enumerable.Range(1, 1001).ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            new CreateStockCountRequestDto("Vacío", ScopeType: StockCountScopeType.Products)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            new CreateStockCountRequestDto("Tope", ScopeType: StockCountScopeType.Products, ScopeProductIds: tooMany)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            new CreateStockCountRequestDto("Duplicado", ScopeType: StockCountScopeType.Products,
                ScopeProductIds: new List<int> { product.Id, product.Id })));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            new CreateStockCountRequestDto("Inexistente", ScopeType: StockCountScopeType.Products,
                ScopeProductIds: new List<int> { 999999 })));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            new CreateStockCountRequestDto("No contable", ScopeType: StockCountScopeType.Products,
                ScopeProductIds: new List<int> { cashAdvance.Id })));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            new CreateStockCountRequestDto("Inactivo", ScopeType: StockCountScopeType.Products,
                ScopeProductIds: new List<int> { inactive.Id })));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            new CreateStockCountRequestDto("Categoría y productos", ScopeCategoryId: 1,
                ScopeType: StockCountScopeType.Products, ScopeProductIds: new List<int> { product.Id })));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            new CreateStockCountRequestDto("Todo con categoría", ScopeCategoryId: 1,
                ScopeType: StockCountScopeType.All)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            new CreateStockCountRequestDto("Categoría sin categoría", ScopeType: StockCountScopeType.Category)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            new CreateStockCountRequestDto("Categoría con productos", ScopeType: StockCountScopeType.Category,
                ScopeProductIds: new List<int> { product.Id })));

        Assert.Empty(await context.StockCountSessions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ScanAndSet_ProductOutsideSelectedScope_Throws409WithOperatorMessage()
    {
        var (service, context, _) = CreateService();
        var inside = await AddProductAsync(context, new ProductBuilder().WithSku("WU2-IN").WithStock(3m));
        var outside = await AddProductAsync(context, new ProductBuilder().WithSku("WU2-OUT").WithStock(4m));
        var session = await service.CreateAsync(new CreateStockCountRequestDto(
            "Focalizado",
            ScopeType: StockCountScopeType.Products,
            ScopeProductIds: new List<int> { inside.Id }));

        var scanned = await service.ScanAsync(session.Id, new ScanStockCountRequestDto("WU2-IN"));
        Assert.Equal(1m, scanned.CountedQuantity);

        var scanError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ScanAsync(session.Id, new ScanStockCountRequestDto("WU2-OUT")));
        Assert.Contains("productos seleccionados", scanError.Message);
        Assert.Contains("alcance", scanError.Message);
        Assert.Contains("WU2-OUT", scanError.Message);

        var setError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetCountedQuantityAsync(session.Id, outside.Id, new SetCountedQuantityRequestDto(9m)));
        Assert.Contains("productos seleccionados", setError.Message);
        Assert.Single(await context.StockCountItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ReviewAsync_ProductScope_CountsOnlySelectedUnscanned()
    {
        var (service, context, _) = CreateService();
        var first = await AddProductAsync(context, new ProductBuilder().WithSku("WU2-R1").WithStock(5m));
        var second = await AddProductAsync(context, new ProductBuilder().WithSku("WU2-R2").WithStock(6m));
        await AddProductAsync(context, new ProductBuilder().WithSku("WU2-R3").WithStock(7m));

        var session = await service.CreateAsync(new CreateStockCountRequestDto(
            "Focalizado",
            ScopeType: StockCountScopeType.Products,
            ScopeProductIds: new List<int> { first.Id, second.Id }));
        await service.ScanAsync(session.Id, new ScanStockCountRequestDto("WU2-R1"));

        var review = await service.ReviewAsync(session.Id);

        Assert.Single(review.Items);
        Assert.Equal(first.Id, review.Items[0].ProductId);
        Assert.Equal(1, review.UnscannedInScopeCount);
    }

    [Fact]
    public async Task ApplyAsync_ProductScope_AppliesOnlyScannedSelectedProducts()
    {
        var (service, context, _) = CreateService();
        var scanned = await AddProductAsync(context, new ProductBuilder().WithSku("WU2-APP1").WithStock(10m));
        var notScannedSelected = await AddProductAsync(context, new ProductBuilder().WithSku("WU2-APP2").WithStock(20m));
        var outside = await AddProductAsync(context, new ProductBuilder().WithSku("WU2-APP3").WithStock(30m));

        var session = await service.CreateAsync(new CreateStockCountRequestDto(
            "Focalizado",
            ScopeType: StockCountScopeType.Products,
            ScopeProductIds: new List<int> { scanned.Id, notScannedSelected.Id }));
        await service.ScanAsync(session.Id, new ScanStockCountRequestDto("WU2-APP1", 12m));
        await service.CloseAsync(session.Id);

        var applied = await service.ApplyAsync(session.Id);

        Assert.Equal(StockCountStatus.Applied, applied.Status);
        Assert.Equal(12m, (await context.Products.AsNoTracking().SingleAsync(p => p.Id == scanned.Id)).StockQuantity);
        Assert.Equal(20m, (await context.Products.AsNoTracking().SingleAsync(p => p.Id == notScannedSelected.Id)).StockQuantity);
        Assert.Equal(30m, (await context.Products.AsNoTracking().SingleAsync(p => p.Id == outside.Id)).StockQuantity);

        var movement = await context.StockMovements.AsNoTracking().SingleAsync();
        Assert.Equal(scanned.Id, movement.ProductId);
        Assert.Equal(2m, movement.QuantityChange);
        Assert.Equal(session.Id, movement.StockCountSessionId);
    }

    [Fact]
    public void CreateRequest_RoundTripsScopeFields_WithWebJsonDefaults()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = """{"name":"Focal","scopeType":2,"scopeProductIds":[10,20]}""";

        var dto = JsonSerializer.Deserialize<CreateStockCountRequestDto>(json, options);

        Assert.NotNull(dto);
        Assert.Equal(StockCountScopeType.Products, dto!.ScopeType);
        Assert.Equal(new[] { 10, 20 }, dto.ScopeProductIds);

        var serialized = JsonSerializer.Serialize(dto, options);
        Assert.Contains("\"scopeType\":2", serialized);
        Assert.Contains("\"scopeProductIds\":[10,20]", serialized);
    }

    private static (StockCountService Service, InventoryDbContext Context, Mock<ICurrentUserService> User) CreateService()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        var user = new Mock<ICurrentUserService>();
        user.SetupGet(u => u.UserId).Returns(CounterUserId);
        user.SetupGet(u => u.CanMutateCatalog).Returns(true);
        var inventoryService = new InventoryService(context, user.Object);
        return (new StockCountService(context, inventoryService, user.Object), context, user);
    }

    private static async Task<Product> AddProductAsync(InventoryDbContext context, ProductBuilder builder)
    {
        var product = builder.WithId(0).Build();
        context.Products.Add(product);
        await context.SaveChangesAsync();
        return product;
    }

    private static async Task<Category> AddCategoryAsync(InventoryDbContext context, string name, int? parentId)
    {
        var category = new Category { Name = name, NormalizedName = name.ToUpperInvariant(), ParentCategoryId = parentId };
        context.Categories.Add(category);
        await context.SaveChangesAsync();
        return category;
    }
}
