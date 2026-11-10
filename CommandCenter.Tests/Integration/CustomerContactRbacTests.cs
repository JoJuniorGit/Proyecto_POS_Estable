using System;
using System.Linq;
using System.Reflection;
using Backend.API.Controllers;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.188 (Pasos 11-13): matriz RBAC de la superficie de clientes. El alta y la edición de
/// contacto quedan disponibles para caja (Admin/Manager/Cashier) porque no administran crédito;
/// la escritura del perfil crediticio se mantiene exclusiva de Admin/Manager y la eliminación
/// física del cliente sigue siendo Admin/Manager (el servicio la bloquea con cartera).
/// </summary>
public class CustomerContactRbacTests
{
    private static void AssertAuthorizedRoles(string methodName, params string[] expectedRoles)
    {
        var method = typeof(SalesController).GetMethod(methodName);
        Assert.NotNull(method);

        var authorize = method!.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);

        var roles = (authorize!.Roles ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(role => role, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expectedRoles.OrderBy(role => role, StringComparer.Ordinal).ToArray(),
            roles);
    }

    [Fact]
    public void CreateCustomer_AllowsAdminManagerAndCashier()
    {
        AssertAuthorizedRoles(nameof(SalesController.CreateCustomerAsync), "Admin", "Manager", "Cashier");
    }

    [Fact]
    public void UpdateCustomer_AllowsAdminManagerAndCashier()
    {
        AssertAuthorizedRoles(nameof(SalesController.UpdateCustomerAsync), "Admin", "Manager", "Cashier");
    }

    [Fact]
    public void DeleteCustomer_RemainsRestrictedToAdminAndManager()
    {
        AssertAuthorizedRoles(nameof(SalesController.DeleteCustomerAsync), "Admin", "Manager");
    }

    [Fact]
    public void CreditProfileWrite_RemainsRestrictedToAdminAndManager()
    {
        AssertAuthorizedRoles(nameof(SalesController.UpdateCreditProfileAsync), "Admin", "Manager");
    }
}
