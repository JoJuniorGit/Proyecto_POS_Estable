using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Sales.Module.Interfaces;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// T4-prep (Paso 11, S1/S3/S4): contratos HTTP del perfil crediticio y la cartera global.
/// La escritura del perfil queda restringida a Admin/Manager y la lectura de cartera a
/// Admin/Manager/Cashier; el servicio traduce KeyNotFound → 404 y validación → 400.
/// </summary>
public class CreditProfileUpdateControllerTests
{
    [Fact]
    public async Task UpdateCreditProfileAsync_ReturnsUpdatedProfile()
    {
        var expected = new CreditProfileDto
        {
            CustomerId = 5,
            CustomerName = "Cliente Uno",
            IsCreditEnabled = true,
            CreditLimitUSD = 750m,
            PaymentTermDays = 45,
            CreditGraceDays = 5,
            CreditStatus = Core.Entities.CreditStatus.ManuallyBlocked
        };
        var service = new Mock<ICreditService>();
        service.Setup(instance => instance.UpdateCustomerCreditProfileAsync(
                It.IsAny<int>(), It.IsAny<CustomerCreditProfileUpdateDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = CreateController();

        var result = await controller.UpdateCreditProfileAsync(
            5,
            new CustomerCreditProfileUpdateDto { IsCreditEnabled = true, CreditLimitUSD = 750m, PaymentTermDays = 45, CreditGraceDays = 5 },
            service.Object,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(expected, ok.Value);
    }

    [Fact]
    public async Task UpdateCreditProfileAsync_KeyNotFound_Returns404()
    {
        var service = new Mock<ICreditService>();
        service.Setup(instance => instance.UpdateCustomerCreditProfileAsync(
                It.IsAny<int>(), It.IsAny<CustomerCreditProfileUpdateDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Cliente 5 no encontrado."));
        var controller = CreateController();

        var result = await controller.UpdateCreditProfileAsync(
            5,
            new CustomerCreditProfileUpdateDto(),
            service.Object,
            CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(notFound.Value);
        Assert.Contains("Cliente", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateCreditProfileAsync_ValidationError_Returns400()
    {
        var service = new Mock<ICreditService>();
        service.Setup(instance => instance.UpdateCustomerCreditProfileAsync(
                It.IsAny<int>(), It.IsAny<CustomerCreditProfileUpdateDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("El plazo de pago debe estar entre 1 y 365 días continuos."));
        var controller = CreateController();

        var result = await controller.UpdateCreditProfileAsync(
            5,
            new CustomerCreditProfileUpdateDto { PaymentTermDays = 0 },
            service.Object,
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Contains("plazo", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCreditReceivablesAsync_ReturnsFilteredList()
    {
        var expected = new CreditReceivablesListDto
        {
            TodayRate = 50m,
            TotalOpenUSD = 100m,
            OpenCount = 1
        };
        expected.Items.Add(new CreditReceivableListItemDto
        {
            ReceivableId = 10,
            CustomerId = 5,
            CustomerName = "Cliente Uno",
            SaleId = 20,
            InvoiceNumber = 1001,
            BalanceUSD = 100m
        });
        var service = new Mock<ICreditService>();
        service.Setup(instance => instance.GetCreditReceivablesAsync(
                It.IsAny<CreditReceivablesFilterDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = CreateController();

        var result = await controller.GetCreditReceivablesAsync(
            new CreditReceivablesFilterDto { CustomerId = 5 },
            service.Object,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(expected, ok.Value);
        service.Verify(instance => instance.GetCreditReceivablesAsync(
            It.Is<CreditReceivablesFilterDto>(filter => filter.CustomerId == 5), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetCreditReceivablesAsync_InvalidStatus_Returns400()
    {
        var service = new Mock<ICreditService>();
        service.Setup(instance => instance.GetCreditReceivablesAsync(
                It.IsAny<CreditReceivablesFilterDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Estado de cuenta por cobrar inválido: 'Bogus'."));
        var controller = CreateController();

        var result = await controller.GetCreditReceivablesAsync(
            new CreditReceivablesFilterDto { Status = "Bogus" },
            service.Object,
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Contains("Estado", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreditProfileWrite_IsRestrictedToAdminAndManager()
    {
        var method = typeof(SalesController).GetMethod(nameof(SalesController.UpdateCreditProfileAsync))!;
        var authorize = method.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal("Admin,Manager", authorize!.Roles);
    }

    [Fact]
    public void CreditReceivablesRead_IsRestrictedToAdminManagerAndCashier()
    {
        var method = typeof(SalesController).GetMethod(nameof(SalesController.GetCreditReceivablesAsync))!;
        var authorize = method.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal("Admin,Manager,Cashier", authorize!.Roles);
    }

    private static SalesController CreateController()
    {
        var salesService = new Mock<ISalesService>();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(instance => instance.UserId).Returns("9");
        currentUser.SetupGet(instance => instance.UserRole).Returns(Core.Entities.UserRole.Admin);

        var controller = new SalesController(salesService.Object, currentUser.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "9"), new Claim(ClaimTypes.Role, "Admin") },
                    "mock"))
            }
        };
        return controller;
    }
}
