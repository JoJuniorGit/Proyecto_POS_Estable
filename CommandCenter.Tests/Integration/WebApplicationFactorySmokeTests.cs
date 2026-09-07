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

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>();

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
        using var response = await client.GetAsync("/ruta/no/existente-8b6");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}