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

namespace CommandCenter.Tests.Unit;

public class CashDrawerRbacAndPaymentMethodsTests
{
    private static (Mock<ICashDrawerService> Cash, Mock<IExchangeRateService> Rate, Mock<IDialogService> Dialog, Mock<IPaymentService> Payments) CreateMocks()
    {
        var cash = new Mock<ICashDrawerService>();
        cash.Setup(s => s.GetActiveSessionAsync()).ReturnsAsync(new CashDrawerSessionDto { Id = 1, Status = CashDrawerStatus.Open });
        cash.Setup(s => s.GetHistoryAsync(It.IsAny<int>())).ReturnsAsync(new List<CashTransactionDto>());
        cash.Setup(s => s.GetCurrentBalanceLocalAsync(It.IsAny<int>())).ReturnsAsync(500m);

        var rate = new Mock<IExchangeRateService>();
        rate.SetupGet(r => r.CurrentRate).Returns(50m);
        rate.Setup(r => r.GetCurrentRateAsync()).ReturnsAsync((50m, (DateTime?)null));
        rate.Setup(r => r.GetHistoryAsync()).ReturnsAsync(new List<ExchangeRateHistoryDto>());

        return (cash, rate, new Mock<IDialogService>(), new Mock<IPaymentService>());
    }

    private static UserSession CreateSession(UserRole? role)
    {
        var session = new UserSession();
        if (role.HasValue)
        {
            session.SetUser(new UserDto { Id = 1, Name = "Usuario Test", Cedula = "V-1", Role = role.Value });
        }
        return session;
    }

    [Fact]
    public void IsAdmin_FailsClosed_WithoutSession()
    {
        var (cash, rate, dialog, payments) = CreateMocks();

        using var vm = new CashDrawerViewModel(cash.Object, rate.Object, dialog.Object, payments.Object, null);
        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        Assert.False(vm.IsAdmin);
    }

    [Fact]
    public void IsAdmin_IsTrueOnlyForAdminSession()
    {
        var (cash, rate, dialog, payments) = CreateMocks();

        using var adminVm = new CashDrawerViewModel(cash.Object, rate.Object, dialog.Object, payments.Object, CreateSession(UserRole.Admin));
        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(adminVm);
        using var cashierVm = new CashDrawerViewModel(cash.Object, rate.Object, dialog.Object, payments.Object, CreateSession(UserRole.Cashier));
        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(cashierVm);

        Assert.True(adminVm.IsAdmin);
        Assert.False(cashierVm.IsAdmin);
    }

    [Fact]
    public async Task ProcessCashIn_WithoutSession_IsDeniedAndDoesNotOpenDialog()
    {
        var (cash, rate, dialog, payments) = CreateMocks();
        using var vm = new CashDrawerViewModel(cash.Object, rate.Object, dialog.Object, payments.Object, null);
        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        await vm.ProcessCashInCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowError("Acceso Denegado", It.IsAny<string>()), Times.Once);
        dialog.Verify(d => d.ShowCashTransactionDialogAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProcessCashOut_WithCashierSession_IsDeniedAndDoesNotOpenDialog()
    {
        var (cash, rate, dialog, payments) = CreateMocks();
        using var vm = new CashDrawerViewModel(cash.Object, rate.Object, dialog.Object, payments.Object, CreateSession(UserRole.Cashier));
        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        await vm.ProcessCashOutCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowError("Acceso Denegado", It.IsAny<string>()), Times.Once);
        dialog.Verify(d => d.ShowCashTransactionDialogAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProcessCashIn_WithAdminSession_OpensCashTransactionDialog()
    {
        var (cash, rate, dialog, payments) = CreateMocks();
        using var vm = new CashDrawerViewModel(cash.Object, rate.Object, dialog.Object, payments.Object, CreateSession(UserRole.Admin));
        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        await vm.ProcessCashInCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowCashTransactionDialogAsync("Cash In (Add Funds)"), Times.Once);
        dialog.Verify(d => d.ShowError("Acceso Denegado", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProcessCashAdvance_UsesPaymentMethodsFromService()
    {
        var (cash, rate, dialog, payments) = CreateMocks();
        payments.Setup(p => p.GetActiveMethodsAsync()).ReturnsAsync(new List<PaymentMethodDto>
        {
            new() { Id = 99, Name = "Método Catálogo", IsCash = false, IsActive = true }
        });

        List<PaymentMethodDto>? capturedMethods = null;
        dialog.Setup(d => d.ShowCashAdvanceRegisterDialogAsync(It.IsAny<List<PaymentMethodDto>>(), It.IsAny<decimal>()))
            .Callback<List<PaymentMethodDto>, decimal>((methods, _) => capturedMethods = methods)
            .ReturnsAsync(((bool success, decimal requestedAmount, decimal commissionAmount, int paymentMethodId, string paymentMethodName, bool isTransfer)?)null);

        using var vm = new CashDrawerViewModel(cash.Object, rate.Object, dialog.Object, payments.Object, CreateSession(UserRole.Admin));
        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        await vm.ProcessCashAdvanceCommand.ExecuteAsync(null);

        Assert.NotNull(capturedMethods);
        var method = Assert.Single(capturedMethods!);
        Assert.Equal(99, method.Id);
        payments.Verify(p => p.GetActiveMethodsAsync(), Times.Once);
    }

    [Fact]
    public async Task ProcessCashAdvance_WithoutPaymentService_FallsBackToCentralizedIds()
    {
        var (cash, rate, dialog, _) = CreateMocks();

        List<PaymentMethodDto>? capturedMethods = null;
        dialog.Setup(d => d.ShowCashAdvanceRegisterDialogAsync(It.IsAny<List<PaymentMethodDto>>(), It.IsAny<decimal>()))
            .Callback<List<PaymentMethodDto>, decimal>((methods, _) => capturedMethods = methods)
            .ReturnsAsync(((bool success, decimal requestedAmount, decimal commissionAmount, int paymentMethodId, string paymentMethodName, bool isTransfer)?)null);

        using var vm = new CashDrawerViewModel(cash.Object, rate.Object, dialog.Object, null, CreateSession(UserRole.Admin));
        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        await vm.ProcessCashAdvanceCommand.ExecuteAsync(null);

        Assert.NotNull(capturedMethods);
        Assert.Equal(new[] { 2, 3, 4 }, capturedMethods!.Select(m => m.Id).ToArray());
    }
}
