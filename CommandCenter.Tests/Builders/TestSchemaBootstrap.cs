using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Sales.Module.Data;

namespace CommandCenter.Tests.Builders;

public static class TestSchemaBootstrap
{
    private const string SeedLockKey = "pos-test-seed-1";

    private static readonly ConcurrentDictionary<string, Lazy<Task>> Bootstraps = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PassGuards = new();

    public static void EnsureSharedSchema(string connStr)
    {
        EnsureSharedSchemaAsync(connStr).GetAwaiter().GetResult();
    }

    public static async Task EnsureSharedSchemaAsync(string connStr, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(connStr);
        var lazy = Bootstraps.GetOrAdd(key, _ => new Lazy<Task>(() => RunBootstrapAsync(connStr, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
        await lazy.Value;
    }

    private static async Task RunBootstrapAsync(string connStr, CancellationToken cancellationToken)
    {
        var passGuard = PassGuards.GetOrAdd(BuildKey(connStr), _ => new SemaphoreSlim(1, 1));
        await passGuard.WaitAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync<SalesDbContext>(connStr, "Users", cancellationToken);
            await CreateSchemaAsync<InventoryDbContext>(connStr, "Products", cancellationToken);

            await using var verifyConn = new NpgsqlConnection(connStr);
            await verifyConn.OpenAsync(cancellationToken);

            await using var salesCheck = new NpgsqlCommand(
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'Users'",
                verifyConn);
            var salesCount = (long)(await salesCheck.ExecuteScalarAsync(cancellationToken))!;
            if (salesCount == 0)
            {
                throw new InvalidOperationException(
                    $"Bootstrap failed: marker table 'Users' missing in database '{new NpgsqlConnectionStringBuilder(connStr).Database}'.");
            }

            await using var invCheck = new NpgsqlCommand(
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'Products'",
                verifyConn);
            var invCount = (long)(await invCheck.ExecuteScalarAsync(cancellationToken))!;
            if (invCount == 0)
            {
                throw new InvalidOperationException(
                    $"Bootstrap failed: marker table 'Products' missing in database '{new NpgsqlConnectionStringBuilder(connStr).Database}'.");
            }
        }
        finally
        {
            passGuard.Release();
        }
    }

    private static async Task CreateSchemaAsync<TContext>(string connStr, string markerTable, CancellationToken cancellationToken)
        where TContext : DbContext
    {
        await using var checkConn = new NpgsqlConnection(connStr);
        await checkConn.OpenAsync(cancellationToken);

        await using var checkCmd = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{markerTable}'",
            checkConn);
        var exists = (long)(await checkCmd.ExecuteScalarAsync(cancellationToken))! > 0;

        if (exists) return;

        var options = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(connStr)
            .Options;

        await using var ctx = (TContext)Activator.CreateInstance(typeof(TContext), options)!;

        var creator = ctx.Database.GetService<IRelationalDatabaseCreator>();
        await creator.CreateTablesAsync(cancellationToken);
    }

    private static string BuildKey(string connStr)
    {
        var builder = new NpgsqlConnectionStringBuilder(connStr);
        return $"{builder.Host}:{builder.Port}/{builder.Database}";
    }
}
