using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T6-U18: real model-differ parity for both contexts. This is the same check that
/// produces PendingModelChangesWarning during migration startup.
/// </summary>
public class PendingModelChangesTests
{
    [Fact]
    public void SalesModel_HasNoPendingModelChanges()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql("Host=localhost;Database=model_snapshot_parity;Username=postgres;Password=postgres")
            .Options;
        using var context = new SalesDbContext(options);
        AssertNoDifferences(context);
    }

    [Fact]
    public void InventoryModel_HasNoPendingModelChanges()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql("Host=localhost;Database=model_snapshot_parity;Username=postgres;Password=postgres")
            .Options;
        using var context = new InventoryDbContext(options);
        AssertNoDifferences(context);
    }

    private static void AssertNoDifferences(DbContext context)
    {
        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);
        var initialized = context.GetService<IModelRuntimeInitializer>()
            .Initialize(snapshot!.Model, designTime: true);
        var differences = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(
                initialized.GetRelationalModel(),
                context.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        Assert.True(
            differences.Count == 0,
            $"El ModelSnapshot tiene drift contra el modelo EF: {differences.Count} operaciones pendientes.");
    }
}


