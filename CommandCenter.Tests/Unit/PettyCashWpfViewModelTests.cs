using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T4 (Paso 13 / 8.188, S2/S5): acciones de caja chica en la vista de caja. El listado del turno
/// se refresca junto con los saldos físicos por moneda (solo supervisores, por el arqueo ciego),
/// la anulación exige motivo y el cierre de sesión acepta el conteo USD opcional.
/// </summary>
public class PettyCashWpfViewModelTests
{
    private static UserSession LoggedUser(UserRole role)
    {
        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Usuario Test", Cedula = "V-1", Role = role });
        return session;
    }

    private static CashDrawerViewModel NewViewModel(
        Mock<ICashDrawerService> cash,
        Mock<IExchangeRateService> rate,
        Mock<IDialogService> dialog,
        UserSession userSession)
    {
        var vm = new CashDrawerViewModel(cash.Object, rate.Object, dialog.Object, new Mock<IPaymentService>().Object, userSession);
        // Aislamiento del bus global compartido entre pruebas.
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.UnregisterAll(vm);
        return vm;
    }

    private static PettyCashExpenseClientDto NewExpense(int id, PettyCashStatusClient status = PettyCashStatusClient.Active) => new()
    {
        Id = id,
        VoucherNumber = id,
        SessionId = 3,
        Currency = PettyCashCurrencyClient.BsS,
        Category = PettyCashCategoryClient.InsumosLimpieza,
        AmountBsS = 1000m,
        AmountUsd = 20m,
        HistoricalRate = 50m,
        Concept = "Detergente",
        Beneficiary = "Limpieza Total",
        ReferenceNumber = "REF-1",
        CreatedByUserId = 4,
        AuthorizedByUserId = 9,
        Status = status,
        CreatedAtUtc = new DateTime(2026, 10, 18, 14, 0, 0, DateTimeKind.Utc)
    };

    private static Mock<ICashDrawerService> IdleCash()
    {
        var cash = new Mock<ICashDrawerService>();
        cash.Setup(s => s.GetActiveSessionAsync()).ReturnsAsync((CashDrawerSessionDto?)null);
        cash.Setup(s => s.GetHistoryAsync(It.IsAny<int>())).ReturnsAsync(new List<CashTransactionDto>());
        return cash;
    }

    [Fact]
    public async Task LoadPettyCash_LoadsShiftExpensesAndPhysicalBalancesForSupervisor()
    {
        var cash = IdleCash();
        var session = new CashDrawerSessionDto { Id = 3, Status = CashDrawerStatus.Open };
        cash.Setup(s => s.GetActiveSessionAsync()).ReturnsAsync(session);
        cash.Setup(s => s.GetCurrentBalanceLocalAsync(3)).ReturnsAsync(1500m);
        cash.Setup(s => s.GetPettyCashExpensesAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PettyCashExpenseClientDto> { NewExpense(1), NewExpense(2, PettyCashStatusClient.Cancelled) });
        cash.Setup(s => s.GetPhysicalBalancesAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PettyCashPhysicalBalancesClientDto { BsS = 1500.25m, Usd = 32.5000m });

        var vm = NewViewModel(cash, new Mock<IExchangeRateService>(), new Mock<IDialogService>(), LoggedUser(UserRole.Admin));
        await vm.LoadSessionAsync();
        await vm.LoadPettyCashAsync();

        Assert.Equal(2, vm.PettyCashExpenses.Count);
        Assert.True(vm.HasPettyCashExpenses);
        Assert.Equal(1500.25m, vm.PettyCashPhysicalBalanceBsS);
        Assert.Equal(32.5m, vm.PettyCashPhysicalBalanceUsd);
        Assert.False(string.IsNullOrWhiteSpace(vm.PettyCashPhysicalBalanceBsSDisplay));
        Assert.False(string.IsNullOrWhiteSpace(vm.PettyCashPhysicalBalanceUsdDisplay));
        Assert.Equal("VCC-000001", vm.PettyCashExpenses[0].VoucherCode);
        Assert.Equal("Activo", vm.PettyCashExpenses[0].StatusLabel);
        Assert.True(vm.PettyCashExpenses[0].CanCancel);
        Assert.Equal("Anulado", vm.PettyCashExpenses[1].StatusLabel);
        Assert.False(vm.PettyCashExpenses[1].CanCancel);
    }

    [Fact]
    public async Task LoadPettyCash_CashierDoesNotFetchPhysicalBalances()
    {
        var cash = IdleCash();
        cash.Setup(s => s.GetActiveSessionAsync()).ReturnsAsync(new CashDrawerSessionDto { Id = 3, Status = CashDrawerStatus.Open });
        cash.Setup(s => s.GetPettyCashExpensesAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PettyCashExpenseClientDto> { NewExpense(1) });

        var vm = NewViewModel(cash, new Mock<IExchangeRateService>(), new Mock<IDialogService>(), LoggedUser(UserRole.Cashier));
        await vm.LoadSessionAsync();
        await vm.LoadPettyCashAsync();

        cash.Verify(s => s.GetPhysicalBalancesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("—", vm.PettyCashPhysicalBalanceBsSDisplay);
        Assert.Equal("—", vm.PettyCashPhysicalBalanceUsdDisplay);
        Assert.Single(vm.PettyCashExpenses);
    }

    [Fact]
    public async Task RegisterPettyCash_InvokesDialogRefreshesAndOffersVoucher()
    {
        var cash = IdleCash();
        cash.Setup(s => s.GetActiveSessionAsync()).ReturnsAsync(new CashDrawerSessionDto { Id = 3, Status = CashDrawerStatus.Open });
        cash.Setup(s => s.GetCurrentBalanceLocalAsync(3)).ReturnsAsync(1000m);
        cash.Setup(s => s.GetPettyCashExpensesAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PettyCashExpenseClientDto>());
        cash.Setup(s => s.GetPhysicalBalancesAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PettyCashPhysicalBalancesClientDto());
        cash.Setup(s => s.GetPettyCashVoucherAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 1, 2, 3 });

        var created = NewExpense(7);
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowPettyCashExpenseDialogAsync(It.IsAny<PettyCashExpenseDialogViewModel>()))
            .ReturnsAsync(created);
        dialog.Setup(d => d.ShowSuccessDialog(It.IsAny<string>(), "Imprimir Vale")).Returns(true);

        var rate = new Mock<IExchangeRateService>();
        rate.SetupGet(r => r.CurrentRate).Returns(50m);

        var vm = NewViewModel(cash, rate, dialog, LoggedUser(UserRole.Cashier));
        await vm.LoadSessionAsync();

        byte[]? opened = null;
        vm.PettyCashVoucherOpener = (bytes, _) =>
        {
            opened = bytes;
            return Task.CompletedTask;
        };

        await vm.RegisterPettyCashExpenseCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowPettyCashExpenseDialogAsync(It.IsAny<PettyCashExpenseDialogViewModel>()), Times.Once);
        Assert.Equal(new byte[] { 1, 2, 3 }, opened);
    }

    [Fact]
    public async Task CancelPettyCash_OnlyActiveRow_InvokesCancelDialogAndRefreshes()
    {
        var cash = IdleCash();
        cash.Setup(s => s.GetActiveSessionAsync()).ReturnsAsync(new CashDrawerSessionDto { Id = 3, Status = CashDrawerStatus.Open });
        cash.Setup(s => s.GetCurrentBalanceLocalAsync(3)).ReturnsAsync(1000m);
        cash.Setup(s => s.GetPettyCashExpensesAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PettyCashExpenseClientDto> { NewExpense(1) });
        cash.Setup(s => s.GetPhysicalBalancesAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PettyCashPhysicalBalancesClientDto());

        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowPettyCashCancelDialogAsync(It.IsAny<PettyCashCancelDialogViewModel>()))
            .ReturnsAsync(NewExpense(1, PettyCashStatusClient.Cancelled));

        var vm = NewViewModel(cash, new Mock<IExchangeRateService>(), dialog, LoggedUser(UserRole.Manager));
        await vm.LoadSessionAsync();
        await vm.LoadPettyCashAsync();

        await vm.CancelPettyCashExpenseCommand.ExecuteAsync(vm.PettyCashExpenses[0]);
        dialog.Verify(d => d.ShowPettyCashCancelDialogAsync(It.IsAny<PettyCashCancelDialogViewModel>()), Times.Once);

        dialog.Invocations.Clear();
        var cancelledRow = new PettyCashExpenseRowViewModel(NewExpense(2, PettyCashStatusClient.Cancelled));
        await vm.CancelPettyCashExpenseCommand.ExecuteAsync(cancelledRow);
        dialog.Verify(d => d.ShowPettyCashCancelDialogAsync(It.IsAny<PettyCashCancelDialogViewModel>()), Times.Never);
    }

    [Fact]
    public async Task CloseCashDrawer_WithUsd_PersistsClosingBalanceUsd()
    {
        var cash = IdleCash();
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowCashDrawerCloseDialogAsync(50m, 0m, It.IsAny<decimal?>()))
            .ReturnsAsync(new CashDrawerCloseResult(700m, 25.5m));
        cash.Setup(s => s.CloseSessionAsync(700m, 50m, 25.5m))
            .ReturnsAsync(new CashDrawerSessionDto { Id = 3, Status = CashDrawerStatus.Closed });

        var rate = new Mock<IExchangeRateService>();
        rate.SetupGet(r => r.CurrentRate).Returns(50m);

        var vm = NewViewModel(cash, rate, dialog, LoggedUser(UserRole.Admin));
        vm.ActiveSession = new CashDrawerSessionDto { Id = 3, Status = CashDrawerStatus.Open };

        await vm.CloseCashDrawerCommand.ExecuteAsync(null);

        cash.Verify(s => s.CloseSessionAsync(700m, 50m, 25.5m), Times.Once);
    }

    [Fact]
    public async Task CloseCashDrawer_WithoutUsd_KeepsLegacyBehavior()
    {
        var cash = IdleCash();
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowCashDrawerCloseDialogAsync(50m, 0m, It.IsAny<decimal?>()))
            .ReturnsAsync(new CashDrawerCloseResult(700m, null));
        cash.Setup(s => s.CloseSessionAsync(700m, 50m, (decimal?)null))
            .ReturnsAsync(new CashDrawerSessionDto { Id = 3, Status = CashDrawerStatus.Closed });

        var rate = new Mock<IExchangeRateService>();
        rate.SetupGet(r => r.CurrentRate).Returns(50m);

        var vm = NewViewModel(cash, rate, dialog, LoggedUser(UserRole.Manager));
        vm.ActiveSession = new CashDrawerSessionDto { Id = 3, Status = CashDrawerStatus.Open };

        await vm.CloseCashDrawerCommand.ExecuteAsync(null);

        cash.Verify(s => s.CloseSessionAsync(700m, 50m, (decimal?)null), Times.Once);
    }

    [Fact]
    public async Task CloseCashDrawer_CashierCannotCloseSession()
    {
        var cash = IdleCash();
        var dialog = new Mock<IDialogService>();
        var rate = new Mock<IExchangeRateService>();
        rate.SetupGet(r => r.CurrentRate).Returns(50m);

        var vm = NewViewModel(cash, rate, dialog, LoggedUser(UserRole.Cashier));
        vm.ActiveSession = new CashDrawerSessionDto { Id = 3, Status = CashDrawerStatus.Open };

        await vm.CloseCashDrawerCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowCashDrawerCloseDialogAsync(
            It.IsAny<decimal>(),
            It.IsAny<decimal>(),
            It.IsAny<decimal?>()), Times.Never);
        cash.Verify(s => s.CloseSessionAsync(
            It.IsAny<decimal>(),
            It.IsAny<decimal>(),
            It.IsAny<decimal?>()), Times.Never);
    }
}
