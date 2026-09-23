using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class HistoryWindowBoundsTests
{
    [Fact]
    public async Task CashDrawerHistory_IsTrimmedToTheMostRecentWindow_EvenWhenServiceReturnsMore()
    {
        var items = Enumerable.Range(1, 350)
            .Select(i => new CashTransactionDto
            {
                Id = i,
                Description = $"tx{i}",
                TransactionTimeLocal = DateTime.UtcNow.AddSeconds(-i)
            })
            .ToList();

        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer.Setup(s => s.GetActiveSessionAsync()).ReturnsAsync((CashDrawerSessionDto?)null);
        cashDrawer.Setup(s => s.GetHistoryAsync(It.IsAny<int>())).ReturnsAsync(items);
        cashDrawer.Setup(s => s.GetCurrentBalanceLocalAsync(It.IsAny<int>())).ReturnsAsync(0m);

        var rate = new Mock<IExchangeRateService>();
        rate.SetupGet(r => r.CurrentRate).Returns(50m);

        var vm = new CashDrawerViewModel(cashDrawer.Object, rate.Object);
        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);
        using var vmDisposer = vm;
        await vm.LoadSessionAsync();

        Assert.Equal(300, vm.TotalPhysicalTransactions);
        Assert.Equal(12, vm.TotalPages);
        Assert.Equal("tx1", vm.OrderedTransactions.First().Description);
        cashDrawer.Verify(s => s.GetHistoryAsync(300), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExchangeRateHistory_IsTrimmedToTheMostRecentWindow_EvenWhenServiceReturnsMore()
    {
        var items = Enumerable.Range(1, 500)
            .Select(i => new ExchangeRateHistoryDto
            {
                Date = new DateOnly(2026, 1, 1).AddDays(-i),
                Rate = 50m,
                UpdatedAt = DateTime.UtcNow.AddDays(-i)
            })
            .ToList();

        var rate = new Mock<IExchangeRateService>();
        rate.Setup(r => r.GetCurrentRateAsync()).ReturnsAsync((50m, (DateTime?)null));
        rate.Setup(r => r.GetHistoryAsync()).ReturnsAsync(items);

        var vm = new ExchangeRateViewModel(rate.Object);
        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);
        using var vmDisposer = vm;

        await WaitForAsync(() => vm.History.Count > 0);

        Assert.Equal(365, vm.History.Count);
        Assert.Equal(items[0].Date, vm.History.First().Date);
        Assert.Equal(items[364].Date, vm.History.Last().Date);
    }

    [Fact]
    public async Task ExchangeRateLoadAll_SkipsOverlappingRefresh_SoHistoryIsQueriedOnce()
    {
        var rateTcs = new TaskCompletionSource<(decimal Rate, DateTime? LastUpdated)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rate = new Mock<IExchangeRateService>();
        rate.Setup(r => r.GetCurrentRateAsync()).Returns(rateTcs.Task);
        rate.Setup(r => r.GetHistoryAsync()).ReturnsAsync(new List<ExchangeRateHistoryDto>());

        var vm = new ExchangeRateViewModel(rate.Object);
        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);
        using var vmDisposer = vm;

        var overlapping = vm.LoadAllCommand.ExecuteAsync(null);

        rateTcs.SetResult((50m, null));
        await overlapping;
        await WaitForAsync(() => !vm.IsLoading && vm.CurrentRate == 50m);

        rate.Verify(r => r.GetHistoryAsync(), Times.Once);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(25);
        }
    }
}
