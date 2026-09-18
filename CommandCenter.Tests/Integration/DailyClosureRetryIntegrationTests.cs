using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.106-C1: CreateClosureFromCommandAsync se auto-envuelve bajo la execution strategy SOLO cuando no hay
/// transaccion activa ambiante. El flujo de produccion (DailyClosureController/ShiftsController)
/// abre una transaccion Serializable dentro de una strategy externa, por lo que el servicio
/// ejecuta el cuerpo directo y la strategy externa reintenta el bloque completo (8.9-B4).
/// Este test reproduce ese flujo real bajo PostgreSQL y valida que no se dispara el error
/// "retrying strategy does not support user-initiated transactions".
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class DailyClosureRetryIntegrationTests
{
    [Fact]
    public async Task CreateClosureFromCommandAsync_UnderRetryingStrategyAndSerializableTransaction_CreatesClosureCleanly()
    {
        var connStr = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
            {
                throw new InvalidOperationException(
                    "TEST_POSTGRES_CONNECTION no está definida en CI. Configure PostgreSQL real para ejecutar DailyClosureRetryIntegrationTests.");
            }

            return;
        }

        using var context = TestDatabaseFactory.CreatePostgreSqlSalesDbContextWithRetry();
        Assert.NotNull(context);
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context!);

        var puntoDeVenta = await context!.PaymentMethods.SingleAsync(p => p.Name == "Punto de Venta");
        var puntoDeVentaId = puntoDeVenta.Id;

        var saleBase = DateTime.UtcNow.Date.AddDays(1).AddHours(9);
        int idSuffix = Environment.TickCount & 0x3FFFF;
        context.Sales.Add(new Sale { Id = idSuffix + 500, Status = SaleStatus.Completed, Date = saleBase.AddHours(1) });
        context.SalePayments.Add(new SalePayment { SaleId = idSuffix + 500, PaymentMethodId = 1, AmountBsS = 1000m });
        context.Sales.Add(new Sale { Id = idSuffix + 501, Status = SaleStatus.Completed, Date = saleBase.AddHours(2) });
        context.SalePayments.Add(new SalePayment { SaleId = idSuffix + 501, PaymentMethodId = puntoDeVentaId, AmountBsS = 2500m });
        await context.SaveChangesAsync();

        var command = new CreateClosureCommand(
            saleBase.AddHours(3).AddMinutes(30),
            "Admin Auditor",
            "Cierre bajo strategy + Serializable (8.106-C1)",
            new List<DeclaredPaymentAmount>
            {
                new(1, 20m),
                new(puntoDeVentaId, 2500m)
            });

        var closureService = CommandCenter.Tests.TestHelpers.DailyClosureTestHelper.CreateService(context);

        var result = await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            var created = await closureService.CreateClosureFromCommandAsync(command, CancellationToken.None);
            await tx.CommitAsync();
            return created;
        });

        var saved = await context.DailyClosures.AsNoTracking().FirstAsync(dc => dc.Id == result.ClosureId);
        Assert.True(saved.Id > 0);
        Assert.Equal(3500m, saved.TotalExpectedBsS);
        Assert.Equal(3500m, saved.TotalActualBsS);
        Assert.Equal(0m, saved.TotalDifferenceBsS);

        var totalsAfter = await closureService.GetExpectedTotalsByPaymentMethodAsync(saleBase.AddHours(3).AddMinutes(35));
        Assert.All(totalsAfter, t => Assert.Equal(0m, t.ExpectedAmountBsS));
    }
}