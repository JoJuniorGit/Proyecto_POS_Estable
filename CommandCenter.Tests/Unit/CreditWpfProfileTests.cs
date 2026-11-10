using System;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T4 (F3, S1/S4): perfil crediticio en la pestaña de clientes. Edición Admin/Manager con cupo,
/// plazo, gracia y notas; bloqueo manual Active/ManuallyBlocked; mora administrada por el sistema.
/// </summary>
public class CreditWpfProfileTests
{
    private static CustomerManagementViewModel CreateViewModel(
        Mock<ISalesService> sales,
        Mock<IDialogService> dialog,
        UserRole role = UserRole.Manager)
    {
        var session = new UserSession();
        session.SetUser(new UserDto { Id = 9, Name = "Supervisor", Role = role }, "token");
        return new CustomerManagementViewModel(sales.Object, session, dialog.Object);
    }

    private static CustomerDto Customer(int id = 7) => new()
    {
        Id = id,
        CedulaOrRif = "V-12345678",
        Name = "Ana Pérez",
        Phone = "0412-1234567",
        IsActive = true,
        CreditLimitUSD = 500m
    };

    private static CreditProfileClientDto Profile(
        bool enabled = true,
        CreditStatus status = CreditStatus.Active,
        int overdueCount = 0) => new()
    {
        CustomerId = 7,
        CustomerName = "Ana Pérez",
        IsActive = true,
        IsCreditEnabled = enabled,
        Status = status,
        CreditLimitUSD = 500m,
        CurrentDebtUSD = 120.50m,
        AvailableCreditUSD = 379.50m,
        UtilizationPercent = 24.10m,
        PaymentTermDays = 30,
        CreditGraceDays = 5,
        CreditNotes = "Acuerdo especial",
        OverdueUSD = overdueCount > 0 ? 20m : 0m,
        OverdueCount = overdueCount
    };

    [Fact]
    public async Task SelectCustomer_LoadsProfileAndExposesCreditMetrics()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        sales.Setup(s => s.GetCreditProfileAsync(7, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(Profile());
        using var vm = CreateViewModel(sales, dialog);

        vm.SelectedCustomer = Customer();
        await (vm.PendingCreditProfile ?? Task.CompletedTask);

        Assert.NotNull(vm.CreditProfile);
        Assert.True(vm.IsCreditEnabled);
        Assert.Equal("30", vm.PaymentTermDaysText);
        Assert.Equal("5", vm.CreditGraceDaysText);
        Assert.Equal("Acuerdo especial", vm.CreditNotes);
        Assert.True(vm.CanEditCreditProfile);
        Assert.Equal(379.50m, vm.CreditProfile!.AvailableCreditUSD);
    }

    [Fact]
    public async Task SaveCreditProfile_WithInvalidTerm_ShowsValidationAndDoesNotCallService()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        sales.Setup(s => s.GetCreditProfileAsync(7, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(Profile());
        using var vm = CreateViewModel(sales, dialog);
        vm.SelectedCustomer = Customer();
        await (vm.PendingCreditProfile ?? Task.CompletedTask);

        vm.PaymentTermDaysText = "0";
        await vm.SaveCreditProfileCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowWarning("Perfil Crediticio", It.Is<string>(m => m.Contains("plazo"))), Times.Once);
        sales.Verify(s => s.UpdateCreditProfileAsync(It.IsAny<int>(), It.IsAny<CreditProfileUpdateClientDto>(), It.IsAny<System.Threading.CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SaveCreditProfile_WithValidData_PersistsAllFields()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        sales.Setup(s => s.GetCreditProfileAsync(7, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(Profile());
        sales.Setup(s => s.UpdateCreditProfileAsync(7, It.IsAny<CreditProfileUpdateClientDto>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(Profile());
        using var vm = CreateViewModel(sales, dialog);
        vm.SelectedCustomer = Customer();
        await (vm.PendingCreditProfile ?? Task.CompletedTask);

        vm.IsCreditEnabled = true;
        vm.CreditLimitText = "800.00";
        vm.PaymentTermDaysText = "45";
        vm.CreditGraceDaysText = "10";
        vm.CreditNotes = "Garantía prendaria";
        await vm.SaveCreditProfileCommand.ExecuteAsync(null);

        sales.Verify(s => s.UpdateCreditProfileAsync(
            7,
            It.Is<CreditProfileUpdateClientDto>(r =>
                r.IsCreditEnabled &&
                r.CreditLimitUSD == 800m &&
                r.PaymentTermDays == 45 &&
                r.CreditGraceDays == 10 &&
                r.CreditNotes == "Garantía prendaria" &&
                r.Status == CreditStatus.Active),
            It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        dialog.Verify(d => d.ShowSuccessDialog(It.Is<string>(m => m.Contains("Perfil crediticio")), null), Times.Once);
    }

    [Fact]
    public async Task ToggleBlock_FromActive_SavesManuallyBlocked()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        sales.Setup(s => s.GetCreditProfileAsync(7, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(Profile());
        sales.Setup(s => s.UpdateCreditProfileAsync(7, It.IsAny<CreditProfileUpdateClientDto>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(Profile(status: CreditStatus.ManuallyBlocked));
        using var vm = CreateViewModel(sales, dialog);
        vm.SelectedCustomer = Customer();
        await (vm.PendingCreditProfile ?? Task.CompletedTask);

        await vm.ToggleCreditBlockCommand.ExecuteAsync(null);

        sales.Verify(s => s.UpdateCreditProfileAsync(
            7,
            It.Is<CreditProfileUpdateClientDto>(r => r.Status == CreditStatus.ManuallyBlocked),
            It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        Assert.Equal(CreditStatus.ManuallyBlocked, vm.CreditStatus);
    }

    [Fact]
    public async Task ToggleBlock_WhenSuspendedByMora_DoesNotOverrideSystemState()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        sales.Setup(s => s.GetCreditProfileAsync(7, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(Profile(status: CreditStatus.SuspendedByMora));
        using var vm = CreateViewModel(sales, dialog);
        vm.SelectedCustomer = Customer();
        await (vm.PendingCreditProfile ?? Task.CompletedTask);

        await vm.ToggleCreditBlockCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowWarning("Perfil Crediticio", It.Is<string>(m => m.Contains("mora"))), Times.Once);
        sales.Verify(s => s.UpdateCreditProfileAsync(It.IsAny<int>(), It.IsAny<CreditProfileUpdateClientDto>(), It.IsAny<System.Threading.CancellationToken>()), Times.Never);
        Assert.False(vm.CanEditCreditProfile);
    }

    [Fact]
    public async Task Cashier_CanSeeProfileButCannotEditIt()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        sales.Setup(s => s.GetCreditProfileAsync(7, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(Profile());
        using var vm = CreateViewModel(sales, dialog, UserRole.Cashier);
        vm.SelectedCustomer = Customer();
        await (vm.PendingCreditProfile ?? Task.CompletedTask);

        Assert.NotNull(vm.CreditProfile);
        Assert.False(vm.CanEditCreditProfile);
    }
}
