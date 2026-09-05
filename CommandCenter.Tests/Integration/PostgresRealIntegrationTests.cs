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
            // Omitir si no hay base de datos PostgreSQL real configurada en el entorno local
            return;
        }

        using var context = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
        Assert.NotNull(context);
        bool canConnect = await context.Database.CanConnectAsync();
        Assert.True(canConnect);
    }
}
