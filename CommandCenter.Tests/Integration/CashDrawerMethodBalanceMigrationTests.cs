using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.212-WU1: the method-balance migration must be safe to retry and create the table,
/// constraints and indexes expected by the model.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class CashDrawerMethodBalanceMigrationTests
{
    [Fact]
    public async Task Migration_IsIdempotent_AndCreatesTheMethodBalanceSchema()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(applyMigrations: false);
        await using var context = database.CreateSalesDbContext();
        await context.Database.MigrateAsync();

        var migrations = context.GetService<IMigrationsAssembly>();
        var key = migrations.Migrations.Keys.Single(key =>
            key.Contains("AddCashDrawerSessionMethodBalancesRev8212", StringComparison.Ordinal));
        var migration = (Migration)Activator.CreateInstance(migrations.Migrations[key].AsType())!;

        foreach (var operation in migration.UpOperations.OfType<SqlOperation>())
        {
            await ExecuteSqlAsync(context, operation.Sql);
            await ExecuteSqlAsync(context, operation.Sql);
        }

        Assert.True(await TableExistsAsync(database, "CashDrawerSessionMethodBalances"));
        Assert.Equal(8, await ColumnCountAsync(database, "CashDrawerSessionMethodBalances"));
        Assert.Equal(1, await IndexCountAsync(database, "IX_CashDrawerSessionMethodBalances_SessionId_PaymentMethodId"));
        Assert.Equal(1, await IndexCountAsync(database, "IX_CashDrawerSessionMethodBalances_PaymentMethodId"));
    }

    private static async Task ExecuteSqlAsync(DbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableExistsAsync(IsolatedPostgresDatabase database, string table)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT to_regclass(@table) IS NOT NULL",
            connection);
        command.Parameters.AddWithValue("table", $"\"{table}\"");
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> ColumnCountAsync(IsolatedPostgresDatabase database, string table)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT COUNT(*)::int FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @table",
            connection);
        command.Parameters.AddWithValue("table", table);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> IndexCountAsync(IsolatedPostgresDatabase database, string index)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT COUNT(*)::int FROM pg_indexes WHERE schemaname = 'public' AND indexname = @index",
            connection);
        command.Parameters.AddWithValue("index", index);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
