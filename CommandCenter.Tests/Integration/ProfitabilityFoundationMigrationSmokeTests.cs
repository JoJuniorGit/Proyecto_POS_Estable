using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// Smoke real de la migración de rentabilidad (Paso 14, F1-WU1/F1-WU2): siembra ventas, pagos y
/// movimientos de caja legacy sobre el esquema previo
/// (<c>AddCashDrawerSessionMethodBalancesRev8212</c>), aplica la migración de fundación y el
/// backfill correctivo de comisión, y verifica columnas/tablas/índices/FKs y los backfills
/// históricos. Corre en una base AISLADA por test del carril <see cref="PostgresTestLane"/>
/// (creada vacía y eliminada al final); sin conexión utilizable el lane falla EXPLÍCITO.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class ProfitabilityFoundationMigrationSmokeTests
{
    // El seed usa SQL crudo a propósito: el modelo EF ya incluiría las columnas nuevas, que a
    // esta altura de la cadena de migraciones todavía no existen en la base.
    private const string PreviousMigrationId = "20261107020155_AddCashDrawerSessionMethodBalancesRev8212";
    private const string ProfitabilityMigrationId = "20261109231727_AddProfitabilityFoundationRev8213";
    private const string CorrectiveBackfillMigrationId = "20261110000007_CorrectCashAdvanceCommissionBackfillRev8213";

    [Fact]
    public async Task Migration_AddsProfitabilitySchema_AndBackfillsHistoricalSales()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(applyMigrations: false);
        await using var context = database.CreateSalesDbContext();

        await context.Database.MigrateAsync(PreviousMigrationId);
        await context.Database.ExecuteSqlRawAsync(SeedLegacySalesSql);
        await context.Database.MigrateAsync();

        await AssertSalesColumnsAsync(context);
        await AssertPaymentFeeColumnsAsync(context, "SalePayments");
        await AssertPaymentFeeColumnsAsync(context, "ReceivablePayments");
        await AssertPaymentMethodFeeRulesAsync(context);
        await AssertSupportIndexesAsync(context);
        await AssertBackfillsAsync(context);

        // Reintento: la migración ya aplicada no debe re-ejecutarse ni duplicar su historial.
        await context.Database.MigrateAsync();

        var profitabilityHistoryCount = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = {0}",
            ProfitabilityMigrationId).SingleAsync();
        Assert.Equal(1, profitabilityHistoryCount);

        var correctiveHistoryCount = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = {0}",
            CorrectiveBackfillMigrationId).SingleAsync();
        Assert.Equal(1, correctiveHistoryCount);
    }

    private static async Task AssertSalesColumnsAsync(SalesDbContext context)
    {
        var saleTypeShape = await context.Database.SqlQueryRaw<string>(
            "SELECT data_type || ':' || is_nullable AS \"Value\" FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'Sales' AND column_name = 'SaleType'")
            .SingleAsync();
        Assert.Equal("integer:NO", saleTypeShape);

        var saleTypeDefault = await context.Database.SqlQueryRaw<string>(
            "SELECT column_default AS \"Value\" FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'Sales' AND column_name = 'SaleType'")
            .SingleAsync();
        Assert.Equal("0", saleTypeDefault);

        var commissionUsdShape = await context.Database.SqlQueryRaw<string>(
            "SELECT data_type || ':' || numeric_precision::text || ':' || numeric_scale::text || ':' || is_nullable AS \"Value\" " +
            "FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'Sales' AND column_name = 'CommissionAmountUSD'")
            .SingleAsync();
        Assert.Equal("numeric:18:4:NO", commissionUsdShape);

        var commissionLocalShape = await context.Database.SqlQueryRaw<string>(
            "SELECT data_type || ':' || numeric_precision::text || ':' || numeric_scale::text || ':' || is_nullable AS \"Value\" " +
            "FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'Sales' AND column_name = 'CommissionAmountLocal'")
            .SingleAsync();
        Assert.Equal("numeric:18:2:NO", commissionLocalShape);

        var cancelledAtShape = await context.Database.SqlQueryRaw<string>(
            "SELECT data_type || ':' || is_nullable AS \"Value\" FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'Sales' AND column_name = 'CancelledAtUtc'")
            .SingleAsync();
        Assert.Equal("timestamp with time zone:YES", cancelledAtShape);

        var unitCostLocalShape = await context.Database.SqlQueryRaw<string>(
            "SELECT data_type || ':' || numeric_precision::text || ':' || numeric_scale::text || ':' || is_nullable AS \"Value\" " +
            "FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'SaleItems' AND column_name = 'UnitCostLocal'")
            .SingleAsync();
        Assert.Equal("numeric:18:4:YES", unitCostLocalShape);
    }

    private static async Task AssertPaymentFeeColumnsAsync(SalesDbContext context, string tableName)
    {
        // Las 10 columnas de snapshot de comisión comparten contrato en ambos medios de cobro.
        var matchingColumnCount = await context.Database.SqlQueryRaw<int>(
            """
            SELECT COUNT(*)::int AS "Value"
            FROM (VALUES
                ('PaymentMethodFeeRuleId', 'integer', NULL::int, NULL::int, NULL::int, 'YES'),
                ('FeeType', 'integer', NULL::int, NULL::int, NULL::int, 'YES'),
                ('FeePercentage', 'numeric', 9, 4, NULL::int, 'YES'),
                ('FeeFixedAmount', 'numeric', 18, 2, NULL::int, 'YES'),
                ('FeeCurrencyCode', 'character varying', NULL::int, NULL::int, 10, 'YES'),
                ('FeeAmountUsd', 'numeric', 18, 4, NULL::int, 'NO'),
                ('FeeAmountLocal', 'numeric', 18, 2, NULL::int, 'NO'),
                ('FeeRate', 'numeric', 18, 4, NULL::int, 'NO'),
                ('FeeConfigured', 'boolean', NULL::int, NULL::int, NULL::int, 'NO'),
                ('FeeRuleEffectiveFromUtc', 'timestamp with time zone', NULL::int, NULL::int, NULL::int, 'YES')
            ) AS expected(column_name, data_type, numeric_precision, numeric_scale, character_maximum_length, is_nullable)
            JOIN information_schema.columns AS c
              ON c.table_schema = 'public'
             AND c.table_name = {0}
             AND c.column_name = expected.column_name
             AND c.data_type = expected.data_type
             AND c.is_nullable = expected.is_nullable
             -- PostgreSQL sintetiza precision 32 / escala 0 para enteros; la forma exacta
             -- solo es contrato para los campos monetarios/porcentuales numeric.
             AND (expected.data_type <> 'numeric'
                  OR (c.numeric_precision = expected.numeric_precision
                      AND c.numeric_scale = expected.numeric_scale))
             AND c.character_maximum_length IS NOT DISTINCT FROM expected.character_maximum_length
            """,
            tableName).SingleAsync();
        Assert.Equal(10, matchingColumnCount);
    }

    private static async Task AssertPaymentMethodFeeRulesAsync(SalesDbContext context)
    {
        var tableCount = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables " +
            "WHERE table_schema = 'public' AND table_name = 'PaymentMethodFeeRules'")
            .SingleAsync();
        Assert.Equal(1, tableCount);

        // xmin es pseudo-columna de sistema en PostgreSQL: solo el token de concurrencia la expone
        // y no debe materializarse como columna física propia de la tabla.
        var xminColumnCount = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'PaymentMethodFeeRules' AND column_name = 'xmin'")
            .SingleAsync();
        Assert.Equal(0, xminColumnCount);

        var uniqueIndexCount = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' " +
            "AND tablename = 'PaymentMethodFeeRules' " +
            "AND indexname = 'IX_PaymentMethodFeeRules_PaymentMethodId_EffectiveFromUtc' " +
            "AND indexdef ILIKE 'CREATE UNIQUE INDEX%'")
            .SingleAsync();
        Assert.Equal(1, uniqueIndexCount);

        var foreignKeyCount = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM pg_constraint WHERE contype = 'f' AND conname IN " +
            "('FK_PaymentMethodFeeRules_PaymentMethods', 'FK_SalePayments_PaymentMethodFeeRules', 'FK_ReceivablePayments_PaymentMethodFeeRules')")
            .SingleAsync();
        Assert.Equal(3, foreignKeyCount);
    }

    private static async Task AssertSupportIndexesAsync(SalesDbContext context)
    {
        var indexCount = await context.Database.SqlQueryRaw<int>(
            """
            SELECT COUNT(*)::int AS "Value" FROM pg_indexes
            WHERE schemaname = 'public'
              AND ((tablename = 'CreditReturns' AND indexname = 'IX_CreditReturns_CreatedAtUtc'
                    AND indexdef ILIKE '%"CreatedAtUtc"%')
                OR (tablename = 'Sales' AND indexname = 'IX_Sales_CancelledAtUtc'
                    AND indexdef ILIKE '%"CancelledAtUtc"%')
                OR (tablename = 'Sales' AND indexname = 'IX_Sales_SaleType_Status_Date'
                    AND indexdef ILIKE '%"SaleType", "Status", "Date"%'))
            """).SingleAsync();
        Assert.Equal(3, indexCount);
    }

    private static async Task AssertBackfillsAsync(SalesDbContext context)
    {
        var advanceSaleType = await context.Database.SqlQueryRaw<int>(
            "SELECT \"SaleType\" AS \"Value\" FROM \"Sales\" WHERE \"Id\" = 1002")
            .SingleAsync();
        Assert.Equal(1, advanceSaleType);

        var advanceCommissionLocal = await context.Database.SqlQueryRaw<decimal>(
            "SELECT \"CommissionAmountLocal\" AS \"Value\" FROM \"Sales\" WHERE \"Id\" = 1002")
            .SingleAsync();
        Assert.Equal(70.00m, advanceCommissionLocal);

        var advanceCommissionUsd = await context.Database.SqlQueryRaw<decimal>(
            "SELECT \"CommissionAmountUSD\" AS \"Value\" FROM \"Sales\" WHERE \"Id\" = 1002")
            .SingleAsync();
        Assert.Equal(1.4000m, advanceCommissionUsd);

        var merchandiseSaleType = await context.Database.SqlQueryRaw<int>(
            "SELECT \"SaleType\" AS \"Value\" FROM \"Sales\" WHERE \"Id\" = 1001")
            .SingleAsync();
        Assert.Equal(0, merchandiseSaleType);

        var merchandiseUnitCostLocal = await context.Database.SqlQueryRaw<decimal>(
            "SELECT \"UnitCostLocal\" AS \"Value\" FROM \"SaleItems\" WHERE \"Id\" = 1101")
            .SingleAsync();
        Assert.Equal(100.0000m, merchandiseUnitCostLocal);

        var cancelledAtMatchesDate = await context.Database.SqlQueryRaw<bool>(
            "SELECT (\"CancelledAtUtc\" IS NOT NULL AND \"CancelledAtUtc\" = \"Date\") AS \"Value\" " +
            "FROM \"Sales\" WHERE \"Id\" = 1003")
            .SingleAsync();
        Assert.True(cancelledAtMatchesDate);
    }

    private const string SeedLegacySalesSql = """
        INSERT INTO "Sales" (
            "Id", "Date", "Status", "Subtotal", "TotalUSD", "AppliedRate", "TotalBsS",
            "SubtotalBsS", "RoundingAdjustment", "FinalPaidAmountBsS", "DeliveryStatus",
            "PriceListType", "ClaimAction")
        VALUES
            (1001, TIMESTAMPTZ '2026-11-01 10:00:00+00', 1, 5.0000, 5.00, 40.0000, 200.00, 5.0000, 0.00, 200.00, 0, 'Retail', 0),
            (1002, TIMESTAMPTZ '2026-11-02 12:00:00+00', 1, 21.4000, 21.40, 50.0000, 1070.00, 21.4000, 0.00, 1070.00, 0, 'Retail', 0),
            (1003, TIMESTAMPTZ '2026-11-03 08:30:00+00', 2, 0.0000, 0.00, 50.0000, 0.00, 0.0000, 0.00, 0.00, 0, 'Retail', 0);

        INSERT INTO "SaleItems" (
            "Id", "SaleId", "Quantity", "UnitPrice", "Subtotal", "UnitPriceBsS",
            "SubtotalBsS", "ProductId", "ProductName", "IsCustomPrice", "DeliveredQuantity",
            "UnitCostUSD")
        VALUES
            (1101, 1001, 1.000, 5.0000, 5.0000, 200.0000, 200.0000, 1, 'Mercancia con costo', FALSE, 1.000, 2.50),
            (1102, 1002, 1.000, 21.4000, 21.4000, 1070.0000, 1070.0000, 0, 'Adelanto de Efectivo (Punto de Venta)', FALSE, 1.000, 0.00);

        INSERT INTO "SalePayments" (
            "Id", "SaleId", "PaymentMethodId", "Amount", "AmountBsS", "ExchangeRate",
            "CreatedAt", "ReferenceNumber", "CurrencyCode")
        VALUES
            (1201, 1002, 2, 21.40, 1070.00, 50.0000, TIMESTAMPTZ '2026-11-02 12:00:10+00', 'ADELANTO-20261102120000', 'VES');

        INSERT INTO "CashDrawerSessions" (
            "Id", "OpenedAt", "Status", "OpeningBalanceLocal", "OpeningExchangeRate", "OpeningBalanceUsd")
        VALUES
            (1, TIMESTAMPTZ '2026-11-02 12:00:00+00', 0, 0.00, 50.0000, 0.0000);

        INSERT INTO "CashTransactions" (
            "Id", "SessionId", "TransactionTime", "Type", "Source", "AmountUsd", "ExchangeRate",
            "AmountLocal", "Description", "SaleId", "IsPhysicalCash", "PaymentMethodId", "CurrencyCode")
        VALUES
            (1301, 1, TIMESTAMPTZ '2026-11-02 12:00:20+00', 1, 2, 20.0000, 50.0000, 1000.00, 'Capital de adelanto', NULL, TRUE, 2, 'VES'),
            (1302, 1, TIMESTAMPTZ '2026-11-02 12:00:30+00', 0, 2, 1.4000, 50.0000, 70.00, 'Comision de adelanto', NULL, FALSE, 2, 'VES');
        """;
}
