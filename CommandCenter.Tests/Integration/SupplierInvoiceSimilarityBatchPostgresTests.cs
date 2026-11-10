using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Inventory.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

[Collection(PostgresRealCollection.Name)]
public class SupplierInvoiceSimilarityBatchPostgresTests
{
    [Fact]
    public async Task FindCandidatesBatchAsync_ResolvesAllNamesInOneQuery()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(applyInventoryMigrations: true);
        await using var context = database.CreateInventoryDbContext();

        context.Products.AddRange(
            new Product
            {
                Name = "Harina Pan 1kg",
                SKU = "SIM-HARINA-1",
                PriceUSD = 1m,
                IsActive = true,
                IsDeleted = false,
                IsGroupHeader = false
            },
            new Product
            {
                Name = "Arroz Mary 1kg",
                SKU = "SIM-ARROZ-1",
                PriceUSD = 2m,
                IsActive = true,
                IsDeleted = false,
                IsGroupHeader = false
            });
        await context.SaveChangesAsync();

        var search = new PostgresSupplierProductSimilaritySearch(context);
        var result = await search.FindCandidatesBatchAsync(
            new[] { "Harina Pan 1kg", "Arroz Mary 1kg", "Producto Inexistente" },
            0.3d);

        Assert.Equal(2, result.Count);
        Assert.Single(result["Harina Pan 1kg"]);
        Assert.Single(result["Arroz Mary 1kg"]);
        Assert.DoesNotContain("Producto Inexistente", result.Keys);
    }
}
