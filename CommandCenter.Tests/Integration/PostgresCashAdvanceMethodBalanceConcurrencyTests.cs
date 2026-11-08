using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.212-WU6: two method-aware advances against the same physical method must serialize through the
/// session advisory lock. The loser must not persist a sale or drawer movement, and the method
/// balance must never become negative.
/// </summary>
[Collection(PostgresRealCollection.Name)]
public class PostgresCashAdvanceMethodBalanceConcurrencyTests
{
    private const decimal Rate = 50m;
    private const int LocalMethodId = 2;
    private const int ReceiverMethodId = 4;

    private static Mock<IMonetaryProfileService> DualProfile()
    {
        var profile = new Mock<IMonetaryProfileService>();
        profile.Setup(p => p.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "USD",
            PrimaryCurrencySymbol = "$",
            SecondaryCurrencyCode = "VES",
            SecondaryCurrencySymbol = "Bs.S",
            IsConfigured = true
        });
        return profile;
    }

    private static Mock<ISystemSettingsService> Settings(decimal percentage = 10m)
    {
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync(percentage.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return settings;
    }

    private static CashAdvanceCoordinator CreateCoordinator(
        SalesDbContext context,
        IMonetaryProfileService profile,
        ISystemSettingsService settings)
    {
        var inventory = new Mock<IInventoryService>();
        inventory.Setup(i => i.GetCashAdvanceProductAsync())
            .ReturnsAsync(new SaleProductInfoDto { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        var drawer = new CashDrawerService(context, profile);
        var sales = new SalesService(
            context,
            inventory.Object,
            Mock.Of<IMediator>(),
            drawer,
            settings,
            monetaryProfileService: profile);

        return new CashAdvanceCoordinator(context, sales, drawer, settings, profile);
    }

    [Fact]
    public async Task TwoAdvancesAgainstSameMethod_SerializeAndNeverOverdraw()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(seedStandardSalesData: true);

        int sessionId;
        await using (var setup = database.CreateSalesDbContext())
        {
            var profile = DualProfile().Object;
            var drawer = new CashDrawerService(setup, profile);
            sessionId = (await drawer.OpenSessionAsync(1000m, Rate, openingBalanceUsd: 20m)).Id;
        }

        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<(bool Succeeded, string? Error)> SubmitAsync()
        {
            await using var context = database.CreateSalesDbContext();
            var profile = DualProfile();
            var settings = Settings();
            var coordinator = CreateCoordinator(context, profile.Object, settings.Object);

            await gate.Task;
            try
            {
                await coordinator.ProcessAsync(
                    sessionId,
                    requestedAmountLocal: 800m,
                    paymentMethodId: ReceiverMethodId,
                    paymentMethodName: "Pago Movil",
                    isTransfer: true,
                    exchangeRate: Rate,
                    cashierId: null,
                    userName: null,
                    cancellationToken: CancellationToken.None,
                    dispensedCashPaymentMethodId: LocalMethodId,
                    dispensedAmount: 800m,
                    receivedPaymentMethodId: ReceiverMethodId);

                return (true, null);
            }
            catch (InvalidOperationException ex)
            {
                return (false, ex.Message);
            }
        }

        var first = SubmitAsync();
        var second = SubmitAsync();
        gate.SetResult(true);
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Succeeded);
        var failure = Assert.Single(results, result => !result.Succeeded);
        Assert.Contains("Existencia insuficiente", failure.Error);

        await using var verify = database.CreateSalesDbContext();
        Assert.Equal(
            2,
            await verify.CashTransactions.CountAsync(movement =>
                movement.SessionId == sessionId
                && movement.Source == CashTransactionSource.CashAdvance));

        var profileForBalances = DualProfile().Object;
        var balanceDrawer = new CashDrawerService(verify, profileForBalances);
        var balances = await balanceDrawer.GetPhysicalMethodBalancesAsync(sessionId);
        var local = Assert.Single(balances, balance => balance.PaymentMethodId == LocalMethodId);
        var usd = Assert.Single(balances, balance => balance.PaymentMethodId == 1);

        Assert.Equal(200m, local.CurrentBalance);
        Assert.Equal(20m, usd.CurrentBalance);
        Assert.True(local.CurrentBalance >= 0m);
    }
}
