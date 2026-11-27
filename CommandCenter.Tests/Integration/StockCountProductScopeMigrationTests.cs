using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// WU1: la migración nueva debe aplicarse sobre PostgreSQL real, backfillear sesiones históricas
/// y crear la tabla puente con clave compuesta/FKs Restrict. Corre en una base aislada; sin
/// conexión el carril falla explícito (nunca silent-pass).
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public sealed class StockCountProductScopeMigrationTests
{
    private const string PreviousInventoryMigration = "20261106154248_WidenSupplierInvoiceUnitCostDocumentRev8211";

    [Fact]
    public async Task Migration_BackfillsLegacySessions_AndCreatesRestrictedBridge()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(applyMigrations: false);
        await using var context = database.CreateInventoryDbContext();
        var migrator = context.GetService<IMigrator>();

        // Estado previo: esquema Inventory completo hasta la migración anterior a esta feature.
        await migrator.MigrateAsync(PreviousInventoryMigration);

        var category = new Category { Name = "Categoría migración", NormalizedName = "CATEGORIA MIGRACION" };
        context.Categories.Add(category);
        var product = new ProductBuilder().WithId(0).WithSku("WU1-MIG").WithName("Producto migración").Build();
        context.Products.Add(product);
        await context.SaveChangesAsync();

        // Filas legacy sin ScopeType: una con categoría y otra sin categoría.
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"StockCountSessions\" (\"Name\",\"Status\",\"ScopeCategoryId\",\"CreatedAt\") " +
            "VALUES ('Legacy Cat','Open',{0},now()), ('Legacy All','Open',NULL,now())",
            category.Id);

        await migrator.MigrateAsync();

        // Backfill: categoría -> Category; null -> All (default de la columna).
        var sessions = await context.StockCountSessions.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.Equal(StockCountScopeType.Category, sessions[0].ScopeType);
        Assert.Equal(StockCountScopeType.All, sessions[1].ScopeType);

        var scopeColumn = await context.Database.SqlQueryRaw<string>(
                "SELECT data_type || ':' || character_maximum_length::text || ':' || is_nullable || ':' || column_default AS \"Value\" " +
                "FROM information_schema.columns WHERE table_schema = 'public' " +
                "AND table_name = 'StockCountSessions' AND column_name = 'ScopeType'")
            .SingleAsync();
        Assert.Contains("character varying:32:NO", scopeColumn);
        Assert.Contains("'All'", scopeColumn);

        // Tabla puente, PK compuesta e índice por producto.
        var bridgeTableCount = await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables " +
                "WHERE table_schema = 'public' AND table_name = 'StockCountSessionProducts'")
            .SingleAsync();
        Assert.Equal(1, bridgeTableCount);

        var pkCount = await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM pg_constraint " +
                "WHERE conrelid = '\"StockCountSessionProducts\"'::regclass AND contype = 'p'")
            .SingleAsync();
        Assert.Equal(1, pkCount);

        var restrictFkCount = await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM pg_constraint " +
                "WHERE conrelid = '\"StockCountSessionProducts\"'::regclass AND contype = 'f' AND confdeltype = 'r'")
            .SingleAsync();
        Assert.Equal(2, restrictFkCount);

        var productIndexCount = await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' " +
                "AND indexname = 'IX_StockCountSessionProducts_ProductId'")
            .SingleAsync();
        Assert.Equal(1, productIndexCount);

        context.StockCountSessionProducts.Add(new StockCountSessionProduct
        {
            StockCountSessionId = sessions[0].Id,
            ProductId = product.Id
        });
        await context.SaveChangesAsync();

        // FK Product Restrict: borrar un producto referenciado por el alcance debe fallar.
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var delete = new NpgsqlCommand("DELETE FROM \"Products\" WHERE \"Id\" = @id", connection);
        delete.Parameters.AddWithValue("id", product.Id);
        await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
    }
}
