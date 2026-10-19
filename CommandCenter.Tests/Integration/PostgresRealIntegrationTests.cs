using System;
using System.Threading.Tasks;
using Backend.API.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sales.Module.Data;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// Pruebas de integración con PostgreSQL real sobre bases AISLADAS por test (carril
/// <see cref="PostgresTestLane"/>: TEST_POSTGRES_CONNECTION o fallback Development). Sin conexión
/// utilizable el carril falla EXPLÍCITO con la instrucción de configuración (nunca silent-pass);
/// la base apuntada por la cadena base no se escribe (cada test crea su cc_pg_lane_&lt;guid&gt;).
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class PostgresRealIntegrationTests
{
    [Fact]
    public async Task PostgresRealConnection_WhenConfigured_ExecutesSuccessfully()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync();
        await using var context = database.CreateSalesDbContext();

        Assert.True(await context.Database.CanConnectAsync());
    }

    [Fact]
    public async Task OutboxProcessorJob_WithRetryingStrategy_ProcessesPendingMessage()
    {
        // 8.9-B4 regression: el SELECT FromSqlRaw (FOR UPDATE SKIP LOCKED) dentro de una
        // transaccion manual REVENTABA en Produccion: NpgsqlRetryingExecutionStrategy no admite
        // transacciones user-initiated fuera del lambda de CreateExecutionStrategy(). El job debe
        // procesar un mensaje Pending end-to-end con la configuracion real (EnableRetryOnFailure).
        await using var database = await IsolatedPostgresDatabase.CreateAsync();
        string connStr = database.ConnectionString;

        Guid seededId;
        await using (var seedContext = database.CreateSalesDbContextWithRetry())
        {
            var message = new Core.Entities.OutboxMessage
            {
                Id = Guid.NewGuid(),
                EventType = "SaleCompleted",
                Payload = "{\"SaleId\":42}",
                Status = "Pending",
                NextRetryUtc = DateTime.UtcNow.AddMinutes(-1),
                CreatedAtUtc = DateTime.UtcNow
            };
            seededId = message.Id;
            seedContext.OutboxMessages.Add(message);
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
