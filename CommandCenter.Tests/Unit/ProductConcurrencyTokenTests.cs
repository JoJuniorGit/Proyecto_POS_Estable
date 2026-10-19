using System;
using System.Threading.Tasks;
using CommandCenter.Tests.Integration;
using Core.DTOs;
using Core.Entities;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommandCenter.Tests.Unit;

// El token xmin solo es efectivo a traves del camino de actualizacion rastreado
// (FindAsync + CurrentValues.SetValues en InventoryService.UpdateProductAsync):
// un refactor a un update detached o a ExecuteUpdate lo evadiria sin aviso.
//
// El test de conflicto corre contra PostgreSQL real en base AISLADA por test (carril
// PostgresTestLane): antes escribia en la base COMPARTIDA del harness legacy
// (TestSchemaBootstrap.EnsureSharedSchema) y con TEST_POSTGRES_CONNECTION ausente retornaba
// en silencio. Ahora sin conexion utilizable falla EXPLICITO (nunca silent-pass).
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class ProductConcurrencyTokenTests
{
    private const string NpgsqlConnectionString = "Host=localhost;Database=none;Username=none;Password=none";

    [Fact]
    public void InventoryModel_NpgsqlProvider_XminIsConfiguredAsConcurrencyToken()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(NpgsqlConnectionString)
            .Options;
        using var context = new InventoryDbContext(options);

        var productType = context.Model.FindEntityType(typeof(Product));
        Assert.NotNull(productType);

        var xmin = productType!.FindProperty("xmin");
        Assert.NotNull(xmin);
        Assert.Equal("xid", xmin!.GetColumnType());
        Assert.True(xmin.IsConcurrencyToken);
    }

    [Fact]
    public void InventoryModel_SqliteProvider_XminIsNotConfigured()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        using var context = new InventoryDbContext(options);

        var productType = context.Model.FindEntityType(typeof(Product));
        Assert.NotNull(productType);

        Assert.Null(productType!.FindProperty("xmin"));
    }

    [Fact]
    public async Task UpdateProductFromDto_StaleToken_Conflicts()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(
            applyMigrations: false,
            applyInventoryMigrations: true);

        int productId;
        await using (var seedContext = database.CreateInventoryDbContext())
        {
            var product = new Product
            {
                Name = "Stale Token Probe",
                SKU = $"STALE-{Guid.NewGuid():N}"[..20],
                PriceRetailUSD = 5m,
                CostPriceUSD = 2m,
                IsActive = true
            };
            seedContext.Products.Add(product);
            await seedContext.SaveChangesAsync();
            productId = product.Id;
        }

        await using var staleContext = database.CreateInventoryDbContext();
        var staleService = new InventoryService(staleContext);
        await staleContext.Products.FirstAsync(p => p.Id == productId);

        await using (var writerContext = database.CreateInventoryDbContext())
        {
            var writerService = new InventoryService(writerContext);
            await writerService.UpdateProductFromDtoAsync(productId, new UpdateProductDto
            {
                Id = productId,
                Name = "Changed By Writer",
                PriceRetailUSD = 6m,
                CostPriceUSD = 2m,
                IsActive = true
            });
        }

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleService.UpdateProductFromDtoAsync(productId, new UpdateProductDto
        {
            Id = productId,
            Name = "Changed By Stale Reader",
            PriceRetailUSD = 7m,
            CostPriceUSD = 2m,
            IsActive = true
        }));
    }
}
