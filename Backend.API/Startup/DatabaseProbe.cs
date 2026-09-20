using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Backend.API.Startup;

/// <summary>
/// Validación de conectividad a PostgreSQL para el instalador (--check-db): verifica
/// credenciales, existencia/creación de la base destino y privilegio CREATE sobre el
/// esquema public sin arrancar el host web. Nunca imprime la contraseña.
/// </summary>
public static class DatabaseProbe
{
    /// <summary>
    /// Arma su propia configuración (sin web host) replicando la precedencia del arranque:
    /// appsettings.json, appsettings.{entorno}.json, secrets.json y variables de entorno.
    /// </summary>
    public static async Task<int> RunAsync(TextWriter? output = null)
    {
        var writer = output ?? Console.Out;
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddJsonFile("secrets.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return await RunAsync(configuration.GetConnectionString("DefaultConnection"), writer);
    }

    /// <summary>
    /// Núcleo de la validación. Devuelve 0 si la conexión es utilizable y
    /// <see cref="StartupExitCodes.ConfigurationError"/> (2) ante cualquier fallo.
    /// </summary>
    public static async Task<int> RunAsync(string? connectionString, TextWriter output)
    {
        void Log(string message) => output.WriteLine($"[check-db] {message}");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Log("ERROR: no se encontró la cadena de conexión (ConnectionStrings:DefaultConnection).");
            Log("RESULT: FAIL");
            return StartupExitCodes.ConfigurationError;
        }

        NpgsqlConnectionStringBuilder source;
        try
        {
            source = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex)
        {
            Log($"ERROR: la cadena de conexión no es válida. Detalle: {ex.Message}");
            Log("RESULT: FAIL");
            return StartupExitCodes.ConfigurationError;
        }

        if (string.IsNullOrWhiteSpace(source.Host) || string.IsNullOrWhiteSpace(source.Database))
        {
            Log("ERROR: la cadena de conexión debe especificar Host y Database.");
            Log("RESULT: FAIL");
            return StartupExitCodes.ConfigurationError;
        }

        var maintenanceCsb = new NpgsqlConnectionStringBuilder(source.ConnectionString)
        {
            Database = "postgres",
            Timeout = 10
        };

        try
        {
            await using var maintenance = new NpgsqlConnection(maintenanceCsb.ConnectionString);
            await maintenance.OpenAsync();

            bool databaseExists;
            await using (var existsCmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @db", maintenance))
            {
                existsCmd.Parameters.AddWithValue("db", source.Database);
                databaseExists = await existsCmd.ExecuteScalarAsync() != null;
            }

            if (!databaseExists)
            {
                bool canCreateDatabase;
                await using (var roleCmd = new NpgsqlCommand(
                    "SELECT rolcreatedb OR rolsuper FROM pg_roles WHERE rolname = current_user", maintenance))
                {
                    canCreateDatabase = await roleCmd.ExecuteScalarAsync() is true;
                }

                if (!canCreateDatabase)
                {
                    Log($"ERROR: la base '{source.Database}' no existe y el usuario '{source.Username}' no tiene privilegio para crearla.");
                    Log("RESULT: FAIL");
                    return StartupExitCodes.ConfigurationError;
                }

                Log($"La base '{source.Database}' no existe; el usuario '{source.Username}' puede crearla (rolcreatedb/superuser): OK.");
                Log("RESULT: OK");
                return 0;
            }

            Log($"La base '{source.Database}' existe.");
        }
        catch (PostgresException pg) when (pg.SqlState == "28P01" || pg.SqlState == "28000")
        {
            Log($"ERROR: credenciales inválidas para el usuario '{source.Username}' (verifique la contraseña).");
            Log("RESULT: FAIL");
            return StartupExitCodes.ConfigurationError;
        }
        catch (Exception ex)
        {
            Log($"ERROR: no se pudo conectar al servidor PostgreSQL en '{source.Host}:{source.Port}'. Detalle: {ex.Message}");
            Log("RESULT: FAIL");
            return StartupExitCodes.ConfigurationError;
        }

        var targetCsb = new NpgsqlConnectionStringBuilder(source.ConnectionString)
        {
            Database = source.Database,
            Timeout = 10
        };

        try
        {
            await using var target = new NpgsqlConnection(targetCsb.ConnectionString);
            await target.OpenAsync();

            string serverVersion;
            await using (var versionCmd = new NpgsqlCommand("SELECT version()", target))
            {
                serverVersion = (await versionCmd.ExecuteScalarAsync())?.ToString() ?? "desconocida";
            }

            bool canCreateSchema;
            await using (var privilegeCmd = new NpgsqlCommand(
                "SELECT has_schema_privilege(current_user, 'public', 'CREATE')", target))
            {
                canCreateSchema = await privilegeCmd.ExecuteScalarAsync() is true;
            }

            if (!canCreateSchema)
            {
                Log($"ERROR: el usuario '{source.Username}' no tiene privilegio CREATE sobre el esquema 'public' de '{source.Database}'.");
                Log("RESULT: FAIL");
                return StartupExitCodes.ConfigurationError;
            }

            var shortVersion = serverVersion.StartsWith("PostgreSQL ", StringComparison.OrdinalIgnoreCase)
                ? serverVersion["PostgreSQL ".Length..].Split(' ')[0]
                : serverVersion.Split(' ')[0];
            Log($"Versión del servidor: {serverVersion}");
            Log($"Conexión OK (PostgreSQL {shortVersion}); base '{source.Database}' existe; privilegio CREATE OK.");
        }
        catch (Exception ex)
        {
            Log($"ERROR: no se pudo validar la base '{source.Database}'. Detalle: {ex.Message}");
            Log("RESULT: FAIL");
            return StartupExitCodes.ConfigurationError;
        }

        Log("RESULT: OK");
        return 0;
    }
}
