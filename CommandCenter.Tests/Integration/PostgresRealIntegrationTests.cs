using System;
using System.Threading.Tasks;
using Backend.API.Jobs;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sales.Module.Data;
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

    [Fact]
    public async Task OutboxProcessorJob_WithRetryingStrategy_ProcessesPendingMessage()
    {
        // 8.9-B4 regression: el SELECT FromSqlRaw (FOR UPDATE SKIP LOCKED) dentro de una
        // transaccion manual REVENTABA en Produccion: NpgsqlRetryingExecutionStrategy no admite
        // transacciones user-initiated fuera del lambda de CreateExecutionStrategy(). El job debe
        // procesar un mensaje Pending end-to-end con la configuracion real (EnableRetryOnFailure).
        var connStr = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
            {
                throw new InvalidOperationException(
                    "TEST_POSTGRES_CONNECTION no está definida en CI. Configure PostgreSQL real para ejecutar PostgresRealIntegrationTests.");
            }

            return;
        }

        Guid seededId;
        using (var seedContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContextWithRetry())
        {
            Assert.NotNull(seedContext);
            var message = new OutboxMessage
            {
                Id = Guid.NewGuid(),
                EventType = "SaleCompleted",
                Payload = "{\"SaleId\":42}",
                Status = "Pending",
                NextRetryUtc = DateTime.UtcNow.AddMinutes(-1),
                CreatedAtUtc = DateTime.UtcNow
            };
            seededId = message.Id;
            seedContext!.OutboxMessages.Add(message);
            await seedContext.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<SalesDbContext>(o =>
            o.UseNpgsql(connStr, npgsql => npgsql.EnableRetryOnFailure(3)));
        using var provider = services.BuildServiceProvider();

        var job = new OutboxProcessorJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<OutboxProcessorJob>>());

        await job.ProcessPendingMessagesAsync();

        using var verifyScope = provider.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<SalesDbContext>();
        var processed = await verifyContext.OutboxMessages
            .AsNoTracking()
            .SingleAsync(m => m.Id == seededId);
        Assert.Equal("Processed", processed.Status);
        Assert.NotNull(processed.ProcessedAtUtc);
        Assert.NotNull(processed.DispatchedAtUtc);
        Assert.Null(processed.ErrorMessage);
    }
}
