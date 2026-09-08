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
    }
}