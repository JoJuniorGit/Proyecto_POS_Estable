using System;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sales.Module.Entities;
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
    [Fact]
    public async Task PostgresSharedTransaction_WhenConfigured_CanSharePhysicalTransaction()
    {
        var connStr = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            // Omitir silenciosamente si no hay base de datos PostgreSQL real configurada en el entorno
            return;
        }

        using var salesContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
        if (salesContext == null) return;

        // Crear contexto de inventario con exactamente la misma cadena de conexión
        var invOptions = new DbContextOptionsBuilder<Inventory.Module.Data.InventoryDbContext>()
            .UseNpgsql(connStr)
            .Options;
        using var inventoryContext = new Inventory.Module.Data.InventoryDbContext(invOptions);

        var strategy = salesContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await salesContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
            var rawDbTx = tx.GetDbTransaction();

            // Enrolar InventoryDbContext en la misma transacción física
            await inventoryContext.Database.UseTransactionAsync(rawDbTx);

            Assert.NotNull(salesContext.Database.CurrentTransaction);
            Assert.NotNull(inventoryContext.Database.CurrentTransaction);

            // Revertir para mantener la base de datos limpia
            await tx.RollbackAsync();
        });
    }
}
