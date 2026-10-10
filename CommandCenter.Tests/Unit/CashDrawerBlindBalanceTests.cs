using System;
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

// 8.153 (SEC-06, REQ-BCB-03/04): arqueo ciego del cliente WPF. Un no-supervisor no debe disparar
// el fetch del saldo teórico ni ver un "0" engañoso; el diálogo de adelanto opera sin tope local
// (el servidor valida) y los supervisores conservan el comportamiento vigente.
public class CashDrawerBlindBalanceTests
{
    private const decimal ServiceBalance = 1300m;
    private const decimal ServiceRate = 50m;

    private static (Mock<ICashDrawerService> Cash, Mock<IExchangeRateService> Rate, Mock<IDialogService> Dialog, Mock<IPaymentService> Payments) CreateMocks()
    {
        var cash = new Mock<ICashDrawerService>();
        cash.Setup(s => s.GetActiveSessionAsync()).ReturnsAsync(new CashDrawerSessionDto { Id = 1, Status = CashDrawerStatus.Open });
        cash.Setup(s => s.GetHistoryAsync(It.IsAny<int>())).ReturnsAsync(new List<CashTransactionDto>());
        cash.Setup(s => s.GetCurrentBalanceLocalAsync(It.IsAny<int>())).ReturnsAsync(ServiceBalance);

        var rate = new Mock<IExchangeRateService>();
        rate.SetupGet(r => r.CurrentRate).Returns(ServiceRate);

        return (cash, rate, new Mock<IDialogService>(), new Mock<IPaymentService>());
    }

    private static CashDrawerViewModel CreateVm(
        Mock<ICashDrawerService> cash,
        Mock<IExchangeRateService> rate,
        Mock<IDialogService> dialog,
        Mock<IPaymentService> payments,
        UserSession? session)
    {
        var vm = new CashDrawerViewModel(cash.Object, rate.Object, dialog.Object, payments.Object, session);
        // Aislamiento del bus global compartido entre pruebas (mismo patrón que RBAC).
        WeakReferenceMessenger.Default.UnregisterAll(vm);
        return vm;
    }

    private static UserSession CreateLoggedSession(UserRole role)
    {
        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Usuario Test", Cedula = "V-1", Role = role });
        return session;
    }

    [Fact]
    public async Task LoadSessionAsync_WithCashierSession_DoesNotFetchTheoreticalBalance_AndShowsNeutralRepresentation()
    {
        var (cash, rate, dialog, payments) = CreateMocks();
        var session = new UserSession();
        using var vm = CreateVm(cash, rate, dialog, payments, session);
        // El usuario se registra después del ctor para suprimir el load inicial y que el conteo
        // del fetch sea determinista.
        session.SetUser(new UserDto { Id = 1, Name = "Usuario Test", Cedula = "V-1", Role = UserRole.Cashier });

        await vm.LoadSessionAsync();

        Assert.NotNull(vm.ActiveSession);
        cash.Verify(s => s.GetCurrentBalanceLocalAsync(It.IsAny<int>()), Times.Never);
        Assert.Equal("—", vm.FormattedBalanceBsS);
        Assert.Equal(string.Empty, vm.FormattedBalanceUsd);
    }

    [Fact]
    public async Task LoadSessionAsync_WithoutSession_FailsClosed_WithoutFetching_AndShowsNeutralRepresentation()
    {
        var (cash, rate, dialog, payments) = CreateMocks();
        using var vm = CreateVm(cash, rate, dialog, payments, null);

        await vm.LoadSessionAsync();

        cash.Verify(s => s.GetCurrentBalanceLocalAsync(It.IsAny<int>()), Times.Never);
        Assert.Equal("—", vm.FormattedBalanceBsS);
        Assert.Equal(string.Empty, vm.FormattedBalanceUsd);
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Manager)]
    public async Task LoadSessionAsync_WithSupervisorSession_FetchesAndFormatsBalanceAsToday(UserRole role)
    {
        var (cash, rate, dialog, payments) = CreateMocks();
        var session = new UserSession();
        using var vm = CreateVm(cash, rate, dialog, payments, session);
        session.SetUser(new UserDto { Id = 1, Name = "Usuario Test", Cedula = "V-1", Role = role });

        await vm.LoadSessionAsync();

        cash.Verify(s => s.GetCurrentBalanceLocalAsync(1), Times.Once);
        Assert.Equal(ServiceBalance, vm.CurrentBalanceBsS);
        Assert.Equal("1.300", vm.FormattedBalanceBsS);
        Assert.Equal("26,00 $", vm.FormattedBalanceUsd);
    }

    [Fact]
    public async Task ProcessCashAdvance_WithCashierSession_PassesNullToDialog_AndDoesNotFetch()
    {
        var (cash, rate, dialog, payments) = CreateMocks();
        decimal? capturedAvailableCash = 42m; // centinela: el callback debe sobrescribirlo
        dialog.Setup(d => d.ShowCashAdvanceRegisterDialogAsync(It.IsAny<List<PaymentMethodDto>>(), It.IsAny<decimal?>()))
            .Callback<List<PaymentMethodDto>, decimal?>((_, available) => capturedAvailableCash = available)
            .ReturnsAsync(((bool success, decimal requestedAmount, decimal commissionAmount, int paymentMethodId, string paymentMethodName, bool isTransfer)?)null);

        var session = new UserSession();
        using var vm = CreateVm(cash, rate, dialog, payments, session);
        session.SetUser(new UserDto { Id = 1, Name = "Usuario Test", Cedula = "V-1", Role = UserRole.Cashier });
        await vm.LoadSessionAsync();

        await vm.ProcessCashAdvanceCommand.ExecuteAsync(null);

        cash.Verify(s => s.GetCurrentBalanceLocalAsync(It.IsAny<int>()), Times.Never);
        Assert.Null(capturedAvailableCash);
    }

    [Fact]
    public async Task ProcessCashAdvance_WithAdminSession_PassesAvailableCashToDialog()
    {
        var (cash, rate, dialog, payments) = CreateMocks();
        decimal? capturedAvailableCash = null;
        dialog.Setup(d => d.ShowCashAdvanceRegisterDialogAsync(It.IsAny<List<PaymentMethodDto>>(), It.IsAny<decimal?>()))
            .Callback<List<PaymentMethodDto>, decimal?>((_, available) => capturedAvailableCash = available)
            .ReturnsAsync(((bool success, decimal requestedAmount, decimal commissionAmount, int paymentMethodId, string paymentMethodName, bool isTransfer)?)null);

        var session = new UserSession();
        using var vm = CreateVm(cash, rate, dialog, payments, session);
        session.SetUser(new UserDto { Id = 1, Name = "Usuario Test", Cedula = "V-1", Role = UserRole.Admin });
        await vm.LoadSessionAsync();

        await vm.ProcessCashAdvanceCommand.ExecuteAsync(null);

        Assert.Equal(ServiceBalance, capturedAvailableCash);
    }

    [Fact]
    public async Task CashAdvanceDialog_WithAvailableCash_AppliesLocalCap_AndShowsCapMessage()
    {
        var vm = CreateDialogVm(availableCashLocal: 5000m);
        await vm.RefreshCommissionAsync();

        vm.RequestedAmountBsS = 6000m;

        Assert.Equal("5.000", vm.AvailableCashDisplay);
        Assert.False(vm.CanConfirm);
        Assert.Equal("El monto supera el efectivo en caja (5.000,00 Bs.S).", vm.ErrorMessage);
    }

    [Fact]
    public async Task CashAdvanceDialog_WithoutAvailableCash_AllowsConfirmation_AndShowsNeutralDisplay()
    {
        var vm = CreateDialogVm(availableCashLocal: null);
        await vm.RefreshCommissionAsync();

        vm.RequestedAmountBsS = 6000m;

        Assert.Equal("—", vm.AvailableCashDisplay);
        Assert.True(vm.CanConfirm);
        Assert.Equal(string.Empty, vm.ErrorMessage);
    }

    [Theory]
    [InlineData(UserRole.Admin, true)]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Cashier, false)]
    public void CanViewTheoreticalBalance_IsTrueOnlyForSupervisorRoles(UserRole role, bool expected)
    {
        var (cash, rate, dialog, payments) = CreateMocks();
        using var vm = CreateVm(cash, rate, dialog, payments, CreateLoggedSession(role));

        Assert.Equal(expected, vm.CanViewTheoreticalBalance);
    }

    [Fact]
    public void CanViewTheoreticalBalance_FailsClosed_WithoutSession()
    {
        var (cash, rate, dialog, payments) = CreateMocks();
        using var vm = CreateVm(cash, rate, dialog, payments, null);

        Assert.False(vm.CanViewTheoreticalBalance);
    }

    private static CashAdvanceRegisterViewModel CreateDialogVm(decimal? availableCashLocal)
    {
        var drawer = new Mock<ICashDrawerService>();
        drawer.Setup(d => d.GetAdvanceCommissionAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(5m);

        var methods = new List<PaymentMethodDto>
        {
            new() { Id = 1, Name = "Efectivo", IsCash = true, DisplayOrder = 1 },
            new() { Id = 2, Name = "Transferencia", IsCash = false, DisplayOrder = 2 }
        };

        return new CashAdvanceRegisterViewModel(methods, availableCashLocal, 50m, drawer.Object);
    }
}
