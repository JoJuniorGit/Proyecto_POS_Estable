using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sales.Module.Data;
using Xunit;

namespace CommandCenter.Tests.Integration;

[Collection(PostgresRealCollection.Name)]
public class CustodyPartialDeliveryMigrationSmokeTests
{
    private const string PreviousMigrationId = "20260919152043_AddMissingUserSecurityColumnsRev8142";

    [Fact]
    public async Task Migration_AddsCustodySchema_AndBackfillsDeliveredOnly()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var smokeDatabaseName = $"pos_custody_partial_{Guid.NewGuid():N}";
        var testConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = smokeDatabaseName
        }.ConnectionString;
        var adminConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres"
        }.ConnectionString;

        await CreateSmokeDatabaseAsync(adminConnectionString, smokeDatabaseName);
        try
        {
            var options = new DbContextOptionsBuilder<SalesDbContext>()
                .UseNpgsql(testConnectionString)
                .Options;
            await using var context = new SalesDbContext(options);

            await context.Database.MigrateAsync(PreviousMigrationId);
            await context.Database.ExecuteSqlRawAsync(SeedLegacySalesSql);
            await context.Database.MigrateAsync();

            var deliveredQuantityColumnCount = await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.columns " +
                "WHERE table_schema = 'public' AND table_name = 'SaleItems' " +
                "AND column_name = 'DeliveredQuantity' AND data_type = 'numeric' " +
                "AND numeric_precision = 18 AND numeric_scale = 3 AND is_nullable = 'NO'")
                .SingleAsync();
            Assert.Equal(1, deliveredQuantityColumnCount);

            var custodyTableCount = await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables " +
                "WHERE table_schema = 'public' AND table_name IN ('SaleDeliveries', 'SaleDeliveryItems')")
                .SingleAsync();
            Assert.Equal(2, custodyTableCount);

            var deliveredQuantity = await context.Database.SqlQueryRaw<decimal>(
                "SELECT \"DeliveredQuantity\" AS \"Value\" FROM \"SaleItems\" WHERE \"Id\" = 920001")
                .SingleAsync();
            var custodyQuantity = await context.Database.SqlQueryRaw<decimal>(
                "SELECT \"DeliveredQuantity\" AS \"Value\" FROM \"SaleItems\" WHERE \"Id\" = 920002")
                .SingleAsync();

            Assert.Equal(4.500m, deliveredQuantity);
            Assert.Equal(0m, custodyQuantity);
        }
        finally
        {
            await DropSmokeDatabaseAsync(adminConnectionString, smokeDatabaseName);
        }
    }

    private static async Task CreateSmokeDatabaseAsync(string connectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var createDatabase = new NpgsqlCommand(
            $"CREATE DATABASE \"{databaseName}\"",
            connection);
        await createDatabase.ExecuteNonQueryAsync();
    }

    private static async Task DropSmokeDatabaseAsync(string connectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var dropDatabase = new NpgsqlCommand(
            $"DROP DATABASE \"{databaseName}\" WITH (FORCE)",
            connection);
        await dropDatabase.ExecuteNonQueryAsync();
    }

    private const string SeedLegacySalesSql = """
        INSERT INTO "Sales" (
            "Id", "Date", "Status", "Subtotal", "TotalUSD", "AppliedRate", "TotalBsS",
            "SubtotalBsS", "RoundingAdjustment", "FinalPaidAmountBsS", "DeliveryStatus",
            "PriceListType", "ClaimAction")
        VALUES
            (910001, TIMESTAMPTZ '2026-10-03 00:00:00+00', 1, 45.0000, 45.00, 1.0000, 45.00, 45.0000, 0.00, 45.00, 0, 'Retail', 0),
            (910002, TIMESTAMPTZ '2026-10-03 00:00:00+00', 1, 22.5000, 22.50, 1.0000, 22.50, 22.5000, 0.00, 22.50, 1, 'Retail', 0);

        INSERT INTO "SaleItems" (
            "Id", "SaleId", "Quantity", "UnitPrice", "Subtotal", "UnitPriceBsS",
            "SubtotalBsS", "ProductId", "ProductName", "IsCustomPrice")
        VALUES
            (920001, 910001, 4.500, 10.0000, 45.0000, 10.0000, 45.0000, 1, 'Delivered item', FALSE),
            (920002, 910002, 2.250, 10.0000, 22.5000, 10.0000, 22.5000, 2, 'Custody item', FALSE);
        """;
}
