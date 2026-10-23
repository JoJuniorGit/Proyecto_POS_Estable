using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Attributes;
using Backend.API.Controllers;
using Backend.API.Middleware;
using Backend.API.Services;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class DevSecOpsPhase1HardeningTests
{
    private static SalesController CreateController(ISalesService salesService, string role)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(service => service.UserId).Returns("42");

        var controller = new SalesController(salesService, currentUser.Object);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "42"),
            new(ClaimTypes.Role, role)
        };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "mock"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
        return controller;
    }

    [Fact]
    public async Task GetCustomersAsync_WhenRoleIsCashier_MasksCreditLimitUSDToZero()
    {
        var salesService = new Mock<ISalesService>();
        var customers = new List<CustomerDto>
        {
            new() { Id = 1, Name = "Cliente 1", CreditLimitUSD = 1500m },
            new() { Id = 2, Name = "Cliente 2", CreditLimitUSD = 500m }
        };
        salesService.Setup(s => s.GetCustomersAsync(null, 1, 20, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((customers, 2));

        var controller = CreateController(salesService.Object, "Cashier");

        var result = await controller.GetCustomersAsync(cancellationToken: CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result);
        var pagedResult = Assert.IsType<CustomerPagedResultDto>(okResult.Value);

        Assert.All(pagedResult.Items, c => Assert.Equal(0m, c.CreditLimitUSD));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Manager")]
    public async Task GetCustomersAsync_WhenRoleIsAdminOrManager_PreservesCreditLimitUSD(string role)
    {
        var salesService = new Mock<ISalesService>();
        var customers = new List<CustomerDto>
        {
            new() { Id = 1, Name = "Cliente 1", CreditLimitUSD = 1500m },
            new() { Id = 2, Name = "Cliente 2", CreditLimitUSD = 500m }
        };
        salesService.Setup(s => s.GetCustomersAsync(null, 1, 20, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((customers, 2));

        var controller = CreateController(salesService.Object, role);

        var result = await controller.GetCustomersAsync(cancellationToken: CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result);
        var pagedResult = Assert.IsType<CustomerPagedResultDto>(okResult.Value);

        Assert.Equal(1500m, pagedResult.Items[0].CreditLimitUSD);
        Assert.Equal(500m, pagedResult.Items[1].CreditLimitUSD);
    }

    [Fact]
    public async Task GetDefaultCustomerAsync_WhenRoleIsCashier_MasksCreditLimitUSDToZero()
    {
        var salesService = new Mock<ISalesService>();
        var defaultCustomer = new CustomerDto { Id = 1, Name = "Consumidor Final", CreditLimitUSD = 1000m };
        salesService.Setup(s => s.GetDefaultCustomerAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(defaultCustomer);

        var controller = CreateController(salesService.Object, "Cashier");

        var result = await controller.GetDefaultCustomerAsync(CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var customer = Assert.IsType<CustomerDto>(okResult.Value);

        Assert.Equal(0m, customer.CreditLimitUSD);
    }

    [Fact]
    public void CancelCreditSale_HasAuthorizeAttributeRestrictedToAdminAndManagerOnly()
    {
        var method = typeof(SalesController).GetMethod(nameof(SalesController.CancelCreditSaleAsync));
        Assert.NotNull(method);

        var authAttr = method!.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authAttr);
        Assert.Equal("Admin,Manager", authAttr!.Roles);
        Assert.DoesNotContain("Cashier", authAttr.Roles);
    }

    [Theory]
    [InlineData(typeof(SalesController))]
    [InlineData(typeof(ProductsController))]
    [InlineData(typeof(SettingsController))]
    [InlineData(typeof(PaymentMethodsController))]
    [InlineData(typeof(ExchangeRateController))]
    [InlineData(typeof(UsersController))]
    public void AdministrativeAndCoreControllers_HaveRequireSecurityStampValidationAttribute(Type controllerType)
    {
        var attr = controllerType.GetCustomAttribute<RequireSecurityStampValidationAttribute>();
        Assert.NotNull(attr);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task SecurityStampValidationMiddleware_WhenMutatingMethod_ForcesImmediateValidation(string httpMethod)
    {
        var validator = new Mock<ISecurityStampValidator>();
        validator.Setup(v => v.ValidateStampAsync(42, "stamp-123", true))
            .ReturnsAsync(true);

        var context = new DefaultHttpContext();
        context.Request.Method = httpMethod;
        context.Request.Path = "/api/test";

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "42"),
            new("security_stamp", "stamp-123")
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "mock"));

        bool nextCalled = false;
        var middleware = new SecurityStampValidationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, validator.Object);

        Assert.True(nextCalled);
        validator.Verify(v => v.ValidateStampAsync(42, "stamp-123", true), Times.Once);
    }

    [Fact]
    public async Task SecurityStampValidationMiddleware_WhenGetWithoutAttribute_UsesCachedWindow()
    {
        var validator = new Mock<ISecurityStampValidator>();
        validator.Setup(v => v.ValidateStampAsync(42, "stamp-123", false))
            .ReturnsAsync(true);

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/public-read";

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "42"),
            new("security_stamp", "stamp-123")
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "mock"));

        bool nextCalled = false;
        var middleware = new SecurityStampValidationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, validator.Object);

        Assert.True(nextCalled);
        validator.Verify(v => v.ValidateStampAsync(42, "stamp-123", false), Times.Once);
    }
}
