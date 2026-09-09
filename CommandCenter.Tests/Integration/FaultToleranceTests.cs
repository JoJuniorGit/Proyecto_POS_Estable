using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Backend.API.Jobs;
using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Sales.Module.Data;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

[Trait("Category", "RequiresDocker")]
public class FaultToleranceTests
{
    private static string ConnectionString() =>
        Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION") ?? string.Empty;

    private static async Task<string> CreateIsolatedDatabaseAsync(string baseConnString, string dbName)
    {
        var masterCs = new NpgsqlConnectionStringBuilder(baseConnString) { Database = "postgres" }.ConnectionString;
        await using var master = new NpgsqlConnection(masterCs);
        await master.OpenAsync();
        await using var create = master.CreateCommand();
        create.CommandText = $"CREATE DATABASE \"{dbName}\"";
        await create.ExecuteNonQueryAsync();
        return dbName;
    }

    private static async Task DropIsolatedDatabaseAsync(string baseConnString, string dbName)
    {
        var masterCs = new NpgsqlConnectionStringBuilder(baseConnString) { Database = "postgres" }.ConnectionString;
        await using var master = new NpgsqlConnection(masterCs);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)";
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task<IServiceScopeFactory> CreateScopeFactoryAsync(string connString)
    {
        var services = new ServiceCollection();
        services.AddDbContext<SalesDbContext>(o => o.UseNpgsql(connString, npgsql => npgsql.EnableRetryOnFailure(3)));
        var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SalesDbContext>().Database.EnsureCreatedAsync();
        }
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task OutboxCrashRecovery_ReclaimsStaleDispatchingMessage_AndDispatches()
    {
        var connStr = ConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION no definida en CI.");
            return;
        }

        var dbName = "pos_fault_" + Guid.NewGuid().ToString("N")[..8];
        var isolated = new NpgsqlConnectionStringBuilder(connStr) { Database = dbName }.ConnectionString;

        try
        {
            await CreateIsolatedDatabaseAsync(connStr, dbName);
            var scopeFactory = await CreateScopeFactoryAsync(isolated);
            var messageId = Guid.NewGuid();

            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SalesDbContext>();
                db.OutboxMessages.Add(new OutboxMessage
                {
                    Id = messageId,
                    EventType = "SaleCompleted",
                    Payload = "{\"SaleId\":42}",
                    Status = "Dispatching",
                    DispatchedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                    RetryCount = 0,
                    CreatedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            var job = new OutboxProcessorJob(scopeFactory, NullLogger<OutboxProcessorJob>.Instance);
            await job.ProcessPendingMessagesAsync();

            using var check = scopeFactory.CreateScope();
            var final = await check.ServiceProvider.GetRequiredService<SalesDbContext>().OutboxMessages
                .AsNoTracking()
                .SingleAsync(m => m.Id == messageId);
            Assert.Equal("Processed", final.Status);
            Assert.Null(final.ErrorMessage);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(connStr, dbName);
        }
    }

    [Fact]
    public async Task OutboxDrain_UnderBacklog_ProcessesAllMessages()
    {
        var connStr = ConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION no definida en CI.");
            return;
        }

        var dbName = "pos_fault_" + Guid.NewGuid().ToString("N")[..8];
        var isolated = new NpgsqlConnectionStringBuilder(connStr) { Database = dbName }.ConnectionString;
        const int total = 50;

        try
        {
            await CreateIsolatedDatabaseAsync(connStr, dbName);
            var scopeFactory = await CreateScopeFactoryAsync(isolated);

            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SalesDbContext>();
                db.OutboxMessages.AddRange(Enumerable.Range(0, total).Select(i => new OutboxMessage
                {
                    Id = Guid.NewGuid(),
                    EventType = "SaleCompleted",
                    Payload = $"{{\"SaleId\":{i}}}",
                    Status = "Pending",
                    NextRetryUtc = DateTime.UtcNow.AddMinutes(-1),
                    RetryCount = 0,
                    CreatedAtUtc = DateTime.UtcNow
                }));
                await db.SaveChangesAsync();
            }

            var job = new OutboxProcessorJob(scopeFactory, NullLogger<OutboxProcessorJob>.Instance);
            for (var pass = 0; pass < 10; pass++)
            {
                await job.ProcessPendingMessagesAsync();
                using var scope = scopeFactory.CreateScope();
                var remaining = await scope.ServiceProvider.GetRequiredService<SalesDbContext>().OutboxMessages
                    .AsNoTracking()
                    .CountAsync(m => m.Status != "Processed");
                if (remaining == 0) break;
            }

            using var check = scopeFactory.CreateScope();
            var processed = await check.ServiceProvider.GetRequiredService<SalesDbContext>().OutboxMessages
                .AsNoTracking()
                .CountAsync(m => m.Status == "Processed");
            Assert.Equal(total, processed);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(connStr, dbName);
        }
    }

    [Fact]
    public async Task ConcurrentDuplicateSubmission_SameIdempotencyKey_PersistsExactlyOnce()
    {
        var connStr = ConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION no definida en CI.");
            return;
        }

        var dbName = "pos_fault_" + Guid.NewGuid().ToString("N")[..8];
        var isolated = new NpgsqlConnectionStringBuilder(connStr) { Database = dbName }.ConnectionString;
        const string key = "batch-idempotency-key-42";
        const string path = "/api/sales/10/payments/batch";
        var hash = IdempotencyService.ComputePayloadHash("POST", path, Encoding.UTF8.GetBytes("{\"amount\":100}"));

        try
        {
            await CreateIsolatedDatabaseAsync(connStr, dbName);
            var scopeFactory = await CreateScopeFactoryAsync(isolated);

            var attempts = Enumerable.Range(0, 5)
                .Select(_ => AttemptIdempotentCommitAsync(scopeFactory, key, path, hash));
            var committed = await Task.WhenAll(attempts);

            Assert.Equal(1, committed.Count(c => c));

            using var check = scopeFactory.CreateScope();
            var context = check.ServiceProvider.GetRequiredService<SalesDbContext>();
            var stored = await context.IdempotentRequests
                .AsNoTracking()
                .Where(r => r.Key == key && r.RequestPath == path)
                .ToListAsync();
            Assert.Single(stored);

            var service = new IdempotencyService(context);
            var replay = await service.CheckAsync(key, path, hash);
            Assert.True(replay.IsReplay);
            Assert.Equal(200, replay.StoredStatusCode);
        }
        finally
        {
            await DropIsolatedDatabaseAsync(connStr, dbName);
        }
    }

    private static async Task<bool> AttemptIdempotentCommitAsync(
        IServiceScopeFactory scopeFactory, string key, string path, byte[] hash)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SalesDbContext>();
        var service = new IdempotencyService(context);

        var result = await service.CheckAsync(key, path, hash);
        if (!result.IsNew)
        {
            Assert.True(result.IsReplay);
            return false;
        }

        try
        {
            await service.RegisterSuccessAsync(key, path, hash, 200, "{\"status\":\"ok\"}");
            return true;
        }
        catch (DbUpdateException)
        {
            var collision = await service.HandleConcurrentCollisionAsync(key, path, hash);
            Assert.True(collision.IsReplay);
            return false;
        }
    }
}