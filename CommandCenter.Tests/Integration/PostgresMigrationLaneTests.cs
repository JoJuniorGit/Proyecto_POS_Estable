using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// Carril PostgreSQL real de MIGRACIONES (Paso 11-13 / 8.187-8.188): aplica la cadena Up completa
/// de las migraciones nuevas sobre una base aislada vacía y ejecuta el Down a la migración previa
/// (orden FK -> columna -> tabla del H2), verificando la precisión de <c>CashTransactions.AmountUsd</c>
/// (numeric(18,4) tras Up; numeric(18,2) tras el Down de la migración de precisión). Sin conexión
/// utilizable el carril falla EXPLÍCITO desde el helper de lane (nunca silent-pass).
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class PostgresMigrationLaneTests
{
    private const string PrecisionMigrationPrevious = "20261018222455_AddPettyCashCancellationAuthorizerRev8188";
    private const string PettyCashBlockPrevious = "20261017120000_AddCreditReceivableCxcRev8187";

    [Fact]
    public async Task SalesMigrations_UpThenDown_EnforceUsdPrecisionAndPettyCashFkOrder()
    {
        // La base nace vacía: la cadena de migraciones de 8.187/8.188 es la que crea todo desde cero.
        await using var database = await IsolatedPostgresDatabase.CreateAsync(applyMigrations: false);
        await using var context = database.CreateSalesDbContext();

        await context.Database.MigrateAsync();

        // Up: las migraciones nuevas quedan aplicadas y la columna del libro de caja conserva 4dp.
        Assert.Equal(4, await ColumnNumericScaleAsync(database, "CashTransactions", "AmountUsd"));
        Assert.True(await RelationExistsAsync(database, "\"PettyCashExpenses\""), "La migración 8.188-T1 debe crear PettyCashExpenses.");
        Assert.True(await RelationExistsAsync(database, "petty_cash_voucher_seq"), "La migración 8.188-T1 debe crear la secuencia del vale.");
        Assert.True(await RelationExistsAsync(database, "\"CreditReturns\""), "La migración 8.187-T3b2 debe crear CreditReturns.");
        Assert.Equal(1, await ColumnCountAsync(database, "DailyClosures", "SessionId"));
        Assert.Equal(1, await ColumnCountAsync(database, "CashTransactions", "PettyCashExpenseId"));

        var migrator = context.GetService<IMigrator>();

        // Down a la migración previa a la precisión: revierte el ancla de sesión del cierre y el
        // numeric(18,4) del libro (Down idempotente de la migración de precisión).
        await migrator.MigrateAsync(PrecisionMigrationPrevious);
        Assert.Equal(2, await ColumnNumericScaleAsync(database, "CashTransactions", "AmountUsd"));
        Assert.Equal(0, await ColumnCountAsync(database, "DailyClosures", "SessionId"));
        Assert.True(await RelationExistsAsync(database, "\"PettyCashExpenses\""));

        // Down del bloque de caja chica (H2): el orden FK -> índice -> columna -> tabla permite
        // desmontar el vínculo de CashTransactions antes de que caiga PettyCashExpenses; si el
        // orden fuera el inverso, PostgreSQL rechazaría el DROP TABLE por la FK vigente.
        await migrator.MigrateAsync(PettyCashBlockPrevious);
        Assert.False(await RelationExistsAsync(database, "PettyCashExpenses"));
        Assert.False(await RelationExistsAsync(database, "petty_cash_voucher_seq"));
        Assert.Equal(0, await ColumnCountAsync(database, "CashTransactions", "PettyCashExpenseId"));
        Assert.Equal(0, await IndexCountAsync(database, "IX_CashTransactions_PettyCashExpenseId"));
        Assert.Equal(2, await ColumnNumericScaleAsync(database, "CashTransactions", "AmountUsd"));
    }

    // ---------------------------------------------------------------- helpers de introspección

    private static async Task<int> ColumnNumericScaleAsync(IsolatedPostgresDatabase database, string table, string column)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE((SELECT numeric_scale FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = @table AND column_name = @column), -1)",
            connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> ColumnCountAsync(IsolatedPostgresDatabase database, string table, string column)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*)::int FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = @table AND column_name = @column",
            connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> IndexCountAsync(IsolatedPostgresDatabase database, string indexName)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*)::int FROM pg_indexes WHERE schemaname = 'public' AND indexname = @index",
            connection);
        command.Parameters.AddWithValue("index", indexName);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>to_regclass exige el nombre tal como vive en pg_class: las tablas PascalCase van entre comillas.</summary>
    private static async Task<bool> RelationExistsAsync(IsolatedPostgresDatabase database, string relation)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass(@relation) IS NOT NULL", connection);
        command.Parameters.AddWithValue("relation", relation);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
