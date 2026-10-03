using System;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace CommandCenter.Tests.Integration;

[Collection(PostgresRealCollection.Name)]
public class SupplierInvoiceMigrationSmokeTests
{
    [Fact]
    public void SupplierInvoiceMigration_ModelIncludesRequiredSupplierMappings()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused")
            .Options;

        using var context = new InventoryDbContext(options);
        var model = context.Model;

        var supplierCodeIndex = model.FindEntityType(typeof(SupplierProductCode))!
            .GetIndexes()
            .Single(index => index.GetDatabaseName() == "IX_SupplierProductCodes_Supplier_Code");
        Assert.True(supplierCodeIndex.IsUnique);
        Assert.Equal("\"Code\" IS NOT NULL", supplierCodeIndex.GetFilter());

        var invoiceVersion = model.FindEntityType(typeof(SupplierInvoice))!.FindProperty("xmin");
        Assert.NotNull(invoiceVersion);
        Assert.True(invoiceVersion!.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, invoiceVersion.ValueGenerated);

        var lineType = model.FindEntityType(typeof(SupplierInvoiceLine))!;
        Assert.Equal("numeric(18,2)", lineType.FindProperty(nameof(SupplierInvoiceLine.UnitCostUSD))!.GetColumnType());
        Assert.Equal("numeric(18,3)", lineType.FindProperty(nameof(SupplierInvoiceLine.Quantity))!.GetColumnType());
    }

    [Fact]
    public async Task Migration_AddsSupplierSchema_WithoutChangingExistingProducts()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        // 8.144: migrar sobre una BD aislada propia (patron pos_smoke/pos_zero). Migrar la
        // pos_test compartida colisiona con las tablas pre-creadas por otros tests (42P07
        // "Products already exists"): esa BD no tiene historial EF.
        var supplierSmokeDatabaseName =
            Environment.GetEnvironmentVariable("SMOKE_DB_SUFFIX") is { Length: > 0 } suffix
                ? $"pos_supplier_{suffix}"
                : "pos_supplier_test";
        var connectionBuilder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = supplierSmokeDatabaseName
        };

        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(connectionBuilder.ConnectionString)
            .Options;

        await using var context = new InventoryDbContext(options);

        // La BD aislada puede no existir aun (EF la crea al migrar); crearla vacia primero
        // para poder snapshotear Products antes de aplicar las migraciones.
        var databaseCreator = context.Database.GetService<IRelationalDatabaseCreator>();
        if (!await databaseCreator.ExistsAsync())
        {
            await databaseCreator.CreateAsync();
        }

        var productsTableExists = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables " +
            "WHERE table_schema = 'public' AND table_name = 'Products'")
            .SingleAsync();
        var productsBefore = productsTableExists == 0 ? null : await GetProductSnapshotAsync(context);

        await context.Database.MigrateAsync();

        var supplierTableCount = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables " +
            "WHERE table_schema = 'public' AND table_name IN " +
            "('Suppliers', 'SupplierColumnMappings', 'SupplierProductCodes', 'SupplierInvoices', 'SupplierInvoiceLines')")
            .SingleAsync();
        Assert.Equal(5, supplierTableCount);

        var supplierIndexCount = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' " +
            "AND indexname IN ('IX_Suppliers_NormalizedRifOrNit', 'IX_Suppliers_NormalizedCommercialName', " +
            "'IX_SupplierColumnMappings_SupplierId', 'IX_SupplierProductCodes_Supplier_Code')")
            .SingleAsync();
        Assert.Equal(4, supplierIndexCount);

        var supplierCodeIndexIsUniqueAndFiltered = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' " +
            "AND tablename = 'SupplierProductCodes' AND indexname = 'IX_SupplierProductCodes_Supplier_Code' " +
            "AND indexdef ILIKE '%CREATE UNIQUE INDEX%' AND indexdef ILIKE '%WHERE%'")
            .SingleAsync();
        Assert.Equal(1, supplierCodeIndexIsUniqueAndFiltered);

        if (productsBefore is not null)
        {
            Assert.Equal(productsBefore, await GetProductSnapshotAsync(context));
        }
    }

    private static Task<string> GetProductSnapshotAsync(InventoryDbContext context) =>
        context.Database.SqlQueryRaw<string>(
            "SELECT COALESCE(jsonb_agg(to_jsonb(p) ORDER BY p.\"Id\"), '[]'::jsonb)::text AS \"Value\" " +
            "FROM \"Products\" AS p")
            .SingleAsync();
}
