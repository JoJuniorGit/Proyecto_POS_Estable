using System;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// Pruebas de integración con PostgreSQL real o contenedor Docker (CI/Staging).
/// Permite aislar y filtrar ejecuciones locales ligeras mediante [Trait("Category", "RequiresDocker")].
/// Para omitir en ejecuciones sin Docker/Postgres: dotnet test --filter "Category!=RequiresDocker".
/// </summary>
[Trait("Category", "RequiresDocker")]
public class PostgresRealIntegrationTests
{
    [Fact]
    public async Task PostgresRealConnection_WhenConfigured_ExecutesSuccessfully()
    {
        var connStr = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            // 8.9-L2: TEST_POSTGRES_CONNECTION es un contrato de CI (requiere PostgreSQL real
            // o contenedor). En local la prueba se omite de forma DOCUMENTADA (no silenciosa);
            // en CI (GITHUB_ACTIONS=true) un fallo de configuración se hace visible en lugar
            // de pasar vacío.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
            {
                throw new InvalidOperationException(
                    "TEST_POSTGRES_CONNECTION no está definida en CI. Configure PostgreSQL real para ejecutar PostgresRealIntegrationTests.");
            }

            return;
        }

        using var context = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
        Assert.NotNull(context);
        bool canConnect = await context.Database.CanConnectAsync();
        Assert.True(canConnect);
    }
}
