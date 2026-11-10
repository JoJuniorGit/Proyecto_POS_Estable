using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.201 (S1): apertura manual de caja desde WPF con fondo inicial segregado Bs.S/USD.
/// </summary>
public class CashDrawerOpenSessionTests
{
    private const decimal ServiceRate = 50m;

    private static (Mock<ICashDrawerService> Cash, Mock<IExchangeRateService> Rate, Mock<IDialogService> Dialog, Mock<IPaymentService> Payments) CreateMocks()
    {
        var cash = new Mock<ICashDrawerService>();
        cash.Setup(s => s.GetActiveSessionAsync()).ReturnsAsync((CashDrawerSessionDto?)null);
        cash.Setup(s => s.GetHistoryAsync(It.IsAny<int>())).ReturnsAsync(new List<CashTransactionDto>());

        var rate = new Mock<IExchangeRateService>();
        rate.SetupGet(r => r.CurrentRate).Returns(ServiceRate);

        return (cash, rate, new Mock<IDialogService>(), new Mock<IPaymentService>());
    }

    private static CashDrawerViewModel CreateVm(
        Mock<ICashDrawerService> cash,
        Mock<IExchangeRateService> rate,
        Mock<IDialogService> dialog,
        Mock<IPaymentService> payments)
    {
        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Usuario Test", Cedula = "V-1", Role = UserRole.Admin });

        var vm = new CashDrawerViewModel(cash.Object, rate.Object, dialog.Object, payments.Object, session);
        WeakReferenceMessenger.Default.UnregisterAll(vm);
        return vm;
    }

    [Fact]
    public async Task OpenCashDrawer_SendsLocalAndUsdFundsToTheApi()
    {
        var (cash, rate, dialog, payments) = CreateMocks();
        using var vm = CreateVm(cash, rate, dialog, payments);
        vm.OpeningBalanceLocalText = "1.250,50";
        vm.OpeningBalanceUsdText = "20";

        await vm.OpenCashDrawerCommand.ExecuteAsync(null);

        cash.Verify(
            service => service.OpenSessionAsync(1250.50m, ServiceRate, 20m),
            Times.Once);
        Assert.Equal("0", vm.OpeningBalanceLocalText);
        Assert.Equal("0", vm.OpeningBalanceUsdText);
    }

    [Fact]
    public async Task OpenCashDrawer_NegativeUsd_IsRejectedWithoutCallingTheApi()
    {
        var (cash, rate, dialog, payments) = CreateMocks();
        using var vm = CreateVm(cash, rate, dialog, payments);
        vm.OpeningBalanceLocalText = "100";
        vm.OpeningBalanceUsdText = "-1";

        await vm.OpenCashDrawerCommand.ExecuteAsync(null);

        cash.Verify(
            service => service.OpenSessionAsync(
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()),
            Times.Never);
        dialog.Verify(
            service => service.ShowWarning(It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task OpenCashDrawer_WithoutExchangeRate_IsRejectedWithoutCallingTheApi()
    {
        var (cash, rate, dialog, payments) = CreateMocks();
        rate.SetupGet(r => r.CurrentRate).Returns(0m);
        using var vm = CreateVm(cash, rate, dialog, payments);
        vm.OpeningBalanceLocalText = "100";
        vm.OpeningBalanceUsdText = "0";

        await vm.OpenCashDrawerCommand.ExecuteAsync(null);

        cash.Verify(
            service => service.OpenSessionAsync(
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>()),
            Times.Never);
        dialog.Verify(
            service => service.ShowWarning(It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }
}
