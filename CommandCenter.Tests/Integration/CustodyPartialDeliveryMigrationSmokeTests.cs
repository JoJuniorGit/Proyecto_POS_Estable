using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// Smoke real de la migración de custodia: siembra ventas/ítems legacy sobre el esquema previo
/// (<c>AddMissingUserSecurityColumnsRev8142</c>), aplica la migración de custodia y verifica
/// columnas/tablas/backfill. Corre en una base AISLADA por test del carril
/// <see cref="PostgresTestLane"/> (creada vacía y eliminada al final); antes creaba una base por
/// nombre GUID solo cuando TEST_POSTGRES_CONNECTION existía y retornaba en silencio si faltaba
/// (silent-pass). Sin conexión utilizable el lane falla EXPLÍCITO.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class CustodyPartialDeliveryMigrationSmokeTests
{
    private const string PreviousMigrationId = "20260919152043_AddMissingUserSecurityColumnsRev8142";

    [Fact]
    public async Task Migration_AddsCustodySchema_AndBackfillsDeliveredOnly()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(applyMigrations: false);
        await using var context = database.CreateSalesDbContext();

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
