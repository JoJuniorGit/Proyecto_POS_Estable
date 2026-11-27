using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Converters;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Contrato de la mejora Enterprise de Gestión de Usuarios y Clientes: filtros simétricos,
/// conteo total, filtro de estado server-side, avatares y copy del inspector.
/// </summary>
public sealed class UsersAndCustomersEnterpriseUiTests
{
    [Fact]
    public async Task Users_FilterByTextAndRole_KeepTotalCount()
    {
        var users = new List<UserDto>
        {
            new() { Id = 1, Cedula = "V-10000001", Name = "Ana Perez", Role = UserRole.Admin, IsActive = true },
            new() { Id = 2, Cedula = "V-10000002", Name = "Luis Anaya", Role = UserRole.Manager, IsActive = true },
            new() { Id = 3, Cedula = "V-10000003", Name = "Pedro Gomez", Role = UserRole.Cashier, IsActive = true },
            new() { Id = 4, Cedula = "V-10000004", Name = "Ana Maria", Role = UserRole.Cashier, IsActive = false }
        };

        var userService = new Mock<IUserService>();
        userService.Setup(service => service.GetUsersAsync()).ReturnsAsync(users);
        var session = new UserSession();
        using var customerVm = new CustomerManagementViewModel(
            new Mock<ISalesService>().Object,
            session,
            new Mock<IDialogService>().Object);
        var vm = new UsersManagementViewModel(userService.Object, session, customerVm);

        await vm.LoadUsersCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.TotalUserCount);
        Assert.Equal(4, vm.Users.Count);

        vm.UserSearchQuery = "Luis";
        Assert.Single(vm.Users);
        Assert.Equal("Luis Anaya", vm.Users[0].Name);

        vm.UserSearchQuery = string.Empty;
        vm.SelectedUserRoleFilter = vm.UserRoleFilters.Single(filter => filter.Role == UserRole.Cashier);
        Assert.Equal(2, vm.Users.Count);
        Assert.Contains(vm.Users, user => user.Name == "Ana Maria");
        Assert.DoesNotContain(vm.Users, user => user.Name == "Ana Perez");
        Assert.Equal(4, vm.TotalUserCount);
    }

    [Fact]
    public async Task Customers_StatusFilter_IsForwardedToTheService()
    {
        var sales = new Mock<ISalesService>();
        sales.Setup(service => service.GetCustomersAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                false,
                It.IsAny<bool?>()))
            .ReturnsAsync((new List<CustomerDto>
            {
                new() { Id = 1, CedulaOrRif = "V-10000001", Name = "Cliente Activo", IsActive = true }
            }, 1));

        var vm = new CustomerManagementViewModel(
            sales.Object,
            new UserSession(),
            new Mock<IDialogService>().Object);

        await vm.LoadCustomersAsync();

        sales.Verify(service => service.GetCustomersAsync(
            It.IsAny<string?>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            false,
            null), Times.Once);

        vm.SelectedCustomerStatusFilter = vm.CustomerStatusFilters.Single(filter => filter.IsActive == true);
        await vm.LoadCustomersAsync();

        sales.Verify(service => service.GetCustomersAsync(
            It.IsAny<string?>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            false,
            true), Times.AtLeastOnce);
        Assert.Equal(1, vm.TotalCount);
    }

    [Fact]
    public async Task Users_ModalCreateAndEdit_CancelKeepsTheGridItemIntact()
    {
        var user = new UserDto
        {
            Id = 1,
            Cedula = "V-12345678",
            Name = "Usuario Original",
            Role = UserRole.Cashier,
            IsActive = true
        };
        var userService = new Mock<IUserService>();
        userService.Setup(service => service.GetUsersAsync()).ReturnsAsync(new List<UserDto> { user });
        var session = new UserSession();
        using var customerVm = new CustomerManagementViewModel(
            new Mock<ISalesService>().Object,
            session,
            new Mock<IDialogService>().Object);
        var vm = new UsersManagementViewModel(userService.Object, session, customerVm);
        await vm.LoadUsersCommand.ExecuteAsync(null);

        vm.OpenEditUserCommand.Execute(user);

        Assert.True(vm.IsUserModalOpen);
        Assert.True(vm.IsEditing);
        Assert.Equal("Usuario Original", vm.Name);

        vm.Name = "Cambio de borrador";
        vm.CloseUserModalCommand.Execute(null);

        Assert.False(vm.IsUserModalOpen);
        Assert.False(vm.IsEditing);
        Assert.Equal("Usuario Original", user.Name);

        vm.NewUserCommand.Execute(null);
        Assert.True(vm.IsUserModalOpen);
        Assert.False(vm.IsEditing);
        Assert.Empty(vm.Cedula);
        Assert.Empty(vm.Name);
    }

    [Fact]
    public async Task Customers_ModalCreateAndEdit_CancelKeepsTheGridItemIntact()
    {
        var customer = new CustomerDto
        {
            Id = 7,
            CedulaOrRif = "V-12345678",
            Name = "Cliente Original",
            Phone = "0412-1234567",
            IsActive = true
        };
        var sales = new Mock<ISalesService>();
        sales.Setup(service => service.GetCustomersAsync(
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<bool?>()))
            .ReturnsAsync((new List<CustomerDto> { customer }, 1));
        var vm = new CustomerManagementViewModel(
            sales.Object,
            new UserSession(),
            new Mock<IDialogService>().Object);
        await vm.LoadCustomersAsync();

        vm.OpenEditCustomerCommand.Execute(customer);

        Assert.True(vm.IsCustomerModalOpen);
        Assert.True(vm.IsEditing);
        Assert.Equal("Cliente Original", vm.Name);

        vm.Name = "Cambio de borrador";
        vm.CloseCustomerModalCommand.Execute(null);

        Assert.False(vm.IsCustomerModalOpen);
        Assert.False(vm.IsEditing);
        Assert.Equal("Cliente Original", customer.Name);

        vm.OpenNewCustomerCommand.Execute(null);
        Assert.True(vm.IsCustomerModalOpen);
        Assert.False(vm.IsEditing);
        Assert.Empty(vm.CedulaOrRif);
        Assert.Empty(vm.Name);
    }

    [Fact]
    public async Task Modals_ValidationErrorsStayInlineAndPreserveTheDraft()
    {
        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Admin", Role = UserRole.Admin }, "token");
        using var customerVm = new CustomerManagementViewModel(
            new Mock<ISalesService>().Object,
            session,
            new Mock<IDialogService>().Object);
        var users = new UsersManagementViewModel(
            new Mock<IUserService>().Object,
            session,
            customerVm);

        users.NewUserCommand.Execute(null);
        users.Name = "Borrador sin usuario";
        await users.SaveUserCommand.ExecuteAsync(null);

        Assert.True(users.IsUserModalOpen);
        Assert.Contains("obligatorios", users.UserModalError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Borrador sin usuario", users.Name);

        customerVm.OpenNewCustomerCommand.Execute(null);
        customerVm.Name = "Borrador sin cédula";
        await customerVm.SaveCustomerCommand.ExecuteAsync(null);

        Assert.True(customerVm.IsCustomerModalOpen);
        Assert.Contains("Cédula o RIF", customerVm.CustomerModalError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Borrador sin cédula", customerVm.Name);
    }

    [Fact]
    public async Task Users_AdvancedPagination_SlicesTheFilteredDirectory()
    {
        var users = Enumerable.Range(1, 60)
            .Select(index => new UserDto
            {
                Id = index,
                Cedula = $"V-{10000000 + index}",
                Name = $"Usuario {index:00}",
                Role = UserRole.Cashier,
                IsActive = index != 60
            })
            .ToList();

        var userService = new Mock<IUserService>();
        userService.Setup(service => service.GetUsersAsync()).ReturnsAsync(users);
        var session = new UserSession();
        using var customerVm = new CustomerManagementViewModel(
            new Mock<ISalesService>().Object,
            session,
            new Mock<IDialogService>().Object);
        var vm = new UsersManagementViewModel(userService.Object, session, customerVm);
        await vm.LoadUsersCommand.ExecuteAsync(null);

        Assert.Equal(60, vm.TotalUserCount);
        Assert.Equal(25, vm.Users.Count);
        Assert.Equal(3, vm.UserTotalPages);
        Assert.Equal("Página 1 de 3 (60 usuarios)", vm.UserPageSummary);
        Assert.Equal(3, vm.UserPageNumbers.Count);

        vm.NextUserPageCommand.Execute(null);
        Assert.Equal(2, vm.UserCurrentPage);
        Assert.Equal(25, vm.Users.Count);

        vm.LastUserPageCommand.Execute(null);
        Assert.Equal(3, vm.UserCurrentPage);
        Assert.Equal(10, vm.Users.Count);

        vm.GoToUserPageCommand.Execute(1);
        Assert.Equal(1, vm.UserCurrentPage);
        Assert.Equal(25, vm.Users.Count);

        vm.UserSearchQuery = "Usuario 60";
        Assert.Equal(1, vm.UserCurrentPage);
        Assert.Single(vm.Users);
        Assert.Equal("Página 1 de 1 (1 usuarios)", vm.UserPageSummary);

        vm.UserSearchQuery = string.Empty;
        vm.SelectedUserStatusFilter = vm.UserStatusFilters.Single(filter => filter.IsActive == false);
        Assert.Single(vm.Users);
        Assert.Equal("Usuario 60", vm.Users[0].Name);
    }

    [Fact]
    public async Task Customers_SearchQuery_IsForwardedToTheService()
    {
        var sales = new Mock<ISalesService>();
        sales.Setup(service => service.GetCustomersAsync(
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<bool?>()))
            .ReturnsAsync((new List<CustomerDto>(), 0));
        var vm = new CustomerManagementViewModel(
            sales.Object,
            new UserSession(),
            new Mock<IDialogService>().Object);

        await vm.LoadCustomersAsync("0414");

        sales.Verify(service => service.GetCustomersAsync(
            "0414", 1, 25, false, null), Times.Once);
    }

    [Fact]
    public async Task Customers_AdvancedPagination_UsesServerPagesAndCatalogControls()
    {
        var pageItems = Enumerable.Range(1, 25)
            .Select(index => new CustomerDto
            {
                Id = index,
                CedulaOrRif = $"V-{10000000 + index}",
                Name = $"Cliente {index:00}",
                Phone = "04141234567",
                IsActive = true
            })
            .ToList();

        var sales = new Mock<ISalesService>();
        sales.Setup(service => service.GetCustomersAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                false,
                It.IsAny<bool?>()))
            .ReturnsAsync((pageItems, 60));

        var vm = new CustomerManagementViewModel(
            sales.Object,
            new UserSession(),
            new Mock<IDialogService>().Object);
        await vm.LoadCustomersAsync();

        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal(3, vm.TotalPages);
        Assert.Equal(25, vm.PageSize);
        Assert.Equal("Página 1 de 3 (60 clientes)", vm.PageSummary);
        Assert.Equal(3, vm.PageNumbers.Count);

        await vm.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal("Página 2 de 3 (60 clientes)", vm.PageSummary);

        await vm.LastPageCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.CurrentPage);

        await vm.GoToPageCommand.ExecuteAsync(1);
        Assert.Equal(1, vm.CurrentPage);

        vm.TargetPageInput = "2";
        await vm.SubmitGoToPageCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.CurrentPage);
    }

    [Fact]
    public async Task UserModal_SuccessfulSaveClosesTheModalAndEmitsAToast()
    {
        var user = new UserDto
        {
            Id = 1,
            Cedula = "V-12345678",
            Name = "Usuario Original",
            Role = UserRole.Cashier,
            IsActive = true
        };
        var userService = new Mock<IUserService>();
        userService.Setup(service => service.GetUsersAsync()).ReturnsAsync(new List<UserDto> { user });
        userService.Setup(service => service.UpdateUserAsync(
                1,
                It.Is<UpdateUserDto>(request => request.Name == "Usuario Guardado")))
            .ReturnsAsync(new UserDto { Id = 1, Cedula = "V-12345678", Name = "Usuario Guardado", Role = UserRole.Cashier, IsActive = true });

        var session = new UserSession();
        using var customerVm = new CustomerManagementViewModel(
            new Mock<ISalesService>().Object,
            session,
            new Mock<IDialogService>().Object);
        var vm = new UsersManagementViewModel(userService.Object, session, customerVm);
        await vm.LoadUsersCommand.ExecuteAsync(null);

        vm.OpenEditUserCommand.Execute(user);
        vm.Name = "Usuario Guardado";
        await vm.SaveUserCommand.ExecuteAsync(null);

        Assert.False(vm.IsUserModalOpen);
        Assert.Null(vm.UserModalError);
        Assert.Contains("guardado", vm.ToastMessage, StringComparison.OrdinalIgnoreCase);
        userService.Verify(service => service.UpdateUserAsync(1, It.IsAny<UpdateUserDto>()), Times.Once);
    }

    [Fact]
    public async Task CustomerModal_SuccessfulSaveClosesTheModalAndEmitsAToast()
    {
        var customer = new CustomerDto
        {
            Id = 7,
            CedulaOrRif = "V-12345678",
            Name = "Cliente Original",
            Phone = "0412-1234567",
            IsActive = true
        };
        var sales = new Mock<ISalesService>();
        sales.Setup(service => service.GetCustomersAsync(
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), false, It.IsAny<bool?>()))
            .ReturnsAsync((new List<CustomerDto> { customer }, 1));
        sales.Setup(service => service.UpdateCustomerAsync(7, It.IsAny<UpdateCustomerDto>()))
            .ReturnsAsync(new CustomerDto
            {
                Id = 7,
                CedulaOrRif = "V-12345678",
                Name = "Cliente Guardado",
                Phone = "0412-1234567",
                IsActive = true
            });
        sales.Setup(service => service.GetCreditProfileAsync(7, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new CreditProfileClientDto
            {
                CustomerId = 7,
                CustomerName = "Cliente Original",
                IsActive = true,
                IsCreditEnabled = false,
                Status = CreditStatus.Active
            });

        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Admin", Role = UserRole.Admin }, "token");
        var vm = new CustomerManagementViewModel(sales.Object, session, new Mock<IDialogService>().Object);
        await vm.LoadCustomersAsync();

        string? toast = null;
        vm.ToastRequested += message => toast = message;

        vm.OpenEditCustomerCommand.Execute(customer);
        await (vm.PendingCreditProfile ?? Task.CompletedTask);
        vm.Name = "Cliente Guardado";
        await vm.SaveCustomerCommand.ExecuteAsync(null);

        Assert.False(vm.IsCustomerModalOpen);
        Assert.Null(vm.CustomerModalError);
        Assert.Contains("guardado", toast, StringComparison.OrdinalIgnoreCase);
        sales.Verify(service => service.UpdateCustomerAsync(7, It.IsAny<UpdateCustomerDto>()), Times.Once);
    }

    [Fact]
    public void CustomerRif_PreservesTheCheckDigitAndValidates()
    {
        using var vm = new CustomerManagementViewModel(
            new Mock<ISalesService>().Object,
            new UserSession(),
            new Mock<IDialogService>().Object);

        vm.CedulaOrRif = "J-12345678-9";

        Assert.Equal("J-12345678-9", vm.CedulaOrRif);
        Assert.True(vm.IsCedulaValid);

        vm.CedulaOrRif = "V-12345678";
        Assert.Equal("V-12345678", vm.CedulaOrRif);
        Assert.True(vm.IsCedulaValid);
    }

    [Fact]
    public void InitialsConverter_ProducesTwoLettersForNames()
    {
        var converter = new InitialsConverter();

        Assert.Equal("AP", converter.Convert("Ana Perez", typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Equal("AD", converter.Convert("Admin", typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Equal("?", converter.Convert("", typeof(string), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void PasswordPlaceholder_UsesTheShortCanonicalCopy()
    {
        var session = new UserSession();
        using var customerVm = new CustomerManagementViewModel(
            new Mock<ISalesService>().Object,
            session,
            new Mock<IDialogService>().Object);
        var vm = new UsersManagementViewModel(
            new Mock<IUserService>().Object,
            session,
            customerVm);

        Assert.Equal("Dejar vacío para valor por defecto", vm.PasswordPlaceholder);

        vm.SelectedUser = new UserDto { Id = 1, Cedula = "V-1", Name = "Editor" };
        Assert.Equal("Dejar vacío para conservar la actual", vm.PasswordPlaceholder);
    }

    [Fact]
    public void View_DeclaresTheEnterpriseStructureAndCanonicalCopy()
    {
        var mainPath = ResolveViewPath();
        var viewDirectory = Path.GetDirectoryName(mainPath)!;
        var source = File.ReadAllText(mainPath)
            + File.ReadAllText(Path.Combine(viewDirectory, "UserEditorModal.xaml"))
            + File.ReadAllText(Path.Combine(viewDirectory, "CustomerEditorModal.xaml"));

        Assert.Contains("Text=\"Gestión de Usuarios y Clientes\"", source, StringComparison.Ordinal);
        Assert.Contains("Administra accesos, roles del personal y el directorio comercial de clientes.", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Usuarios y Cajeros\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Gestión de Clientes\"", source, StringComparison.Ordinal);
        Assert.Contains("UserModalTitle", source, StringComparison.Ordinal);
        Assert.Contains("CustomerModalTitle", source, StringComparison.Ordinal);
        Assert.Contains("IsUserModalOpen", source, StringComparison.Ordinal);
        Assert.Contains("IsCustomerModalOpen", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Rol del Usuario *\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Nombre Completo o Razón Social *\"", source, StringComparison.Ordinal);
        Assert.Contains("PasswordPlaceholder", source, StringComparison.Ordinal);
        Assert.Contains("caracteres para facturas y reportes.", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Usuario Activo\"", source, StringComparison.Ordinal);
        Assert.Contains("operar en caja", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Cliente Activo\"", source, StringComparison.Ordinal);
        Assert.Contains("ventas a cr", source, StringComparison.Ordinal);
        Assert.Contains("Crear Usuario", source, StringComparison.Ordinal);
        Assert.Contains("Crear Cliente", source, StringComparison.Ordinal);
        Assert.Contains("Guardar Cambios", source, StringComparison.Ordinal);
        Assert.Contains("Header=\"USUARIO\"", source, StringComparison.Ordinal);
        Assert.Contains("Header=\"CÉDULA / RIF\"", source, StringComparison.Ordinal);
        Assert.Contains("Header=\"TELÉFONO\"", source, StringComparison.Ordinal);
        Assert.Contains("Header=\"ROL\"", source, StringComparison.Ordinal);
        Assert.Contains("Header=\"ESTADO\"", source, StringComparison.Ordinal);
        Assert.Contains("RowHeight=\"52\"", source, StringComparison.Ordinal);
        Assert.Contains("InitialsConverter", source, StringComparison.Ordinal);
        Assert.Contains("UserSearchQuery", source, StringComparison.Ordinal);
        Assert.Contains("SelectedUserRoleFilter", source, StringComparison.Ordinal);
        Assert.Contains("SelectedUserStatusFilter", source, StringComparison.Ordinal);
        Assert.Contains("SelectedCustomerStatusFilter", source, StringComparison.Ordinal);
        Assert.Contains("OpenEditUserCommand", source, StringComparison.Ordinal);
        Assert.Contains("OpenEditCustomerCommand", source, StringComparison.Ordinal);
        Assert.Contains("HasUserModalError", source, StringComparison.Ordinal);
        Assert.Contains("HasCustomerModalError", source, StringComparison.Ordinal);
        Assert.Contains("PaginationNumberButtonStyle", source, StringComparison.Ordinal);
        Assert.Contains("Users_PageSummary", source, StringComparison.Ordinal);
        Assert.Contains("Customers_PageSummary", source, StringComparison.Ordinal);
        Assert.Contains("FirstUserPageCommand", source, StringComparison.Ordinal);
        Assert.Contains("FirstPageCommand", source, StringComparison.Ordinal);
        Assert.Contains("UserPageNumbers", source, StringComparison.Ordinal);
        Assert.Contains("PageNumbers", source, StringComparison.Ordinal);
        Assert.Contains("TargetPageInput", source, StringComparison.Ordinal);
        Assert.DoesNotContain("materialDesign:HintAssist.Hint=\"Rol del Usuario *\"", source, StringComparison.Ordinal);
        Assert.True(System.Text.RegularExpressions.Regex.Matches(source, "Text=\" / 50\"").Count == 1, "El contador / 50 debe aparecer una sola vez.");
    }

    private static string ResolveViewPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Desktop.Client", "Views", "UsersManagementView.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("No se encontró Desktop.Client/Views/UsersManagementView.xaml.");
    }
}
