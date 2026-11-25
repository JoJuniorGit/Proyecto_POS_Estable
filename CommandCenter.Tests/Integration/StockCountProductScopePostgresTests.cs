using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// WU2 sobre PostgreSQL real: persiste el alcance Products, bloquea escaneos fuera de la selección,
/// cuenta solo los seleccionados no escaneados y aplica únicamente los ítems escaneados.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public sealed class StockCountProductScopePostgresTests
{
    [Fact]
    public async Task ProductScope_IsStrict_AndApplyTouchesOnlyScannedProducts()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(
            applyMigrations: false,
            applyInventoryMigrations: true);
        await using var context = database.CreateInventoryDbContext();

        var first = new ProductBuilder().WithId(0).WithSku("PG-WU2-1").WithName("Primero").WithCostAndMargin(2m, 30m).WithStock(10m).Build();
        var second = new ProductBuilder().WithId(0).WithSku("PG-WU2-2").WithName("Segundo").WithStock(20m).Build();
        var outside = new ProductBuilder().WithId(0).WithSku("PG-WU2-3").WithName("Fuera").WithStock(30m).Build();
        context.Products.AddRange(first, second, outside);
        await context.SaveChangesAsync();

        var user = Mock.Of<ICurrentUserService>(u => u.UserId == "pg-wu2" && u.CanMutateCatalog == true);
        var inventory = new InventoryService(context, user);
        var service = new StockCountService(context, inventory, user);

        var created = await service.CreateAsync(new CreateStockCountRequestDto(
            "Conteo PostgreSQL focalizado",
            ScopeType: StockCountScopeType.Products,
            ScopeProductIds: new[] { first.Id, second.Id }.ToList()));

        Assert.Equal(StockCountScopeType.Products, created.ScopeType);
        Assert.Equal(2, created.ScopeProductCount);
        Assert.Equal("Productos seleccionados (2)", created.ScopeDisplay);
        Assert.Equal(
            2,
            await context.StockCountSessionProducts.AsNoTracking()
                .CountAsync(sp => sp.StockCountSessionId == created.Id));

        await service.ScanAsync(created.Id, new ScanStockCountRequestDto("PG-WU2-1", 12m));

        var scanError = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => service.ScanAsync(created.Id, new ScanStockCountRequestDto("PG-WU2-3")));
        Assert.Contains("productos seleccionados", scanError.Message);
        Assert.Contains("PG-WU2-3", scanError.Message);

        var review = await service.ReviewAsync(created.Id);
        Assert.Single(review.Items);
        Assert.Equal(1, review.UnscannedInScopeCount);

        await service.CloseAsync(created.Id);
        var applied = await service.ApplyAsync(created.Id);
        Assert.Equal(StockCountStatus.Applied, applied.Status);

        Assert.Equal(12m, (await context.Products.AsNoTracking().SingleAsync(p => p.Id == first.Id)).StockQuantity);
        Assert.Equal(20m, (await context.Products.AsNoTracking().SingleAsync(p => p.Id == second.Id)).StockQuantity);
        Assert.Equal(30m, (await context.Products.AsNoTracking().SingleAsync(p => p.Id == outside.Id)).StockQuantity);

        var movements = await context.StockMovements.AsNoTracking()
            .Where(m => m.StockCountSessionId == created.Id)
            .ToListAsync();
        var movement = Assert.Single(movements);
        Assert.Equal(first.Id, movement.ProductId);
        Assert.Equal(2m, movement.QuantityChange);
    }
}
