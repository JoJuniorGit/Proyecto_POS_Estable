using System.Linq;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// WU1: el modelo EF debe exponer el alcance por producto y la tabla puente antes de que la
/// migración los cree. La guarda de drift completa vive en MigrationIndexDriftTests.
/// </summary>
public sealed class StockCountProductScopeModelTests
{
    [Fact]
    public void Model_ExposesScopeTypeAndStockCountSessionProductsBridge()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        using (connection)
        using (context)
        {
            var session = context.Model.FindEntityType(typeof(StockCountSession));
            Assert.NotNull(session);

            var scopeType = session!.FindProperty(nameof(StockCountSession.ScopeType));
            Assert.NotNull(scopeType);
            Assert.Equal(typeof(StockCountScopeType), scopeType!.ClrType);

            var bridge = context.Model.FindEntityType(typeof(StockCountSessionProduct));
            Assert.NotNull(bridge);
            Assert.Equal("StockCountSessionProducts", bridge!.GetTableName());

            var key = bridge.FindPrimaryKey();
            Assert.NotNull(key);
            Assert.Equal(
                new[] { "StockCountSessionId", "ProductId" },
                key!.Properties.Select(property => property.Name).ToArray());

            var foreignKeys = bridge.GetForeignKeys().ToList();
            Assert.Equal(2, foreignKeys.Count);
            Assert.All(foreignKeys, fk => Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior));
        }
    }

    [Fact]
    public void ScopeType_HasStableNumericContract()
    {
        Assert.Equal(0, (int)StockCountScopeType.All);
        Assert.Equal(1, (int)StockCountScopeType.Category);
        Assert.Equal(2, (int)StockCountScopeType.Products);
    }
}
