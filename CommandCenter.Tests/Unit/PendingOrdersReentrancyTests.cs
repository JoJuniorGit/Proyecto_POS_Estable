using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;
using ISalesService = Desktop.Client.Services.ISalesService;

namespace CommandCenter.Tests.Unit;

public class PendingOrdersReentrancyTests
{
    [Fact]
    public async Task EnsureLoaded_WhenAlreadyLoading_DoesNotOverlap_AndDefersASingleReload()
    {
        var (sales, _, _, vm) = CreateViewModel();
        using var vmDisposer = vm;
        var firstTcs = new TaskCompletionSource<(IEnumerable<SaleDto> Items, int TotalCount)>(TaskCreationOptions.RunContinuationsAsynchronously);
        sales.Setup(s => s.GetPendingSalesPagedAsync(200, 0)).Returns(firstTcs.Task);

        var first = vm.EnsureLoadedAsync();
        var second = vm.EnsureLoadedAsync();

        await second;
        sales.Verify(s => s.GetPendingSalesPagedAsync(200, 0), Times.Once);

        firstTcs.SetResult((new[] { CreateSale(1) }, 1));
        await first;

        sales.Verify(s => s.GetPendingSalesPagedAsync(200, 0), Times.Exactly(2));
        Assert.Single(vm.PendingSales);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task LoadMore_WhenRefreshStartsLater_StalePageIsDiscarded()
    {
        var (sales, _, _, vm) = CreateViewModel();
        using var vmDisposer = vm;
        sales.Setup(s => s.GetPendingSalesPagedAsync(200, 0))
            .ReturnsAsync((new[] { CreateSale(1) }, 3));
        await vm.EnsureLoadedAsync();
        Assert.True(vm.HasMore);

        var refreshTcs = new TaskCompletionSource<(IEnumerable<SaleDto> Items, int TotalCount)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadMoreTcs = new TaskCompletionSource<(IEnumerable<SaleDto> Items, int TotalCount)>(TaskCreationOptions.RunContinuationsAsynchronously);
        sales.Setup(s => s.GetPendingSalesPagedAsync(200, 0)).Returns(refreshTcs.Task);
        sales.Setup(s => s.GetPendingSalesPagedAsync(200, 1)).Returns(loadMoreTcs.Task);

        var loadMoreTask = vm.LoadMoreCommand.ExecuteAsync(null);
        var refreshTask = vm.EnsureLoadedAsync();

        refreshTcs.SetResult((new[] { CreateSale(10), CreateSale(11) }, 2));
        await refreshTask;

        loadMoreTcs.SetResult((new[] { CreateSale(2) }, 3));
        await loadMoreTask;

        Assert.Equal(new[] { 10, 11 }, vm.PendingSales.Select(s => s.Id).ToArray());
        Assert.False(vm.IsLoadingMore);
    }

    private static (Mock<ISalesService> Sales, Mock<IExchangeRateService> Rate, Mock<IDialogService> Dialogs, PendingOrdersViewModel Vm) CreateViewModel()
    {
        var sales = new Mock<ISalesService>();
        var rate = new Mock<IExchangeRateService>();
        rate.Setup(r => r.GetCurrentRateAsync()).ReturnsAsync((50m, (DateTime?)null));
        var dialogs = new Mock<IDialogService>();

        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Admin Test", Cedula = "V-1", Role = UserRole.Admin });

        var vm = new PendingOrdersViewModel(
            sales.Object,
            rate.Object,
            new Mock<IPaymentService>().Object,
            dialogs.Object,
            session);

        // Aísla del bus global compartido entre pruebas: un mensaje externo dispararía un
        // EnsureLoadedAsync extra y rompería las aserciones de reentrancia (overlap/defers).
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        return (sales, rate, dialogs, vm);
    }

    private static SaleDto CreateSale(int id) => new()
    {
        Id = id,
        Status = "OnHold",
        Date = DateTime.UtcNow.AddMinutes(-id),
        TotalUSD = 100m,
        TotalBsS = 5000m,
        RemainingBalanceUSD = 100m,
        CustomerId = 1,
        CustomerName = "Cliente Test"
    };
}
