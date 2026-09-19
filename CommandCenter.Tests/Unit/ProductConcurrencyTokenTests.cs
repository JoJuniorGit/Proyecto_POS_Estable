using Core.Entities;
using Inventory.Module.Data;
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
}
