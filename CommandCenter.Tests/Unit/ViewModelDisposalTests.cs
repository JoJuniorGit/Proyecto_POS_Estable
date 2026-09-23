using System;
using System.Reflection;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;
using ISalesService = Desktop.Client.Services.ISalesService;

namespace CommandCenter.Tests.Unit;

public class ViewModelDisposalTests
{
    [Theory]
    [InlineData(typeof(CashDrawerViewModel))]
    [InlineData(typeof(PendingOrdersViewModel))]
    [InlineData(typeof(ExchangeRateViewModel))]
    [InlineData(typeof(CustomerManagementViewModel))]
    [InlineData(typeof(CustomerPickerViewModel))]
    [InlineData(typeof(MainViewModel))]
    public void TargetViewModels_ImplementIDisposable(Type viewModelType)
        => Assert.True(typeof(IDisposable).IsAssignableFrom(viewModelType), $"{viewModelType.Name} debe implementar IDisposable (H-06).");

    [Fact]
    public void MessengerViewModels_Dispose_UnregistersHandlers_AndIsIdempotent()
    {
        var cashDrawer = new CashDrawerViewModel(new Mock<ICashDrawerService>().Object, new Mock<IExchangeRateService>().Object);
        var pendingOrders = CreatePendingOrdersViewModel();
        var exchangeRate = new ExchangeRateViewModel(new Mock<IExchangeRateService>().Object);

        Assert.True(WeakReferenceMessenger.Default.IsRegistered<ShiftClosedMessage>(cashDrawer));
        Assert.True(WeakReferenceMessenger.Default.IsRegistered<OnHoldSalesRefreshMessage>(pendingOrders));
        Assert.True(WeakReferenceMessenger.Default.IsRegistered<TimeZoneChangedMessage>(exchangeRate));

        cashDrawer.Dispose();
        pendingOrders.Dispose();
        exchangeRate.Dispose();

        Assert.False(WeakReferenceMessenger.Default.IsRegistered<ShiftClosedMessage>(cashDrawer));
        Assert.False(WeakReferenceMessenger.Default.IsRegistered<OnHoldSalesRefreshMessage>(pendingOrders));
        Assert.False(WeakReferenceMessenger.Default.IsRegistered<TimeZoneChangedMessage>(exchangeRate));

        cashDrawer.Dispose();
        pendingOrders.Dispose();
        exchangeRate.Dispose();
    }

    [Fact]
    public void MainViewModel_Dispose_IsIdempotent_AndDisposesDisposableChildren()
    {
        var health = new Mock<IHealthPollingService>();
        var cashDrawer = new CashDrawerViewModel(new Mock<ICashDrawerService>().Object, new Mock<IExchangeRateService>().Object);
        var pendingOrders = CreatePendingOrdersViewModel();
        var exchangeRate = new ExchangeRateViewModel(new Mock<IExchangeRateService>().Object);

        var mainVm = new MainViewModel(
            null, null, null, null, null,
            pendingOrders, null, null, exchangeRate, cashDrawer,
            null, null, null,
            health.Object);

        mainVm.Dispose();
        mainVm.Dispose();

        health.Verify(h => h.StopPolling(), Times.Once);
        Assert.False(WeakReferenceMessenger.Default.IsRegistered<ShiftClosedMessage>(cashDrawer));
        Assert.False(WeakReferenceMessenger.Default.IsRegistered<OnHoldSalesRefreshMessage>(pendingOrders));
        Assert.False(WeakReferenceMessenger.Default.IsRegistered<TimeZoneChangedMessage>(exchangeRate));
    }

    [Fact]
    public async Task CustomerPickerViewModel_Dispose_CancelsPendingDebounce_AndClearsSearchCts()
    {
        var sales = new Mock<ISalesService>();
        var vm = new CustomerPickerViewModel(sales.Object);

        vm.SearchQuery = "ana";
        vm.Dispose();

        await Task.Delay(700);

        Assert.Empty(sales.Invocations);
        AssertNullSearchCts(vm);
        vm.Dispose();
    }

    [Fact]
    public async Task CustomerManagementViewModel_Dispose_CancelsPendingDebounce_AndClearsSearchCts()
    {
        var sales = new Mock<ISalesService>();
        var session = new UserSession();
        var vm = new CustomerManagementViewModel(sales.Object, session, new Mock<IDialogService>().Object);

        vm.SearchQuery = "ana";
        vm.Dispose();

        await Task.Delay(700);

        Assert.Empty(sales.Invocations);
        AssertNullSearchCts(vm);
        vm.Dispose();
    }

    private static PendingOrdersViewModel CreatePendingOrdersViewModel()
    {
        var sales = new Mock<ISalesService>();
        var rate = new Mock<IExchangeRateService>();
        return new PendingOrdersViewModel(
            sales.Object,
            rate.Object,
            new Mock<IPaymentService>().Object,
            new Mock<IDialogService>().Object);
    }

    private static void AssertNullSearchCts(object viewModel)
    {
        var field = viewModel.GetType().GetField("_searchCts", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.Null(field!.GetValue(viewModel));
    }
}
