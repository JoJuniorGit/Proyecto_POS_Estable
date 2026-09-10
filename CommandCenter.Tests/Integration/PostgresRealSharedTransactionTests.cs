using System;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// Pruebas de integración transaccional compartida con PostgreSQL real (CI/Staging o Contenedor Docker).
/// Valida la coordinación de transacciones compartidas entre SalesDbContext e InventoryDbContext sobre la misma conexión física.
/// Se aísla de las suites unitarias locales mediante el trait [Trait("Category", "RequiresDocker")].
/// </summary>
[Trait("Category", "RequiresDocker")]
public class PostgresRealSharedTransactionTests
{
    private static string? GetConnectionString()
    {
        var connStr = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        return string.IsNullOrWhiteSpace(connStr) ? null : connStr;
    }

    [Fact]
    public async Task PostgresSharedTransaction_WhenConfigured_CanSharePhysicalTransaction()
    {
        var connStr = GetConnectionString();
        if (connStr == null) return;

        // 8.11: patrón correcto para compartir UNA transacción física entre dos DbContexts:
        // ambos contextos deben enlazarse a la MISMA conexión ADO .NET (UseNpgsql(connection)).
        // La versión previa iniciaba la transacción sobre la conexión del SalesDbContext y luego
        // intentaba UseTransactionAsync en un InventoryDbContext con conexión propia (pooling),
        // lo que fallaba con "The specified transaction is not associated with the current connection".
        await using var connection = new Npgsql.NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var rawDbTx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);

        var salesOptions = new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql(connection)
            .Options;
        using var salesContext = new SalesDbContext(salesOptions);

        var invOptions = new DbContextOptionsBuilder<Inventory.Module.Data.InventoryDbContext>()
            .UseNpgsql(connection)
            .Options;
        using var inventoryContext = new Inventory.Module.Data.InventoryDbContext(invOptions);

        salesContext.Database.UseTransaction(rawDbTx);
        inventoryContext.Database.UseTransaction(rawDbTx);

        Assert.NotNull(salesContext.Database.CurrentTransaction);
        Assert.NotNull(inventoryContext.Database.CurrentTransaction);

        // Revertir para mantener la base de datos limpia
        await rawDbTx.RollbackAsync();
    }

    /// <summary>
    /// 8.5-A1: Valida que la verificación de saldo + el INSERT de un egreso físico son atómicos bajo
    /// concurrencia real (advisory lock por sesión). Dos egresos simultáneos que suman más que el saldo
    /// NO deben poder sobregirar la caja (doble egreso / double spend).
    /// </summary>
    [Fact]
    public async Task ConcurrentPhysicalExpenses_CannotOverdrawBalance()
    {
        var connStr = GetConnectionString();
        if (connStr == null) return;

        using var ctx = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
        if (ctx == null) return;

        var strategy = ctx.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            var service = new CashDrawerService(ctx);
            var session = await service.OpenSessionAsync(1000m, 50m);

            // Limpieza previa: cerrar la sesión para no contaminar otros tests del contenedor.
            try
            {
                // 300 + 400 = 700 <= 1000 (OK) -> no debe lanzar
                await service.AddTransactionAsync(session.Id, CashTransactionType.Expense, CashTransactionSource.CashOut, 300m, 6m, 50m, "Egreso A", isPhysicalCash: true);
                await service.AddTransactionAsync(session.Id, CashTransactionType.Expense, CashTransactionSource.CashOut, 400m, 8m, 50m, "Egreso B", isPhysicalCash: true);

                // Quedan 300 Bs.S: dos egresos concurrentes de 200 cada uno = 400 > 300.
                // Al menos UNO debe fallar (el advisory lock serializa check+insert).
                Exception? failureA = null;
                Exception? failureB = null;
                var tA = Task.Run(async () =>
                {
                    try { await service.AddTransactionAsync(session.Id, CashTransactionType.Expense, CashTransactionSource.CashOut, 200m, 4m, 50m, "Egreso conc A", isPhysicalCash: true); }
                    catch (Exception ex) { failureA = ex; }
                });
                var tB = Task.Run(async () =>
                {
                    try { await service.AddTransactionAsync(session.Id, CashTransactionType.Expense, CashTransactionSource.CashOut, 200m, 4m, 50m, "Egreso conc B", isPhysicalCash: true); }
                    catch (Exception ex) { failureB = ex; }
                });
                await Task.WhenAll(tA, tB);

                Assert.True(failureA != null || failureB != null,
                    "Uno de los dos egresos concurrentes debía fallar por saldo insuficiente (TOCTOU).");

                var finalBalance = await service.GetCurrentBalanceLocalAsync(session.Id);
                Assert.False(finalBalance < 0, $"El saldo no debe ser negativo tras la barrera atómica (obtuvo {finalBalance}).");

                await service.CloseSessionAsync(finalBalance, 50m);
            }
            finally
            {
                // Asegurar que no queden sesiones abiertas en el contenedor.
                var active = await service.GetActiveSessionAsync();
                if (active != null)
                {
                    await service.CloseSessionAsync(await service.GetCurrentBalanceLocalAsync(active.Id), 50m);
                }
            }
        });
    }

    [Fact]
    public async Task RecordSaleChangeAsync_WithoutAmbientTransaction_ThrowsInvalidOperationException()
    {
        var connStr = GetConnectionString();
        if (connStr == null) return;

        using var ctx = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
        if (ctx == null) return;

        var service = new CashDrawerService(ctx);
        var session = await service.OpenSessionAsync(1000m, 50m);
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RecordSaleChangeAsync(
                    sessionId: session.Id,
                    changeUsd: 0.4m,
                    changeBsS: 20m,
                    exchangeRate: 50m,
                    description: "Vuelto sin transacción",
                    saleId: 1,
                    cashPaymentMethodId: 1));

            Assert.Contains("transacción compartida del cobro", ex.Message);
        }
        finally
        {
            var active = await service.GetActiveSessionAsync();
            if (active != null)
            {
                await service.CloseSessionAsync(await service.GetCurrentBalanceLocalAsync(active.Id), 50m);
            }
        }
    }
}
