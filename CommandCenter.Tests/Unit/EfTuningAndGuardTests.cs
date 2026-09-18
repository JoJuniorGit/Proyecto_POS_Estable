using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using CommandCenter.Tests.TestHelpers;
using Core.Interfaces;
using Moq;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class EfTuningAndGuardTests
{
    [Fact]
    public async Task GetClosureAsync_ReadsWithoutTrackingTheClosureGraph()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            context.DailyClosures.Add(BuildClosure());
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var service = DailyClosureTestHelper.CreateService(context);
            var closure = await service.GetClosureAsync(1);

            Assert.NotNull(closure);
            Assert.Single(closure!.Details);
            Assert.Empty(context.ChangeTracker.Entries());
        }
    }

    [Fact]
    public async Task GetLatestClosureAsync_ReadsWithoutTrackingTheClosureGraph()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            context.DailyClosures.Add(BuildClosure());
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var service = DailyClosureTestHelper.CreateService(context);
            var closure = await service.GetLatestClosureAsync();

            Assert.NotNull(closure);
            Assert.Single(closure!.Details);
            Assert.Empty(context.ChangeTracker.Entries());
        }
    }

    [Fact]
    public async Task GetActiveSessionWithTransactionsAsync_ReadsWithoutTrackingTheDrawerGraph()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            context.CashDrawerSessions.Add(new CashDrawerSession
            {
                OpenedAt = DateTime.UtcNow,
                OpeningBalanceLocal = 1000m,
                OpeningExchangeRate = 50m,
                Status = CashDrawerStatus.Open,
                Transactions = new List<CashTransaction>
                {
                    new()
                    {
                        TransactionTime = DateTime.UtcNow,
                        Type = CashTransactionType.Income,
                        Source = CashTransactionSource.SalePayment,
                        AmountUsd = 10m,
                        ExchangeRate = 50m,
                        AmountLocal = 500m,
                        Description = "Pago en efectivo",
                        IsPhysicalCash = true
                    }
                }
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var service = new CashDrawerService(context);
            var session = await service.GetActiveSessionWithTransactionsAsync();

            Assert.NotNull(session);
            Assert.Single(session!.Transactions);
            Assert.Empty(context.ChangeTracker.Entries());
        }
    }

    [Fact]
    public async Task CloseShift_WhenRequestIsNull_ThrowsArgumentNullException()
    {
        var controller = new ShiftsController(Mock.Of<IDailyClosureService>(), Mock.Of<ICurrentUserService>());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => controller.CloseShiftAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task CloseShift_WhenDeclaredAmountsIsNull_ThrowsArgumentNullException()
    {
        var controller = new ShiftsController(Mock.Of<IDailyClosureService>(), Mock.Of<ICurrentUserService>());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => controller.CloseShiftAsync(new CloseShiftRequest { DeclaredAmounts = null! }, CancellationToken.None));
    }

    private static DailyClosure BuildClosure() => new()
    {
        ClosureDate = DateTime.UtcNow,
        UserId = "Admin",
        ExchangeRate = 50m,
        TotalExpectedBsS = 1000m,
        TotalActualBsS = 1000m,
        TotalDifferenceBsS = 0m,
        Details = new List<ClosureDetail>
        {
            new()
            {
                PaymentMethodId = 1,
                PaymentMethodName = "Efectivo Bs.S",
                ExpectedAmountBsS = 1000m,
                ActualAmountBsS = 1000m,
                DifferenceBsS = 0m
            }
        }
    };
}
