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

        public async Task<List<CashTransactionDto>> GetHistoryAsync(int limit = 300)
        {
            var history = await _serverService.GetHistoryAsync(limit);
            return history.Select(t => new CashTransactionDto
            {
                Id = t.Id,
                Type = (Desktop.Client.Services.CashTransactionType)(int)t.Type,
                Source = (Desktop.Client.Services.CashTransactionSource)(int)t.Source,
                AmountLocal = t.AmountLocal,
                AmountUsd = t.AmountUsd,
                Description = t.Description,
                IsPhysicalCash = t.IsPhysicalCash,
                TransactionTimeLocal = t.TransactionTime
            }).ToList();
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
    public async Task CashDrawer_Closure_PreservesMovementsAndExpectedCash_InActiveSession()
    {
        using var context = GetInMemoryDbContext();
        var serverService = new ServerCashService.CashDrawerService(context);
        var closureService = new ServerCashService.DailyClosureService(context);
        var clientService = new MockClientCashDrawerService(serverService);
        var rateService = new MockExchangeRateService();

        // 1. Open session 1 with 1000 opening balance and add 500 income and 200 expense
        var session1 = await serverService.OpenSessionAsync(1000m, 50m);
        await serverService.AddTransactionAsync(session1.Id, Sales.Module.Entities.CashTransactionType.Income, Sales.Module.Entities.CashTransactionSource.CashIn, 500m, 10m, 50m, "Ingreso previo");
        await serverService.AddTransactionAsync(session1.Id, Sales.Module.Entities.CashTransactionType.Expense, Sales.Module.Entities.CashTransactionSource.CashOut, 200m, 4m, 50m, "Retiro previo");

        var vm = new CashDrawerViewModel(clientService, rateService);
        await vm.LoadSessionAsync();

        // Assert session 1 before closure
        Assert.NotNull(vm.ActiveSession);
        Assert.Equal(1300m, vm.CurrentBalanceBsS);
        Assert.Equal(3, vm.OrderedTransactions.Count);

        // 2. Perform closure (Create DailyClosure)
        var dailyClosure = new DailyClosure
        {
            ClosureDate = DateTime.UtcNow,
            UserId = "Cajero",
            Observation = "Cierre de turno",
            Details = new List<ClosureDetail>
            {
                new ClosureDetail { PaymentMethodId = 1, PaymentMethodName = "Efectivo", ExpectedAmountBsS = 1300m, ActualAmountBsS = 1300m, DifferenceBsS = 0m }
            }
        };
        await closureService.CreateClosureAsync(dailyClosure);

        // 3. Reload ViewModel session
        await vm.LoadSessionAsync();

        // 4. Assert Expected Cash and ALL Movements REMAIN INTACT in the active session
        Assert.NotNull(vm.ActiveSession);
        Assert.True(vm.IsSessionActive);
        Assert.Equal(1300m, vm.CurrentBalanceBsS);
        Assert.Equal("1.300", vm.FormattedBalanceBsS);
        Assert.Equal("26,00 $", vm.FormattedBalanceUsd);

        // Transactions remain 100% visible and intact
        Assert.Equal(3, vm.OrderedTransactions.Count);
        Assert.Contains(vm.OrderedTransactions, t => t.Description == "Ingreso previo");
        Assert.Contains(vm.OrderedTransactions, t => t.Description == "Retiro previo");
    }

    [Fact]
    public async Task CashDrawer_AfterRollover_KeepsPreviousSessionMovementsVisible()
    {
        using var context = GetInMemoryDbContext();
        var serverService = new ServerCashService.CashDrawerService(context);
        var clientService = new MockClientCashDrawerService(serverService);
        var rateService = new MockExchangeRateService();

        // 1. Open session 1 and register movements
        var session1 = await serverService.OpenSessionAsync(1000m, 50m);
        await serverService.AddTransactionAsync(session1.Id, Sales.Module.Entities.CashTransactionType.Income, Sales.Module.Entities.CashTransactionSource.CashIn, 500m, 10m, 50m, "Ingreso sesión 1");
        await serverService.AddTransactionAsync(session1.Id, Sales.Module.Entities.CashTransactionType.Expense, Sales.Module.Entities.CashTransactionSource.CashOut, 200m, 4m, 50m, "Retiro sesión 1");

        var vm = new CashDrawerViewModel(clientService, rateService);
        await vm.LoadSessionAsync();

        // Session 1 visible: apertura + ingreso + retiro
        Assert.Equal(3, vm.OrderedTransactions.Count);
        Assert.Contains(vm.OrderedTransactions, t => t.Description == "Ingreso sesión 1");
        Assert.Contains(vm.OrderedTransactions, t => t.Description == "Retiro sesión 1");

        // 2. Rollover: cierra sesión 1 y abre sesión 2 conservando el saldo teórico
        await serverService.RolloverSessionAfterClosureAsync(50m);

        // 3. Recargar la vista: los movimientos de la sesión cerrada deben SEGUIR visibles
        await vm.LoadSessionAsync();

        Assert.NotNull(vm.ActiveSession);
        Assert.True(vm.IsSessionActive);
        // Sesión 1 (apertura + ingreso + retiro + cierre) + Sesión 2 (apertura)
        Assert.Equal(5, vm.OrderedTransactions.Count);
        Assert.Contains(vm.OrderedTransactions, t => t.Description == "Ingreso sesión 1");
        Assert.Contains(vm.OrderedTransactions, t => t.Description == "Retiro sesión 1");
        Assert.Contains(vm.OrderedTransactions, t => t.Description == "Cierre de caja");
        Assert.Contains(vm.OrderedTransactions, t => t.Description == "Monto de apertura de caja");

        // El saldo esperado se conserva (1000 + 500 - 200 = 1300)
        Assert.Equal(1300m, vm.CurrentBalanceBsS);
        Assert.Equal("1.300", vm.FormattedBalanceBsS);

        // Los acumuladores de la NUEVA sesión arrancan limpios
        Assert.Equal(0m, vm.TotalIncomeBsS);
        Assert.Equal(0m, vm.TotalExpenseBsS);
    }

    [Fact]
    public async Task CashAdvance_WithDecimalRequestedAmount_ThrowsValidationError()
    {
        using var context = GetInMemoryDbContext();
        var serverService = new ServerCashService.CashDrawerService(context);
        var session = await serverService.OpenSessionAsync(1000m, 50m);

        // El efectivo entregado al cliente solo acepta montos enteros
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            serverService.ProcessCashAdvanceAsync(session.Id, 10.50m, 2, "Card", false, 50m));
        Assert.Contains("número entero sin decimales", ex.Message);
    }
}
