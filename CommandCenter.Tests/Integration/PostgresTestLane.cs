using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sales.Module.Data;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// Origen de la conexión base resuelta para el carril PostgreSQL real.
/// </summary>
internal enum PostgresLaneConnectionSource
{
    /// <summary>Variable de entorno TEST_POSTGRES_CONNECTION (contrato de CI y de runs locales explícitos).</summary>
    EnvironmentVariable,

    /// <summary>Fallback local de desarrollo: ConnectionStrings:DefaultConnection de appsettings.Development.json.</summary>
    DevelopmentAppSettings
}

/// <summary>
/// Conexión base resuelta del carril: solo sirve para derivar bases AISLADAS por test
/// (<see cref="IsolatedPostgresDatabase"/>); la base apuntada por la cadena nunca se escribe.
/// </summary>
internal sealed record PostgresLaneConnection(string ConnectionString, PostgresLaneConnectionSource Source)
{
    public string DescribeSource() => Source == PostgresLaneConnectionSource.EnvironmentVariable
        ? "TEST_POSTGRES_CONNECTION"
        : "Backend.API/appsettings.Development.json (ConnectionStrings:DefaultConnection)";
}

/// <summary>
/// Carril PostgreSQL real (Paso 11-13 / 8.187-8.188): resuelve la conexión base y expone bases
/// aisladas por test (nombre GUID + limpieza al final) para ejercitar comportamientos
/// Postgres-only (advisory locks, nextval, xmin, migraciones, numeric(18,4)) contra Npgsql real.
///
/// Resolución (una sola vez por proceso):
/// 1. <c>TEST_POSTGRES_CONNECTION</c> si está definida y no vacía.
/// 2. Fallback de desarrollo: <c>Backend.API/appsettings.Development.json</c> (solo si el archivo existe).
/// 3. Sin conexión utilizable: falla EXPLÍCITO (nunca silent-pass) con el mensaje de configuración.
///
/// Ejecución del carril:
///   $env:TEST_POSTGRES_CONNECTION='Host=localhost;Port=5432;Database=CommandCenterTest;Username=postgres;Password=***'
///   dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~Postgres"
/// </summary>
internal static class PostgresTestLane
{
    public const string ConnectionVariableName = "TEST_POSTGRES_CONNECTION";

    private const string SolutionMarkerFile = "CommandCenter.slnx";
    private const string DevelopmentSettingsFolder = "Backend.API";
    private const string DevelopmentSettingsFile = "appsettings.Development.json";
    private const string MaintenanceDatabase = "postgres";
    private const int ProbeTimeoutSeconds = 5;

    private static readonly Lazy<PostgresLaneConnection?> Resolution =
        new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Conexión resuelta o null cuando no hay configuración utilizable (sin fallback disponible).</summary>
    public static PostgresLaneConnection? TryGetResolution() => Resolution.Value;

    /// <summary>Conexión resuelta o falla explícito con la instrucción de configuración (contrato anti silent-pass).</summary>
    public static PostgresLaneConnection RequireResolution()
        => Resolution.Value ?? throw new InvalidOperationException(BuildUnavailableMessage("no hay conexión configurada"));

    private static PostgresLaneConnection? Resolve()
    {
        var candidate = ResolveCandidate();
        if (candidate is null)
        {
            return null;
        }

        VerifyConnects(candidate);
        return candidate;
    }

    private static PostgresLaneConnection? ResolveCandidate()
    {
        string? environmentValue = Environment.GetEnvironmentVariable(ConnectionVariableName);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return new PostgresLaneConnection(environmentValue.Trim(), PostgresLaneConnectionSource.EnvironmentVariable);
        }

        string? fallback = TryReadDevelopmentConnectionString();
        return fallback is null
            ? null
            : new PostgresLaneConnection(fallback, PostgresLaneConnectionSource.DevelopmentAppSettings);
    }

    /// <summary>
    /// Sondeo único de conectividad contra la base de mantenimiento: un fallback de desarrollo
    /// obsoleto (archivo presente pero credencial inválida) falla con el mensaje accionable en
    /// lugar de un error críptico por cada test; un env var inválido también queda trazado.
    /// </summary>
    private static void VerifyConnects(PostgresLaneConnection connection)
    {
        try
        {
            using var probe = new NpgsqlConnection(BuildMaintenanceConnectionString(connection.ConnectionString));
            probe.Open();
            using var command = new NpgsqlCommand("SELECT 1", probe);
            command.ExecuteScalar();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                BuildUnavailableMessage($"{connection.DescribeSource()} no conecta ({ex.Message})"),
                ex);
        }
    }

    private static string BuildUnavailableMessage(string reason)
        => $"Carril PostgreSQL real sin conexión utilizable: {reason}. " +
           "Configure PostgreSQL y exporte TEST_POSTGRES_CONNECTION, por ejemplo: " +
           "$env:TEST_POSTGRES_CONNECTION='Host=localhost;Port=5432;Database=CommandCenterTest;Username=postgres;Password=<clave>'. " +
           "Alternativa local: complete ConnectionStrings:DefaultConnection en Backend.API/appsettings.Development.json. " +
           "Ejecute el carril con: dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter \"FullyQualifiedName~Postgres\". " +
           "Cada test crea su propia base aislada (cc_pg_lane_<guid>) en ese servidor y la elimina al final.";

    private static string? TryReadDevelopmentConnectionString()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SolutionMarkerFile)))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return null;
        }

        string settingsPath = Path.Combine(directory.FullName, DevelopmentSettingsFolder, DevelopmentSettingsFile);
        if (!File.Exists(settingsPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.TryGetProperty("ConnectionStrings", out var connectionStrings)
                && connectionStrings.TryGetProperty("DefaultConnection", out var defaultConnection)
                && defaultConnection.ValueKind == JsonValueKind.String)
            {
                string? value = defaultConnection.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    internal static string BuildMaintenanceConnectionString(string connectionString)
        => new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = MaintenanceDatabase,
            Timeout = ProbeTimeoutSeconds
        }.ConnectionString;

    internal static string BuildDatabaseConnectionString(string connectionString, string databaseName)
        => new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = databaseName,
            Timeout = ProbeTimeoutSeconds
        }.ConnectionString;
}

/// <summary>
/// Base de datos PostgreSQL aislada por test: nombre con GUID sobre el servidor resuelto, migrada
/// con el historial EF real (valida la cadena de migraciones Up) y eliminada con
/// <c>DROP DATABASE ... WITH (FORCE)</c> al final. La base apuntada por la cadena base nunca se toca.
/// </summary>
internal sealed class IsolatedPostgresDatabase : IAsyncDisposable
{
    private const string DatabasePrefix = "cc_pg_lane_";

    private bool _disposed;

    private IsolatedPostgresDatabase(string databaseName, string connectionString)
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    /// <summary>Nuevo contexto EF contra la base aislada (mismo contrato del backend: sin retry explícito).</summary>
    public SalesDbContext CreateSalesDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new SalesDbContext(options);
    }

    /// <summary>Contexto con la execution strategy de Producción/CI (EnableRetryOnFailure(3)).</summary>
    public SalesDbContext CreateSalesDbContextWithRetry()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .Options;
        return new SalesDbContext(options);
    }

    /// <summary>Contexto de Inventory contra la base aislada, con la execution strategy de Producción/CI.</summary>
    public InventoryDbContext CreateInventoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .Options;
        return new InventoryDbContext(options);
    }

    public static async Task<IsolatedPostgresDatabase> CreateAsync(
        bool applyMigrations = true,
        bool seedStandardSalesData = false,
        bool applyInventoryMigrations = false,
        CancellationToken cancellationToken = default)
    {
        var connection = PostgresTestLane.RequireResolution();
        string databaseName = DatabasePrefix + Guid.NewGuid().ToString("N");

        await using (var maintenance = new NpgsqlConnection(PostgresTestLane.BuildMaintenanceConnectionString(connection.ConnectionString)))
        {
            await maintenance.OpenAsync(cancellationToken);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", maintenance);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        var database = new IsolatedPostgresDatabase(
            databaseName,
            PostgresTestLane.BuildDatabaseConnectionString(connection.ConnectionString, databaseName));

        try
        {
            if (applyMigrations || seedStandardSalesData)
            {
                await using var context = database.CreateSalesDbContext();
                if (applyMigrations)
                {
                    await context.Database.MigrateAsync(cancellationToken);
                }

                if (seedStandardSalesData)
                {
                    await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
                    await AlignSerialSequencesAsync(context, cancellationToken);
                }
            }

            if (applyInventoryMigrations)
            {
                await using var inventoryContext = database.CreateInventoryDbContext();
                await inventoryContext.Database.MigrateAsync(cancellationToken);
            }

            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Las conexiones pooled mantienen la base "en uso": se cierran antes del DROP FORCE.
        NpgsqlConnection.ClearAllPools();

        await using var maintenance = new NpgsqlConnection(PostgresTestLane.BuildMaintenanceConnectionString(ConnectionString));
        await maintenance.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)", maintenance);
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// El seed clásico inserta Ids canónicos explícitos (Customers Id=1; PaymentMethods 1..5) y el
    /// HasData de las migraciones hace lo propio: un INSERT explícito NO avanza la secuencia serial,
    /// así que la primera alta EF chocaría con el PK. La base es aislada y serializada por
    /// colección: alinear al máximo visible es seguro y deja el sustrato listo para inserciones EF.
    /// </summary>
    private static async Task AlignSerialSequencesAsync(SalesDbContext context, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlRawAsync(
            "SELECT setval(pg_get_serial_sequence('\"Customers\"', 'Id')::regclass, " +
            "GREATEST((SELECT COALESCE(MAX(\"Id\"), 1) FROM \"Customers\"), 1));",
            cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            "SELECT setval(pg_get_serial_sequence('\"PaymentMethods\"', 'Id')::regclass, " +
            "GREATEST((SELECT COALESCE(MAX(\"Id\"), 1) FROM \"PaymentMethods\"), 1));",
            cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            "SELECT setval(pg_get_serial_sequence('\"Sales\"', 'Id')::regclass, " +
            "GREATEST((SELECT COALESCE(MAX(\"Id\"), 1) FROM \"Sales\"), 1));",
            cancellationToken);
    }
}
