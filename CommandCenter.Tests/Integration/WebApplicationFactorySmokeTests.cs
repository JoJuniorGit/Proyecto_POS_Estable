using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.6-B6: ranura el pipeline HTTP real (TestServer de WebApplicationFactory) a través del
/// entrypoint de producción, verificando que el stack middleware + DI arrancan de punta a punta.
/// Sigue el patrón silent-pass de los PostgresReal*: sin PostgreSQL configurada se omite para
/// no exigir una BD local en cualquier máquina.
/// </summary>
public class WebApplicationFactorySmokeTests
{
    // 8.14-N3: nombre de la BD smoke único por ejecución (sufijo desde env SMOKE_DB_SUFFIX,
    // p. ej. el run id de CI) para evitar colisiones entre pipelines concurrentes contra el
    // mismo servidor PostgreSQL. Default 'pos_smoke_test' mantiene compatibilidad local.
    internal static string SmokeDatabaseName =>
        Environment.GetEnvironmentVariable("SMOKE_DB_SUFFIX") is { Length: > 0 } suffix
            ? $"pos_smoke_{suffix}"
            : "pos_smoke_test";

    // IMP-1 (ANEXO 8.67-B1): BD de migración "desde cero" única por ejecución. El smoke
    // MigratedSchema_MatchesModel_OnRealDatabase migra sobre una BD existente; el fallo
    // 8.65 (42703 IsDeleted) solo se materializaba al migrar una BD VACÍA. Este nombre
    // aislado garantiza que el flujo de prueba no colisione con la BD smoke principal.
    internal static string EmptyDatabaseName =>
        Environment.GetEnvironmentVariable("SMOKE_DB_SUFFIX") is { Length: > 0 } suffix
            ? $"pos_zero_{suffix}"
            : "pos_zero_test";

    private static bool PostgresConfiguredForPipeline() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection"));

    private static WebApplicationFactory<Program> CreateFactory()
    {
        // 8.11: el pipeline real (Program.cs) arranca contra la BD de SU SMOKE aislada
        // (pos_smoke_test) derivada de TEST_POSTGRES_CONNECTION. Antes apuntaba al
        // CommandCenterDb de desarrollo (appsettings.Development) -> en CI/auth real el
        // host fallaba al arrancar ("The server has not been started"). Aislar la BD evita
        // además colisionar con EnsureCreated/EnsureDeleted de los tests PostgresReal*.
        var connStr = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (!string.IsNullOrWhiteSpace(connStr))
        {
            var csb = new Npgsql.NpgsqlConnectionStringBuilder(connStr)
            {
                Database = SmokeDatabaseName
            };
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", csb.ConnectionString);
        }

        // Program.cs evalúa la política del Admin seed leyendo la env var ASPNETCORE_ENVIRONMENT
        // (por defecto "Production" -> fail-fast B9). Los smoke arrancan el pipeline real en modo
        // Development (igual que la API local), donde el seed del Admin se omite sin romper.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        return new WebApplicationFactory<Program>();
    }

    [Fact]
    public async Task OpenApiJson_IsServedThroughTheRealPipeline()
    {
        if (!PostgresConfiguredForPipeline()) return;

        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.True(
            response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NotFound,
            $"El pipeline debe responder (200 en Development / 404 como mínimo), se obtuvo {(int)response.StatusCode}");
    }

    [Fact]
    public async Task UnhandledRoute_Returns404_ThroughGlobalPipeline()
    {
        if (!PostgresConfiguredForPipeline()) return;

        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/ruta/no/existente-8b6");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MigratedSchema_MatchesModel_OnRealDatabase()
    {
        // 8.12-M1: el smoke test debe validar el ESQUEMA, no solo responder rutas. Ejecuta
        // MigrateAsync sobre la BD smoke aislada y verifica con queries mínimas que las 8
        // migraciones reactivadas en 8.12-B1/B2/B3 (antes huérfanas) se materializaron.
        if (!PostgresConfiguredForPipeline()) return;

        var connStr = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(connStr)
        {
            Database = SmokeDatabaseName
        };
        var connectionString = csb.ConnectionString;

        var salesOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Sales.Module.Data.SalesDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var inventoryOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Inventory.Module.Data.InventoryDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var salesDb = new Sales.Module.Data.SalesDbContext(salesOptions);
        await using var inventoryDb = new Inventory.Module.Data.InventoryDbContext(inventoryOptions);

        await salesDb.Database.MigrateAsync();
        await inventoryDb.Database.MigrateAsync();

        // B2: Subtotal/UnitPrice deben ser numeric(18,4) (la migración ExpandSubtotalPrecisionTo4Decimals corrió).
        var subtotalPrecision = await salesDb.Database.SqlQueryRaw<int>(
            "SELECT COALESCE(MAX(CASE WHEN column_name = 'Subtotal' THEN numeric_scale END), 0)::int AS \"Value\" " +
            "FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'SaleItems'")
            .FirstOrDefaultAsync();
        Assert.True(subtotalPrecision >= 4, $"SaleItems.Subtotal debe tener scale >= 4 (B2), se obtuvo {subtotalPrecision}");

        // M3/B1: DisplayOrder debe existir en PaymentMethods.
        var displayOrderExists = await salesDb.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'PaymentMethods' AND column_name = 'DisplayOrder'")
            .FirstOrDefaultAsync();
        Assert.Equal(1, displayOrderExists);

        // B1: la columna legacy IsService ya NO debe existir en Products (RemoveIsServiceFromProducts).
        var isServiceCount = await inventoryDb.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'IsService'")
            .FirstOrDefaultAsync();
        Assert.Equal(0, isServiceCount);

        // Índice case-insensitive de Username (AddCaseInsensitiveUsernameIndexAndBackfill).
        var usernameIndex = await salesDb.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'ix_users_username_lower'")
            .FirstOrDefaultAsync();
        Assert.Equal(1, usernameIndex);

        // Secuencia de facturación (convergencia legacy / migración base).
        var invoiceSeq = await salesDb.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM pg_class WHERE relkind = 'S' AND relname = 'factura_number_seq'")
            .FirstOrDefaultAsync();
        Assert.Equal(1, invoiceSeq);

        // 8.20-C01: SaleId (idempotencia de stock H03) e índice deben materializarse en BD
        // real vía MigrateAsync. Antes la migración era huérfana (sin [Migration]) y solo
        // existía en BDs creadas con EnsureCreated.
        var stockMoveSaleId = await inventoryDb.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'SaleId'")
            .FirstOrDefaultAsync();
        Assert.Equal(1, stockMoveSaleId);

        var archiveSaleId = await inventoryDb.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'StockMovements_Archive' AND column_name = 'SaleId'")
            .FirstOrDefaultAsync();
        Assert.Equal(1, archiveSaleId);

        var saleIdIndex = await inventoryDb.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'IX_StockMovements_SaleId'")
            .FirstOrDefaultAsync();
        Assert.Equal(1, saleIdIndex);

        // 8.25-E1: tras adoptar el techo de tasa BCV a 4 decimales, las columnas de tasa
        // snapshot deben tener scale >= 4 (migracion ExpandExchangeRateColumnsTo4Decimals).
        var appliedRateScale = await salesDb.Database.SqlQueryRaw<int>(
            "SELECT COALESCE(MAX(CASE WHEN column_name = 'AppliedRate' THEN numeric_scale END), 0)::int AS \"Value\" " +
            "FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Sales'")
            .FirstOrDefaultAsync();
        Assert.True(appliedRateScale >= 4, $"Sales.AppliedRate debe tener scale >= 4 (E1), se obtuvo {appliedRateScale}");

        var paymentRateScale = await salesDb.Database.SqlQueryRaw<int>(
            "SELECT COALESCE(MAX(CASE WHEN column_name = 'ExchangeRate' THEN numeric_scale END), 0)::int AS \"Value\" " +
            "FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'SalePayments'")
            .FirstOrDefaultAsync();
        Assert.True(paymentRateScale >= 4, $"SalePayments.ExchangeRate debe tener scale >= 4 (E1), se obtuvo {paymentRateScale}");
    }

    [Fact]
    public async Task MigratedSchema_FromEmptyDatabase_AppliesAllMigrationsCleanly()
    {
        // IMP-1 (ANEXO 8.67-B1): smoke de migración "desde cero". El smoke existente
        // (MigratedSchema_MatchesModel_OnRealDatabase) migra sobre la BD smoke ya poblada;
        // el incidente 8.65 (42703 IsDeleted) solo aparecía al migrar una BD VACÍA, donde
        // EF aplica las migraciones en orden cronológico estricto. Este test crea una BD
        // temporal vacía, ejecuta MigrateAsync de TODOS los contextos y verifica que el
        // esquema se materializa completo, para luego descartar la BD.
        if (!PostgresConfiguredForPipeline()) return;

        var connStr = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connStr)) return;

        var csb = new Npgsql.NpgsqlConnectionStringBuilder(connStr) { Database = EmptyDatabaseName };
        var zeroConnectionString = csb.ConnectionString;

        // Conexión administrativa (sin BD explícita) para crear/descartar la BD temporal.
        var adminCsb = new Npgsql.NpgsqlConnectionStringBuilder(connStr) { Database = "postgres" };

        try
        {
            await using (var admin = new Npgsql.NpgsqlConnection(adminCsb.ConnectionString))
            {
                await admin.OpenAsync();
                await using var dropOld = new Npgsql.NpgsqlCommand(
                    $"DROP DATABASE IF EXISTS \"{EmptyDatabaseName}\" WITH (FORCE)", admin);
                await dropOld.ExecuteNonQueryAsync();
                await using var create = new Npgsql.NpgsqlCommand(
                    $"CREATE DATABASE \"{EmptyDatabaseName}\"", admin);
                await create.ExecuteNonQueryAsync();
            }

            var salesOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Sales.Module.Data.SalesDbContext>()
                .UseNpgsql(zeroConnectionString)
                .Options;
            var inventoryOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Inventory.Module.Data.InventoryDbContext>()
                .UseNpgsql(zeroConnectionString)
                .Options;

            await using var salesDb = new Sales.Module.Data.SalesDbContext(salesOptions);
            await using var inventoryDb = new Inventory.Module.Data.InventoryDbContext(inventoryOptions);

            await salesDb.Database.MigrateAsync();
            await inventoryDb.Database.MigrateAsync();

            // 8.65: la cadena completa de migraciones DEBE incluir IsDeleted en
            // PaymentMethods y Products (histórico: la de 09/08 referenciaba la columna
            // antes de su creación en 05/09; el bug solo era visible desde BD vacía).
            var pmIsDeleted = await salesDb.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.columns " +
                "WHERE table_schema = 'public' AND table_name = 'PaymentMethods' AND column_name = 'IsDeleted'")
                .FirstOrDefaultAsync();
            Assert.Equal(1, pmIsDeleted);

            var productIsDeleted = await inventoryDb.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.columns " +
                "WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'IsDeleted'")
                .FirstOrDefaultAsync();
            Assert.Equal(1, productIsDeleted);

            // Sanidad básica: el esquema migrado contiene las tablas críticas de ambos contextos.
            var salesTables = await salesDb.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables " +
                "WHERE table_schema = 'public' AND table_name IN ('Sales', 'SaleItems', 'SalePayments', 'PaymentMethods', 'Customers')")
                .FirstOrDefaultAsync();
            Assert.Equal(5, salesTables);

            var inventoryTables = await inventoryDb.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables " +
                "WHERE table_schema = 'public' AND table_name IN ('Products', 'StockMovements', 'StockMovements_Archive')")
                .FirstOrDefaultAsync();
            Assert.Equal(3, inventoryTables);

            var invoiceSeq = await salesDb.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM pg_class WHERE relkind = 'S' AND relname = 'factura_number_seq'")
                .FirstOrDefaultAsync();
            Assert.Equal(1, invoiceSeq);
        }
        finally
        {
            await using var admin = new Npgsql.NpgsqlConnection(adminCsb.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new Npgsql.NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{EmptyDatabaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}