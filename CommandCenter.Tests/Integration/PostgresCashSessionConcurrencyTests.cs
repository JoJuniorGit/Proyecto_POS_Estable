using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.202-H3 (S6): carreras reales contra PostgreSQL entre el cierre de caja y las escrituras que
/// compiten por la misma sesión. La invariante es única: ninguna <see cref="CashTransaction"/> puede
/// quedar asociada a una sesión cerrada fuera de la ventana que el cierre fotografió. Si la
/// escritura gana el advisory lock, su tiempo de transacción precede al <c>ClosedAt</c>; si el
/// cierre gana, la escritura posterior falla con el error de dominio y no persiste nada.
/// </summary>
[Collection(PostgresRealCollection.Name)]
public class PostgresCashSessionConcurrencyTests
{
    [Fact]
    public async Task CloseAndManualWrite_RunInParallel_NeverPersistsAfterClosure()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(seedStandardSalesData: true);

        for (int iteration = 0; iteration < 5; iteration++)
        {
            int sessionId;
            await using (var setup = database.CreateSalesDbContext())
            {
                var drawer = new CashDrawerService(setup);
                sessionId = (await drawer.OpenSessionAsync(1000m, 50m)).Id;
            }

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string description = $"closure-race-{iteration}";

            var closeTask = Task.Run(async () =>
            {
                await gate.Task;
                await using var context = database.CreateSalesDbContext();
                var drawer = new CashDrawerService(context);
                await drawer.CloseSessionAsync(1000m, 50m);
            });

            var writeTask = Task.Run(async () =>
            {
                await gate.Task;
                await using var context = database.CreateSalesDbContext();
                var drawer = new CashDrawerService(context);
                try
                {
                    var dto = await drawer.AddTransactionAsync(
                        sessionId,
                        CashTransactionType.Income,
                        CashTransactionSource.CashIn,
                        100m,
                        2m,
                        50m,
                        description,
                        isPhysicalCash: true,
                        cancellationToken: CancellationToken.None,
                        idempotency: BuildIdempotency($"manual-{iteration}"));

                    return (Succeeded: true, Response: dto, Error: (string?)null);
                }
                catch (InvalidOperationException ex)
                {
                    return (Succeeded: false, Response: (CashTransactionResponseDto?)null, Error: ex.Message);
                }
            });

            gate.SetResult(true);
            await Task.WhenAll(closeTask, writeTask);
            var write = await writeTask;

            await using var verify = database.CreateSalesDbContext();
            var session = await verify.CashDrawerSessions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == sessionId);

            Assert.Equal(CashDrawerStatus.Closed, session.Status);
            Assert.NotNull(session.ClosedAt);

            var movements = await verify.CashTransactions
                .AsNoTracking()
                .Where(movement => movement.SessionId == sessionId && movement.Description == description)
                .ToListAsync();

            if (write.Succeeded)
            {
                Assert.NotNull(write.Response);
                var movement = Assert.Single(movements);
                Assert.True(
                    movement.TransactionTime <= session.ClosedAt,
                    $"La escritura ganó el lock pero quedó fuera del cierre: tx={movement.TransactionTime:O}, closed={session.ClosedAt:O}.");
            }
            else
            {
                Assert.Empty(movements);
                Assert.True(
                    write.Error!.Contains("sesión de caja cerrada", StringComparison.OrdinalIgnoreCase)
                    || write.Error!.Contains("no existe", StringComparison.OrdinalIgnoreCase),
                    $"Error inesperado al escribir contra una sesión cerrada: {write.Error}");
            }
        }
    }

    [Fact]
    public async Task CloseAndCheckout_RunInParallel_NeverCompletesCashSaleAfterClosure()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(seedStandardSalesData: true);

        for (int iteration = 0; iteration < 3; iteration++)
        {
            int sessionId;
            int saleId;
            await using (var setup = database.CreateSalesDbContext())
            {
                var drawer = new CashDrawerService(setup);
                // El checkout puede abrir una sesión nueva si el cierre gana antes de resolverla;
                // la iteración siguiente reutiliza la que quedó abierta en lugar de duplicarla.
                sessionId = (await drawer.GetOrCreateActiveSessionAsync(50m)).Id;

                // Venta pendiente con una línea de $20 y pago cash exacto de 1.000 Bs.S.
                var sale = new SaleBuilder()
                    .WithId(iteration + 1)
                    .WithAppliedRate(50m)
                    .WithStatus(SaleStatus.Pending)
                    .WithItem(101, "Producto carrera", 1m, 20m)
                    .Build();

                // El builder reutiliza Id=1 por ítem; se deja que PostgreSQL asigne uno nuevo
                // para no colisionar entre las iteraciones del mismo test.
                sale.Items[0].Id = 0;

                setup.Sales.Add(sale);
                await setup.SaveChangesAsync();
                saleId = sale.Id;
            }

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var closeTask = Task.Run(async () =>
            {
                await gate.Task;
                await using var context = database.CreateSalesDbContext();
                var drawer = new CashDrawerService(context);
                await drawer.CloseSessionAsync(1000m, 50m);
            });

            var checkoutTask = Task.Run(async () =>
            {
                await gate.Task;
                await using var context = database.CreateSalesDbContext();
                var drawer = new CashDrawerService(context);
                var salesService = CreateSalesService(context, drawer);
                try
                {
                    int invoice = await salesService.CompleteSaleAsync(
                        saleId,
                        50m,
                        new[] { new PaymentInfo(1, 20m, 1000m, null) },
                        cancellationToken: CancellationToken.None);

                    return (Succeeded: true, Invoice: (int?)invoice, Error: (string?)null);
                }
                catch (InvalidOperationException ex)
                {
                    return (Succeeded: false, Invoice: (int?)null, Error: ex.Message);
                }
            });

            gate.SetResult(true);
            await Task.WhenAll(closeTask, checkoutTask);
            var checkout = await checkoutTask;

            await using var verify = database.CreateSalesDbContext();
            var session = await verify.CashDrawerSessions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == sessionId);
            var saleRow = await verify.Sales
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == saleId);

            Assert.Equal(CashDrawerStatus.Closed, session.Status);
            Assert.NotNull(session.ClosedAt);

            var cashMovements = await verify.CashTransactions
                .AsNoTracking()
                .Where(movement => movement.SaleId == saleId)
                .ToListAsync();

            if (checkout.Succeeded)
            {
                Assert.Equal(SaleStatus.Completed, saleRow.Status);
                Assert.Equal(checkout.Invoice, saleRow.InvoiceNumber);
                var movement = Assert.Single(cashMovements);

                if (movement.SessionId == sessionId)
                {
                    // El checkout resolvió la sesión ANTES de que el cierre ganara el lock: su
                    // movimiento debe estar dentro de la ventana que el cierre fotografió.
                    Assert.True(
                        movement.TransactionTime <= session.ClosedAt,
                        $"El cobro ganó el lock pero quedó fuera del cierre: tx={movement.TransactionTime:O}, closed={session.ClosedAt:O}.");
                }
                else
                {
                    // El cierre commiteó antes de que el checkout resolviera la sesión: el alta
                    // automática abre una sesión posterior. Nunca se escribe en la cerrada.
                    var laterSession = await verify.CashDrawerSessions
                        .AsNoTracking()
                        .SingleAsync(candidate => candidate.Id == movement.SessionId);
                    Assert.True(
                        laterSession.OpenedAt >= session.ClosedAt,
                        $"El checkout abrió una sesión antes de que el cierre terminara: opened={laterSession.OpenedAt:O}, closed={session.ClosedAt:O}.");
                    Assert.True(
                        movement.TransactionTime >= laterSession.OpenedAt,
                        $"El movimiento quedó fuera de la sesión nueva: tx={movement.TransactionTime:O}, opened={laterSession.OpenedAt:O}.");
                }
            }
            else
            {
                Assert.NotEqual(SaleStatus.Completed, saleRow.Status);
                Assert.Empty(cashMovements);
                Assert.Contains("sesión de caja cerrada", checkout.Error!, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static CashTransactionIdempotency BuildIdempotency(string key) => new(
        key,
        "/api/cashdrawer/transaction",
        SHA256.HashData(Encoding.UTF8.GetBytes(key)),
        UserId: 1,
        dto => JsonSerializer.Serialize(dto));

    private static SalesService CreateSalesService(SalesDbContext context, ICashDrawerService drawer)
    {
        var inventory = new Mock<IInventoryService>();
        inventory.Setup(service => service.GetSaleProductsByIdsAsync(
                It.IsAny<IEnumerable<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SaleProductInfoDto>());
        inventory.Setup(service => service.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(50m);
        inventory.Setup(service => service.UpdateStockBatchAsync(
                It.IsAny<IEnumerable<StockDeductionRequest>>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        inventory.Setup(service => service.EnrollInTransactionAsync(
                It.IsAny<System.Data.Common.DbTransaction>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        inventory.Setup(service => service.DetachFromTransactionAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(service => service.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        return new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            drawer,
            settings.Object);
    }
}
