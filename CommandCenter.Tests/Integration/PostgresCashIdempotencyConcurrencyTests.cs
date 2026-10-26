using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
/// 8.202-H3 (S6): la misma clave de idempotencia disparada en paralelo debe producir UN solo
/// efecto financiero y la MISMA respuesta. Cubre los dos caminos que persistían la respuesta
/// después del commit antes de 8.202: movimiento manual de caja y adelanto de efectivo.
/// </summary>
[Collection(PostgresRealCollection.Name)]
public class PostgresCashIdempotencyConcurrencyTests
{
    [Fact]
    public async Task SameKeyManualMovements_RunInParallel_PersistOneRowAndReturnSameResponse()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(seedStandardSalesData: true);

        int sessionId;
        await using (var setup = database.CreateSalesDbContext())
        {
            var drawer = new CashDrawerService(setup);
            sessionId = (await drawer.OpenSessionAsync(1000m, 50m)).Id;
        }

        string key = $"SAME-KEY-MANUAL-{Guid.NewGuid():N}";
        string description = $"same-key-manual-{key}";
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<CashTransactionResponseDto> SubmitAsync()
        {
            await using var context = database.CreateSalesDbContext();
            var drawer = new CashDrawerService(context);
            await gate.Task;
            return await drawer.AddTransactionAsync(
                sessionId,
                CashTransactionType.Income,
                CashTransactionSource.CashIn,
                100m,
                2m,
                50m,
                description,
                isPhysicalCash: true,
                cancellationToken: CancellationToken.None,
                idempotency: BuildTransactionIdempotency(key));
        }

        var first = SubmitAsync();
        var second = SubmitAsync();
        gate.SetResult(true);
        var responses = await Task.WhenAll(first, second);

        await using var verify = database.CreateSalesDbContext();
        Assert.Equal(
            1,
            await verify.CashTransactions.CountAsync(movement =>
                movement.SessionId == sessionId && movement.Description == description));
        Assert.Equal(1, await verify.IdempotentRequests.CountAsync(record => record.Key == key));
        Assert.Equal(responses[0].Id, responses[1].Id);
        Assert.Equal(responses[0].AmountLocal, responses[1].AmountLocal);
        Assert.Equal(responses[0].SessionId, responses[1].SessionId);
    }

    [Fact]
    public async Task SameKeyCashAdvances_RunInParallel_CreateOneSaleAndTwoDrawerMovements()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(seedStandardSalesData: true);

        int sessionId;
        await using (var setup = database.CreateSalesDbContext())
        {
            var drawer = new CashDrawerService(setup);
            sessionId = (await drawer.OpenSessionAsync(5000m, 50m)).Id;
        }

        string key = $"SAME-KEY-ADVANCE-{Guid.NewGuid():N}";
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<CashAdvanceResultDto> SubmitAsync()
        {
            await using var context = database.CreateSalesDbContext();
            var inventory = CreateAdvanceInventoryMock();
            var settings = CreateAdvanceSettingsMock();
            var drawer = new CashDrawerService(context);
            var salesService = new SalesService(
                context,
                inventory.Object,
                new Mock<IMediator>().Object,
                drawer,
                settings.Object);
            var coordinator = new CashAdvanceCoordinator(context, salesService, drawer, settings.Object);

            await gate.Task;
            return await coordinator.ProcessAsync(
                sessionId: sessionId,
                requestedAmountLocal: 1000m,
                paymentMethodId: 3,
                paymentMethodName: "Punto de Venta",
                isTransfer: true,
                exchangeRate: 50m,
                cashierId: null,
                userName: null,
                cancellationToken: CancellationToken.None,
                idempotency: BuildAdvanceIdempotency(key));
        }

        var first = SubmitAsync();
        var second = SubmitAsync();
        gate.SetResult(true);
        var responses = await Task.WhenAll(first, second);

        await using var verify = database.CreateSalesDbContext();
        int advanceSales = await verify.SaleItems
            .AsNoTracking()
            .Where(item => item.ProductId == 1)
            .Select(item => item.SaleId)
            .Distinct()
            .CountAsync();

        Assert.Equal(1, advanceSales);
        Assert.Equal(
            2,
            await verify.CashTransactions.CountAsync(movement =>
                movement.SessionId == sessionId
                && movement.Source == CashTransactionSource.CashAdvance));
        Assert.Equal(1, await verify.IdempotentRequests.CountAsync(record => record.Key == key));
        Assert.NotNull(responses[0].RelatedSaleId);
        Assert.Equal(responses[0].RelatedSaleId, responses[1].RelatedSaleId);
        Assert.Equal(responses[0].InvoiceNumber, responses[1].InvoiceNumber);
    }

    private static CashTransactionIdempotency BuildTransactionIdempotency(string key) => new(
        key,
        "/api/cashdrawer/transaction",
        SHA256.HashData(Encoding.UTF8.GetBytes(key)),
        UserId: 1,
        dto => JsonSerializer.Serialize(dto));

    private static CashAdvanceIdempotency BuildAdvanceIdempotency(string key) => new(
        key,
        "/api/cashdrawer/cash-advance",
        SHA256.HashData(Encoding.UTF8.GetBytes(key)),
        UserId: 1,
        dto => JsonSerializer.Serialize(dto));

    private static Mock<IInventoryService> CreateAdvanceInventoryMock()
    {
        var inventory = new Mock<IInventoryService>();
        inventory.Setup(service => service.GetCashAdvanceProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaleProductInfoDto
            {
                Id = 1,
                Name = "Adelanto de Efectivo",
                IsCashAdvance = true,
                PriceRetailUSD = 0m,
                PriceUSD = 0m
            });
        inventory.Setup(service => service.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(50m);
        return inventory;
    }

    private static Mock<ISystemSettingsService> CreateAdvanceSettingsMock()
    {
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(service => service.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((string?)null);
        settings.Setup(service => service.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");
        settings.Setup(service => service.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct))
            .ReturnsAsync("10.0");
        return settings;
    }
}
