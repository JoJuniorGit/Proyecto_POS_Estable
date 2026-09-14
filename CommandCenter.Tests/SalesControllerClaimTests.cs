using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Sales.Module.Entities;
using Sales.Module.Exceptions;
using Sales.Module.Interfaces;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class SalesControllerClaimTests
{
    private static ClaimsPrincipal CreateUser(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static void AttachUser(ControllerBase controller, ClaimsPrincipal user)
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
    }

    private static object? GetPropertyValue(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        Assert.NotNull(property);
        return property.GetValue(instance);
    }

    [Fact]
    public async Task ClaimSale_WhenServiceThrowsSaleLocked_Returns409WithLockInfo()
    {
        var mockSales = new Mock<ISalesService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("7");

        mockSales.Setup(s => s.GetSaleAsync(1))
            .ReturnsAsync(new SaleDto { Id = 1, Status = "OnHold", CashierId = 99 });

        var claimedAt = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
        mockSales.Setup(s => s.ClaimSaleAsync(1, SaleClaimAction.Checkout, 7, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SaleLockedException(1, 9, "Carlos Cajero", "Checkout", claimedAt));

        var controller = new SalesController(mockSales.Object, mockUser.Object);
        AttachUser(controller, CreateUser("7", "Cashier"));

        var result = await controller.ClaimSale(1, new ClaimSaleRequest { Action = "Checkout" });

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(409, conflict.StatusCode);
        Assert.NotNull(conflict.Value);
        Assert.Equal(9, Assert.IsType<int>(GetPropertyValue(conflict.Value!, "claimedByUserId")));
        Assert.Equal("Carlos Cajero", Assert.IsType<string>(GetPropertyValue(conflict.Value!, "claimedByUserName")));
        Assert.Equal("Checkout", Assert.IsType<string>(GetPropertyValue(conflict.Value!, "claimAction")));
        Assert.Equal(claimedAt, Assert.IsType<DateTime>(GetPropertyValue(conflict.Value!, "claimedAtUtc")));
    }

    [Fact]
    public async Task ClaimSale_WithInvalidAction_Returns400()
    {
        var mockSales = new Mock<ISalesService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("7");

        var controller = new SalesController(mockSales.Object, mockUser.Object);
        AttachUser(controller, CreateUser("7", "Cashier"));

        var result = await controller.ClaimSale(1, new ClaimSaleRequest { Action = "None" });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, badRequest.StatusCode);
        Assert.NotNull(badRequest.Value);
        Assert.Equal("La acción debe ser 'Editing' o 'Checkout'.", GetPropertyValue(badRequest.Value!, "message"));
        mockSales.Verify(
            s => s.ClaimSaleAsync(It.IsAny<int>(), It.IsAny<SaleClaimAction>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ClaimSale_WithNumericAction_Returns400()
    {
        var mockSales = new Mock<ISalesService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("7");

        var controller = new SalesController(mockSales.Object, mockUser.Object);
        AttachUser(controller, CreateUser("7", "Cashier"));

        var result = await controller.ClaimSale(1, new ClaimSaleRequest { Action = "1" });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, badRequest.StatusCode);
        Assert.NotNull(badRequest.Value);
        Assert.Equal("La acción debe ser 'Editing' o 'Checkout'.", GetPropertyValue(badRequest.Value!, "message"));
        mockSales.Verify(
            s => s.ClaimSaleAsync(It.IsAny<int>(), It.IsAny<SaleClaimAction>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReleaseSale_WithForceAndCashierRole_Returns403()
    {
        var mockSales = new Mock<ISalesService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("7");

        var controller = new SalesController(mockSales.Object, mockUser.Object);
        AttachUser(controller, CreateUser("7", "Cashier"));

        var result = await controller.ReleaseSale(1, force: true);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, forbidden.StatusCode);
        Assert.NotNull(forbidden.Value);
        Assert.Equal("Solo Administradores o Supervisores pueden liberar el bloqueo de otro cajero.", GetPropertyValue(forbidden.Value!, "message"));
        mockSales.Verify(
            s => s.ReleaseSaleAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReleaseSale_WithForceAndAdminRole_ReturnsOk()
    {
        var mockSales = new Mock<ISalesService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        mockSales.Setup(s => s.ReleaseSaleAsync(1, 1, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaleDto { Id = 1, Status = "OnHold", ClaimAction = "None" });

        var controller = new SalesController(mockSales.Object, mockUser.Object);
        AttachUser(controller, CreateUser("1", "Admin"));

        var result = await controller.ReleaseSale(1, force: true);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var sale = Assert.IsType<SaleDto>(ok.Value);
        Assert.Equal(1, sale.Id);
        Assert.Equal("None", sale.ClaimAction);
    }
}
