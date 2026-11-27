using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.212-WU2: dedicated per-method balance endpoint. It is a read-only GET gated to the roles
/// that operate the drawer, validates the session id and returns the same DTO collection used by
/// the active-session payload.
/// </summary>
public class CashDrawerPhysicalMethodBalancesTests
{
    private static CashDrawerController CreateController(ICashDrawerService cashDrawerService)
    {
        var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();
        var settings = Mock.Of<ISystemSettingsService>();
        var coordinator = new CashAdvanceCoordinator(
            salesDb,
            Mock.Of<ISalesService>(),
            cashDrawerService,
            settings);

        return ControllerFactory.CreateCashDrawerController(
            cashDrawerService,
            settings,
            salesDb,
            Mock.Of<ICurrentUserService>(),
            inventoryDb,
            coordinator);
    }

    [Fact]
    public void PhysicalMethodBalancesEndpoint_IsReadOnlyGetForCashRolesWithSessionQuery()
    {
        var method = typeof(CashDrawerController).GetMethod(
            nameof(CashDrawerController.GetPhysicalMethodBalancesAsync));

        Assert.NotNull(method);
        Assert.Null(method!.GetCustomAttribute<AllowAnonymousAttribute>());

        var httpGet = Assert.Single(
            method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false).Cast<HttpGetAttribute>());
        Assert.Equal("physical-method-balances", httpGet.Template);

        var authorize = method.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal("Admin,Manager,Cashier", authorize!.Roles);

        var sessionIdParameter = method.GetParameters().Single(parameter => parameter.Name == "sessionId");
        Assert.Equal(typeof(int), sessionIdParameter.ParameterType);

        var legacy = typeof(CashDrawerController)
            .GetMethod(nameof(CashDrawerController.GetPhysicalBalancesAsync))!
            .GetCustomAttribute<HttpGetAttribute>();
        Assert.NotNull(legacy);
        Assert.Equal("physical-balances", legacy!.Template);
    }

    [Fact]
    public async Task PhysicalMethodBalancesEndpoint_ReturnsTheSameDtoCollection()
    {
        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer
            .Setup(service => service.GetPhysicalMethodBalancesAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PhysicalMethodBalanceDto>
            {
                new()
                {
                    PaymentMethodId = 1,
                    Name = "Efectivo USD",
                    CurrencyCode = "USD",
                    CurrencySymbol = "$",
                    CurrentBalance = 20m,
                    IsActive = true,
                    CanDispense = true
                }
            });

        var controller = CreateController(cashDrawer.Object);

        var response = await controller.GetPhysicalMethodBalancesAsync(42, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var balances = Assert.IsAssignableFrom<IReadOnlyList<PhysicalMethodBalanceDto>>(ok.Value);
        var balance = Assert.Single(balances);
        Assert.Equal(1, balance.PaymentMethodId);
        Assert.Equal(20m, balance.CurrentBalance);
        Assert.Equal("$", balance.CurrencySymbol);
    }

    [Fact]
    public async Task PhysicalMethodBalancesEndpoint_RejectsNonPositiveSessionId()
    {
        var cashDrawer = new Mock<ICashDrawerService>();
        var controller = CreateController(cashDrawer.Object);

        var response = await controller.GetPhysicalMethodBalancesAsync(0, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        cashDrawer.Verify(
            service => service.GetPhysicalMethodBalancesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
