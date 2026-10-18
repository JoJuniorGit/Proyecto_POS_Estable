using System;
using System.Collections.Generic;
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
/// 8.188 (Paso 11-13): segregación de funciones en la gestión de clientes del POS WPF. El cajero
/// crea y edita datos de contacto, pero el panel crediticio se oculta por completo (no solo se
/// deshabilita) y las operaciones de activación/eliminación siguen siendo Admin/Manager. La
/// eliminación se bloquea en la UI cuando el perfil cargado ya revela cartera abierta, con el
/// mensaje del servidor como red de seguridad definitiva.
/// </summary>
public class CustomerManagementCreditRbacTests
{
    private static CustomerManagementViewModel CreateViewModel(
        Mock<ISalesService> sales,
        Mock<IDialogService> dialog,
        UserRole role)
    {
        var session = new UserSession();
        session.SetUser(new UserDto { Id = 9, Name = "Operador", Role = role }, "token");
        return new CustomerManagementViewModel(sales.Object, session, dialog.Object);
    }

    private static CustomerDto Customer(int id = 7, string name = "Ana Pérez") => new()
    {
        Id = id,
        CedulaOrRif = "V-12345678",
        Name = name,
        Phone = "0412-1234567",
        IsActive = true,
        CreditLimitUSD = 500m
    };

    private static CreditProfileClientDto Profile(
        decimal currentDebtUsd = 0m,
        CreditStatus status = CreditStatus.Active) => new()
    {
        CustomerId = 7,
        CustomerName = "Ana Pérez",
        IsActive = true,
        IsCreditEnabled = true,
        Status = status,
        CreditLimitUSD = 500m,
        CurrentDebtUSD = currentDebtUsd,
        AvailableCreditUSD = Math.Max(0m, 500m - currentDebtUsd),
        UtilizationPercent = Math.Round(currentDebtUsd / 500m * 100m, 2),
        PaymentTermDays = 30,
        CreditGraceDays = 5,
        OverdueUSD = 0m,
        OverdueCount = 0
    };

    private static void SetupEmptyCustomerList(Mock<ISalesService> sales)
    {
        sales.Setup(instance => instance.GetCustomersAsync(
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync((new List<CustomerDto>(), 0));
    }

    [Fact]
    public async Task Cashier_CanManageContactButCreditPanelIsHiddenAndMutationCommandsDenied()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        sales.Setup(instance => instance.GetCreditProfileAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile());
        using var vm = CreateViewModel(sales, dialog, UserRole.Cashier);

        Assert.True(vm.CanManageCustomerContact);
        Assert.False(vm.CanMutate);
        Assert.False(vm.CanViewCreditProfile);

        vm.SelectedCustomer = Customer();
        await (vm.PendingCreditProfile ?? Task.CompletedTask);

        // El perfil sigue siendo consultable (lectura), pero el panel completo queda oculto.
        Assert.NotNull(vm.CreditProfile);
        Assert.True(vm.IsEditing);
        Assert.False(vm.CanViewCreditProfile);
        Assert.False(vm.CanEditCreditProfile);
        Assert.False(vm.CanDeleteCustomer);

        await vm.ToggleActiveCustomerCommand.ExecuteAsync(null);
        await vm.DeleteCustomerCommand.ExecuteAsync(null);

        dialog.Verify(instance => instance.ShowWarning(
            "Acceso Denegado", It.Is<string>(message => message.Contains("cajeros"))), Times.Exactly(2));
        sales.Verify(instance => instance.UpdateCustomerAsync(It.IsAny<int>(), It.IsAny<UpdateCustomerDto>()), Times.Never);
        sales.Verify(instance => instance.DeleteCustomerAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Cashier_SaveCustomer_EditsContactThroughService()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        sales.Setup(instance => instance.GetCreditProfileAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile());
        SetupEmptyCustomerList(sales);
        sales.Setup(instance => instance.UpdateCustomerAsync(7, It.IsAny<UpdateCustomerDto>()))
            .ReturnsAsync(Customer(name: "Cliente Contacto Editado"));
        using var vm = CreateViewModel(sales, dialog, UserRole.Cashier);
        vm.SelectedCustomer = Customer();
        await (vm.PendingCreditProfile ?? Task.CompletedTask);

        vm.Name = "Cliente Contacto Editado";
        vm.Phone = "0414-7654321";
        await vm.SaveCustomerCommand.ExecuteAsync(null);

        sales.Verify(instance => instance.UpdateCustomerAsync(
            7,
            It.Is<UpdateCustomerDto>(request =>
                request.Name == "Cliente Contacto Editado"
                && request.Phone == "0414-7654321"
                && request.CedulaOrRif == "V-12345678")),
            Times.Once);
        dialog.Verify(instance => instance.ShowWarning("Acceso Denegado", It.IsAny<string>()), Times.Never);
        dialog.Verify(instance => instance.ShowSuccessDialog(
            It.Is<string>(message => message.Contains("actualizado")), null), Times.Once);
    }

    [Fact]
    public async Task Cashier_CanCreateCustomerThroughService()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        SetupEmptyCustomerList(sales);
        sales.Setup(instance => instance.CreateCustomerAsync(It.IsAny<CreateCustomerDto>()))
            .ReturnsAsync(Customer(id: 21, name: "Cliente Nuevo"));
        using var vm = CreateViewModel(sales, dialog, UserRole.Cashier);

        vm.NewCustomerCommand.Execute(null);
        vm.CedulaOrRif = "V-12345678";
        vm.Name = "Cliente Nuevo";
        vm.Phone = "0412-1234567";
        await vm.SaveCustomerCommand.ExecuteAsync(null);

        sales.Verify(instance => instance.CreateCustomerAsync(It.Is<CreateCustomerDto>(request =>
            request.CedulaOrRif == "V-12345678"
            && request.Name == "Cliente Nuevo"
            && request.Phone == "0412-1234567")), Times.Once);
        dialog.Verify(instance => instance.ShowWarning("Acceso Denegado", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Driver_CannotManageContactAndSaveIsDenied()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        using var vm = CreateViewModel(sales, dialog, UserRole.Driver);

        Assert.False(vm.CanManageCustomerContact);

        vm.CedulaOrRif = "V-12345678";
        vm.Name = "Cliente Driver";
        await vm.SaveCustomerCommand.ExecuteAsync(null);

        dialog.Verify(instance => instance.ShowWarning(
            "Acceso Denegado", It.Is<string>(message => message.Contains("permisos"))), Times.Once);
        sales.Verify(instance => instance.CreateCustomerAsync(It.IsAny<CreateCustomerDto>()), Times.Never);
        sales.Verify(instance => instance.UpdateCustomerAsync(It.IsAny<int>(), It.IsAny<UpdateCustomerDto>()), Times.Never);
    }

    [Fact]
    public void Manager_CanViewCreditPanelOnlyWhileEditingAndKeepsReadAccessWhenSuspended()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        using var vm = CreateViewModel(sales, dialog, UserRole.Manager);

        Assert.False(vm.CanViewCreditProfile);

        vm.SelectedCustomer = Customer();
        Assert.True(vm.CanViewCreditProfile);

        // La mora deshabilita la edición, pero Admin/Manager conservan la lectura del panel.
        vm.CreditProfile = Profile(status: CreditStatus.SuspendedByMora);
        vm.CreditStatus = CreditStatus.SuspendedByMora;
        Assert.True(vm.CanViewCreditProfile);
        Assert.False(vm.CanEditCreditProfile);

        vm.NewCustomerCommand.Execute(null);
        Assert.False(vm.CanViewCreditProfile);
    }

    [Fact]
    public async Task Manager_DeleteCustomerIsDisabledWhenLoadedProfileShowsOpenPortfolio()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        sales.Setup(instance => instance.GetCreditProfileAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile(currentDebtUsd: 120.50m));
        using var vm = CreateViewModel(sales, dialog, UserRole.Manager);
        vm.SelectedCustomer = Customer();
        await (vm.PendingCreditProfile ?? Task.CompletedTask);

        Assert.False(vm.CanDeleteCustomer);

        await vm.DeleteCustomerCommand.ExecuteAsync(null);

        dialog.Verify(instance => instance.ShowWarning(
            "Operación No Permitida", It.Is<string>(message => message.Contains("cartera"))), Times.Once);
        dialog.Verify(instance => instance.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        sales.Verify(instance => instance.DeleteCustomerAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Manager_DeleteCustomerWithoutPortfolio_DelegatesToService()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        dialog.Setup(instance => instance.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        sales.Setup(instance => instance.GetCreditProfileAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile(currentDebtUsd: 0m));
        SetupEmptyCustomerList(sales);
        sales.Setup(instance => instance.DeleteCustomerAsync(7)).Returns(Task.CompletedTask);
        using var vm = CreateViewModel(sales, dialog, UserRole.Manager);
        vm.SelectedCustomer = Customer();
        await (vm.PendingCreditProfile ?? Task.CompletedTask);

        Assert.True(vm.CanDeleteCustomer);

        await vm.DeleteCustomerCommand.ExecuteAsync(null);

        sales.Verify(instance => instance.DeleteCustomerAsync(7), Times.Once);
    }

    [Fact]
    public async Task Manager_DeleteCustomerWhenServerBlocksForPortfolio_ShowsServerMessage()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        dialog.Setup(instance => instance.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        sales.Setup(instance => instance.GetCreditProfileAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Profile(currentDebtUsd: 0m));
        sales.Setup(instance => instance.DeleteCustomerAsync(7)).ThrowsAsync(new InvalidOperationException(
            "No se puede eliminar el cliente porque posee cuentas por cobrar (saldo o historial de cartera). El perfil crediticio y su historial se conservan."));
        using var vm = CreateViewModel(sales, dialog, UserRole.Manager);
        vm.SelectedCustomer = Customer();
        await (vm.PendingCreditProfile ?? Task.CompletedTask);

        await vm.DeleteCustomerCommand.ExecuteAsync(null);

        dialog.Verify(instance => instance.ShowWarning(
            "Advertencia de Eliminación",
            It.Is<string>(message => message.Contains("cuentas por cobrar"))), Times.Once);
    }
}
