using System;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
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
    [Trait("Category", "RequiresDocker")]
    public async Task UpdateProductFromDto_StaleToken_Conflicts()
    {
        var connStr = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connStr)) return;

        TestSchemaBootstrap.EnsureSharedSchema(connStr);

        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(connStr)
            .Options;

        int productId;
        using (var seedContext = new InventoryDbContext(options))
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

        using var staleContext = new InventoryDbContext(options);
        var staleService = new InventoryService(staleContext);
        await staleContext.Products.FirstAsync(p => p.Id == productId);

        using (var writerContext = new InventoryDbContext(options))
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
