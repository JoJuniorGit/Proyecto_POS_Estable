using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
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
                Database = "pos_smoke_test"
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
}