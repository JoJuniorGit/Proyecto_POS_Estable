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

        Assert.Equal("Opcional (por defecto: usuario)", vm.PasswordPlaceholder);

        vm.SelectedUser = new UserDto { Id = 1, Cedula = "V-1", Name = "Editor" };
        Assert.Equal("Dejar en blanco para conservar la actual", vm.PasswordPlaceholder);
    }

    [Fact]
    public void View_DeclaresTheEnterpriseStructureAndCanonicalCopy()
    {
        var source = File.ReadAllText(ResolveViewPath());

        Assert.Contains("Text=\"Gestión de Usuarios y Clientes\"", source, StringComparison.Ordinal);
        Assert.Contains("Administra accesos, roles del personal y el directorio de clientes.", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Usuarios y Cajeros\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Gestión de Clientes\"", source, StringComparison.Ordinal);
        Assert.Contains("Value=\"Nuevo Usuario\"", source, StringComparison.Ordinal);
        Assert.Contains("Value=\"Nuevo Cliente\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Rol del Usuario *\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Nombre Completo o Razón Social *\"", source, StringComparison.Ordinal);
        Assert.Contains("PasswordPlaceholder", source, StringComparison.Ordinal);
        Assert.Contains("Máximo 50 caracteres para reportes y facturas", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Usuario Activo\"", source, StringComparison.Ordinal);
        Assert.Contains("Permite iniciar sesión en el POS", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Cliente Activo\"", source, StringComparison.Ordinal);
        Assert.Contains("Habilitado para facturación y crédito", source, StringComparison.Ordinal);
        Assert.Contains("Guardar Usuario", source, StringComparison.Ordinal);
        Assert.Contains("Guardar Cliente", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"+ Nuevo\"", source, StringComparison.Ordinal);
        Assert.Contains("Header=\"USUARIO\"", source, StringComparison.Ordinal);
        Assert.Contains("Header=\"CÉDULA\"", source, StringComparison.Ordinal);
        Assert.Contains("Header=\"CÉDULA / RIF\"", source, StringComparison.Ordinal);
        Assert.Contains("Header=\"TELÉFONO\"", source, StringComparison.Ordinal);
        Assert.Contains("Header=\"ROL\"", source, StringComparison.Ordinal);
        Assert.Contains("Header=\"ESTADO\"", source, StringComparison.Ordinal);
        Assert.Contains("RowHeight=\"50\"", source, StringComparison.Ordinal);
        Assert.Contains("InitialsConverter", source, StringComparison.Ordinal);
        Assert.Contains("UserSearchQuery", source, StringComparison.Ordinal);
        Assert.Contains("SelectedUserRoleFilter", source, StringComparison.Ordinal);
        Assert.Contains("SelectedCustomerStatusFilter", source, StringComparison.Ordinal);
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
