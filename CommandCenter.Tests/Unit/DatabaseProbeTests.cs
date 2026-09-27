using System;
using System.IO;
using System.Threading.Tasks;
using Backend.API.Startup;
using Npgsql;
using Xunit;

namespace CommandCenter.Tests.Unit;

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
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        using var output = new StringWriter();

        var code = await DatabaseProbe.RunAsync(connectionString, output);

        Assert.Equal(0, code);
        Assert.Contains("RESULT: OK", output.ToString());
    }

    [Fact]
    public async Task RunAsync_WithInvalidPassword_ReturnsConfigurationError()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var invalid = new NpgsqlConnectionStringBuilder(connectionString) { Password = "wrong-for-test" };
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
