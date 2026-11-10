using System;
using System.IO;
using System.Threading.Tasks;
using Backend.API.Startup;
using CommandCenter.Tests.Integration;
using Npgsql;
using Xunit;

namespace CommandCenter.Tests.Unit;

// El probe corre contra bases AISLADAS del carril PostgreSQL real (PostgresTestLane):
// - El caso valido crea una base GUID vacia y la sondea completa (existencia + privilegio CREATE).
// - El caso de credencial invalida reutiliza la conexion resuelta del carril con password erronea.
// Sin conexion utilizable el carril falla EXPLICITO con la instruccion de configuracion
// (nunca silent-pass).
[Collection(PostgresRealCollection.Name)]
public class DatabaseProbeTests
{
    [Fact]
    public async Task RunAsync_WithoutConnectionString_ReturnsConfigurationError()
    {
        using var output = new StringWriter();

        var code = await DatabaseProbe.RunAsync((string?)null, output);

        Assert.Equal(2, code);
        Assert.Contains("RESULT: FAIL", output.ToString());
        Assert.Contains("cadena de conexión", output.ToString());
    }

    [Fact]
    public async Task RunAsync_WithValidConnectionString_ReturnsOk()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(applyMigrations: false);

        using var output = new StringWriter();

        var code = await DatabaseProbe.RunAsync(database.ConnectionString, output);

        Assert.Equal(0, code);
        Assert.Contains("RESULT: OK", output.ToString());
    }

    [Fact]
    public async Task RunAsync_WithInvalidCredentials_ReturnsConfigurationError()
    {
        var connection = PostgresTestLane.RequireResolution();

        // 8.202-H4: un usuario inexistente falla tanto con `trust` (rol inexistente → 28000)
        // como con scram/md5 (28P01). Una contraseña incorrecta NO falla en un clúster trust,
        // que es el que usa el carril aislado; el usuario inválido mantiene el test determinista.
        var invalid = new NpgsqlConnectionStringBuilder(connection.ConnectionString)
        {
            Username = "invalid-pos-test-user",
            Password = "wrong-for-test"
        };
        using var output = new StringWriter();

        var code = await DatabaseProbe.RunAsync(invalid.ConnectionString, output);

        Assert.Equal(2, code);
        Assert.Contains("RESULT: FAIL", output.ToString());
        Assert.Contains("credenciales inválidas", output.ToString());
    }

    [Fact]
    public void StartupDiagnostics_DefaultState_AndRecordConvergence()
    {
        // El estado es estático por proceso: un smoke gated del pipeline puede haberlo avanzado
        // antes; se verifica el default si está intacto y en todos los casos se restaura.
        var original = StartupDiagnostics.GetConvergence();
        if (original.Status == "not-run")
        {
            Assert.Equal(("not-run", (string?)null, (string?)null), original);
        }

        try
        {
            StartupDiagnostics.RecordConvergence("ok", "v1-2026-09-19", null);

            var (status, version, error) = StartupDiagnostics.GetConvergence();

            Assert.Equal("ok", status);
            Assert.Equal("v1-2026-09-19", version);
            Assert.Null(error);
        }
        finally
        {
            StartupDiagnostics.RecordConvergence(original.Status, original.Version, original.Error);
        }
    }
}
