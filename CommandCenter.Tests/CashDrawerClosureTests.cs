using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using ServerCashService = Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests;

public class CashDrawerClosureTests
{
    private SalesDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private class MockExchangeRateService : IExchangeRateService
    {
        public decimal CurrentRate { get; set; } = 50.0m;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<(decimal Rate, DateTime? LastUpdated)> GetCurrentRateAsync() => Task.FromResult((50.0m, (DateTime?)DateTime.UtcNow));

        public Task<List<ExchangeRateHistoryDto>> GetHistoryAsync() => Task.FromResult(new List<ExchangeRateHistoryDto>());

        public Task SaveRateAsync(decimal rate)
        {
            CurrentRate = rate;
            return Task.CompletedTask;
        }

        public Task<(decimal Rate, DateTime? LastUpdated)> SyncBcvAsync() => Task.FromResult((50.0m, (DateTime?)DateTime.UtcNow));
    }

    private class MockClientCashDrawerService : ICashDrawerService
    {
        private readonly ServerCashService.CashDrawerService _serverService;

        public MockClientCashDrawerService(ServerCashService.CashDrawerService serverService)
        {
            _serverService = serverService;
        }

        public async Task<CashDrawerSessionDto?> GetActiveSessionAsync()
        {
            var session = await _serverService.GetActiveSessionAsync();
            if (session == null) return null;

            return new CashDrawerSessionDto
            {
                Id = session.Id,
                OpenedAt = session.OpenedAt,
                OpeningBalanceLocal = session.OpeningBalanceLocal,
                OpeningExchangeRate = session.OpeningExchangeRate,
                Status = session.Status == Sales.Module.Entities.CashDrawerStatus.Open ? Desktop.Client.Services.CashDrawerStatus.Open : Desktop.Client.Services.CashDrawerStatus.Closed,
                Transactions = session.Transactions.Select(t => new CashTransactionDto
                {
                    Id = t.Id,
                    Type = (Desktop.Client.Services.CashTransactionType)(int)t.Type,
                    Source = (Desktop.Client.Services.CashTransactionSource)(int)t.Source,
                    AmountLocal = t.AmountLocal,
                    AmountUsd = t.AmountUsd,
                    Description = t.Description,
                    IsPhysicalCash = t.IsPhysicalCash,
                    TransactionTimeLocal = t.TransactionTime
                }).ToList()
            };
        }

        public async Task<decimal> GetCurrentBalanceLocalAsync(int sessionId)
        {
            return await _serverService.GetCurrentBalanceLocalAsync(sessionId);
        }

        public async Task<CashDrawerSessionDto> OpenSessionAsync(decimal openingBalanceLocal, decimal currentExchangeRate)
        {
            var session = await _serverService.OpenSessionAsync(openingBalanceLocal, currentExchangeRate);
            return (await GetActiveSessionAsync())!;
        }

        public async Task<CashDrawerSessionDto> CloseSessionAsync(decimal actualClosingBalanceLocal, decimal currentExchangeRate)
        {
            var session = await _serverService.CloseSessionAsync(actualClosingBalanceLocal, currentExchangeRate);
            return new CashDrawerSessionDto
            {
                Id = session.Id,
                Status = Desktop.Client.Services.CashDrawerStatus.Closed
            };
        }

        public async Task<CashTransactionDto> AddTransactionAsync(int sessionId, decimal amountLocal, Desktop.Client.Services.CashTransactionType type, Desktop.Client.Services.CashTransactionSource source, string description, decimal exchangeRate)
        {
            var tx = await _serverService.AddTransactionAsync(
                sessionId,
                (Sales.Module.Entities.CashTransactionType)(int)type,
                (Sales.Module.Entities.CashTransactionSource)(int)source,
                amountLocal,
                exchangeRate > 0 ? amountLocal / exchangeRate : 0,
                exchangeRate,
                description
            );

            return new CashTransactionDto
            {
                Id = tx.Id,
                AmountLocal = tx.AmountLocal,
                Type = type,
                Source = source,
                Description = tx.Description,
                IsPhysicalCash = tx.IsPhysicalCash
            };
        }

        public Task<CashAdvanceResultClientDto?> ProcessCashAdvanceAsync(
            int sessionId,
            decimal requestedAmountLocal,
            int paymentMethodId,
            string paymentMethodName,
            bool isTransfer,
            decimal exchangeRate,
            int? cashierId = null,
            string? userName = null)
        {
            throw new NotImplementedException();
        }
    }

    [Fact]
    public async Task CashDrawer_AfterClosure_MaintainsExpectedCash_AndResetsIncomeExpenseAccumulatorsToZero()
    {
        using var context = GetInMemoryDbContext();
        var serverService = new ServerCashService.CashDrawerService(context);
        var clientService = new MockClientCashDrawerService(serverService);
        var rateService = new MockExchangeRateService();

        // 1. Open session 1 with 1000 opening balance and add 500 income and 200 expense
        var session1 = await serverService.OpenSessionAsync(1000m, 50m);
        await serverService.AddTransactionAsync(session1.Id, Sales.Module.Entities.CashTransactionType.Income, Sales.Module.Entities.CashTransactionSource.CashIn, 500m, 10m, 50m, "Ingreso previo");
        await serverService.AddTransactionAsync(session1.Id, Sales.Module.Entities.CashTransactionType.Expense, Sales.Module.Entities.CashTransactionSource.CashOut, 200m, 4m, 50m, "Retiro previo");

        var vm = new CashDrawerViewModel(clientService, rateService);
        await vm.LoadSessionAsync();

        // Assert session 1 totals before closure
        Assert.NotNull(vm.ActiveSession);
        Assert.Equal(1300m, vm.CurrentBalanceBsS);
        Assert.Equal(500m, vm.TotalIncomeBsS);
        Assert.Equal(200m, vm.TotalExpenseBsS);
        Assert.Equal("500 Bs.S", vm.FormattedTotalIncomeBsS);
        Assert.Equal("200 Bs.S", vm.FormattedTotalExpenseBsS);

        // 2. Perform closure: close session 1 with 1300 balance and roll over to new session 2 with 1300 opening balance
        decimal closingBalance = await serverService.GetCurrentBalanceLocalAsync(session1.Id);
        await serverService.CloseSessionAsync(closingBalance, 50m);
        await serverService.OpenSessionAsync(closingBalance, 50m);

        // 3. Reload ViewModel session
        await vm.LoadSessionAsync();

        // 4. Assert Expected Cash is MAINTAINED (1,300 Bs.S), but Income and Expense accumulators RESET to 0
        Assert.NotNull(vm.ActiveSession);
        Assert.True(vm.IsSessionActive);
        Assert.Equal(1300m, vm.CurrentBalanceBsS);
        Assert.Equal("1.300", vm.FormattedBalanceBsS);
        Assert.Equal("26,00 $", vm.FormattedBalanceUsd);

        Assert.Equal(0m, vm.TotalIncomeBsS);
        Assert.Equal(0m, vm.TotalExpenseBsS);
        Assert.Equal("0 Bs.S", vm.FormattedTotalIncomeBsS);
        Assert.Equal("0 Bs.S", vm.FormattedTotalExpenseBsS);
    }

    [Fact]
    public async Task RolloverSession_KeepsTheoreticalCash_AndResetsAccumulators_EvenWithZeroDeclaration()
    {
        using var context = GetInMemoryDbContext();
        var serverService = new ServerCashService.CashDrawerService(context);

        // 1. Open session 1 with 1000 opening balance and add 500 income and 200 expense
        var session1 = await serverService.OpenSessionAsync(1000m, 50m);
        await serverService.AddTransactionAsync(session1.Id, Sales.Module.Entities.CashTransactionType.Income, Sales.Module.Entities.CashTransactionSource.CashIn, 500m, 10m, 50m, "Ingreso previo");
        await serverService.AddTransactionAsync(session1.Id, Sales.Module.Entities.CashTransactionType.Expense, Sales.Module.Entities.CashTransactionSource.CashOut, 200m, 4m, 50m, "Retiro previo");

        // 2. Roll over the session: the carried balance must be the THEORETICAL expected cash (1300),
        //    regardless of the declared amounts of the arqueo (which are recorded only in the closure audit).
        await serverService.RolloverSessionAfterClosureAsync(50m);

        // 3. New active session keeps the expected cash (1300) but resets the accumulators to 0
        var newSession = await serverService.GetActiveSessionAsync();
        Assert.NotNull(newSession);
        Assert.Equal(1300m, newSession.OpeningBalanceLocal);
        Assert.Equal(1300m, await serverService.GetCurrentBalanceLocalAsync(newSession.Id));

        decimal income = newSession.Transactions
            .Where(t => t.Type == Sales.Module.Entities.CashTransactionType.Income && t.Source != Sales.Module.Entities.CashTransactionSource.Opening && t.IsPhysicalCash)
            .Sum(t => t.AmountLocal);
        decimal expense = newSession.Transactions
            .Where(t => t.Type == Sales.Module.Entities.CashTransactionType.Expense && t.Source != Sales.Module.Entities.CashTransactionSource.Closing && t.IsPhysicalCash)
            .Sum(t => t.AmountLocal);

        Assert.Equal(0m, income);
        Assert.Equal(0m, expense);
    }

    [Fact]
    public async Task RolloverSession_WithNoActiveSession_DoesNothing()
    {
        using var context = GetInMemoryDbContext();
        var serverService = new ServerCashService.CashDrawerService(context);

        await serverService.RolloverSessionAfterClosureAsync(50m);

        Assert.Null(await serverService.GetActiveSessionAsync());
    }
}
