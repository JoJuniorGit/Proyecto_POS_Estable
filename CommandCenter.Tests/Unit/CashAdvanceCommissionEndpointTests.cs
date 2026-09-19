using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Services;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class CashAdvanceCommissionEndpointTests
{
    private static Mock<ISystemSettingsService> SettingsMock(string? transfer, string? cash)
    {
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        if (transfer != null)
        {
            settings.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct)).ReturnsAsync(transfer);
        }
        if (cash != null)
        {
            settings.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct)).ReturnsAsync(cash);
        }
        return settings;
    }

    private static CashAdvanceCoordinator CreateReadCoordinator(SalesDbContext context, ISystemSettingsService settings)
        => new(context, Mock.Of<Sales.Module.Interfaces.ISalesService>(), Mock.Of<Sales.Module.Interfaces.ICashDrawerService>(), settings);

    private static CashDrawerController CreateController(SalesDbContext context, CashAdvanceCoordinator coordinator)
        => ControllerFactory.CreateCashDrawerController(
            Mock.Of<Sales.Module.Interfaces.ICashDrawerService>(),
            Mock.Of<ISystemSettingsService>(),
            context,
            Mock.Of<ICurrentUserService>(),
            TestDatabaseFactory.CreateInventoryDbContext(),
            coordinator);

    private static (CashAdvanceCoordinator coordinator, Sales.Module.Interfaces.ICashDrawerService drawerService) CreateProcessableCoordinator(
        SalesDbContext context,
        ISystemSettingsService settings)
    {
        var inventoryMock = new Mock<IInventoryService>();
        inventoryMock.Setup(i => i.GetCashAdvanceProductAsync())
            .ReturnsAsync(new Product { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        var cashDrawerMock = new Mock<Sales.Module.Interfaces.ICashDrawerService>();
        cashDrawerMock.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var salesService = new SalesService(context, inventoryMock.Object, Mock.Of<IMediator>(), cashDrawerMock.Object, settings);
        var drawerService = new Sales.Module.Services.CashDrawerService(context);

        return (new CashAdvanceCoordinator(context, salesService, drawerService, settings), drawerService);
    }

    [Fact]
    public void GetAdvanceCommission_IsHttpGetWithKebabCaseRoute_OnCashDrawerController()
    {
        var method = typeof(CashDrawerController).GetMethod(nameof(CashDrawerController.GetAdvanceCommission));

        Assert.NotNull(method);
        var httpGet = method!.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .SingleOrDefault();

        Assert.NotNull(httpGet);
        Assert.Equal("advance-commission", httpGet!.Template);
    }

    [Fact]
    public void GetAdvanceCommission_AllowsCashierAndRejectsAnonymousAndDriver()
    {
        var method = typeof(CashDrawerController).GetMethod(nameof(CashDrawerController.GetAdvanceCommission));

        Assert.NotNull(method);
        Assert.Null(Attribute.GetCustomAttribute(method!, typeof(AllowAnonymousAttribute)));

        var authorize = (AuthorizeAttribute?)Attribute.GetCustomAttribute(method!, typeof(AuthorizeAttribute));
        Assert.NotNull(authorize);

        var allowedRoles = authorize!.Roles?.Split(',', StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
        Assert.Contains("Admin", allowedRoles);
        Assert.Contains("Manager", allowedRoles);
        Assert.Contains("Cashier", allowedRoles);
        Assert.DoesNotContain("Driver", allowedRoles);

        var controllerAuthorize = (AuthorizeAttribute?)Attribute.GetCustomAttribute(typeof(CashDrawerController), typeof(AuthorizeAttribute));
        Assert.NotNull(controllerAuthorize);
    }

    [Fact]
    public async Task GetAdvanceCommission_WithConfiguredTransferPercentage_Returns200WithServerValue()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var settings = SettingsMock(transfer: "5.5", cash: "10.0");
        var controller = CreateController(context, CreateReadCoordinator(context, settings.Object));

        var action = await controller.GetAdvanceCommission(isTransfer: true, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<CashAdvanceCommissionDto>(ok.Value);
        Assert.True(dto.IsTransfer);
        Assert.Equal(5.5m, dto.Percentage);
    }

    [Fact]
    public async Task GetAdvanceCommission_WithConfiguredCashPercentage_ReturnsTheOtherChannelValue()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var settings = SettingsMock(transfer: "5.5", cash: "12.25");
        var controller = CreateController(context, CreateReadCoordinator(context, settings.Object));

        var action = await controller.GetAdvanceCommission(isTransfer: false, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<CashAdvanceCommissionDto>(ok.Value);
        Assert.False(dto.IsTransfer);
        Assert.Equal(12.25m, dto.Percentage);
    }

    [Fact]
    public async Task GetAdvanceCommission_WhenUnresolvable_Returns422WithoutPercentageOrDefault()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var settings = SettingsMock(transfer: null, cash: null);
        var controller = CreateController(context, CreateReadCoordinator(context, settings.Object));

        var action = await controller.GetAdvanceCommission(isTransfer: true, CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.Status);

        var serialized = JsonSerializer.Serialize(problem);
        Assert.DoesNotContain("percentage", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isTransfer", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task TryGetCommissionPercentageAsync_WhenUnsetOrInvalid_ReturnsNull(string? configured)
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync(configured);
        var coordinator = CreateReadCoordinator(context, settings.Object);

        Assert.Null(await coordinator.TryGetCommissionPercentageAsync(isTransfer: true));
    }

    [Fact]
    public async Task TryGetCommissionPercentageAsync_ResolvesPerSelectedChannel()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var settings = SettingsMock(transfer: "5.5", cash: "10.0");
        var coordinator = CreateReadCoordinator(context, settings.Object);

        Assert.Equal(5.5m, await coordinator.TryGetCommissionPercentageAsync(isTransfer: true));
        Assert.Equal(10.0m, await coordinator.TryGetCommissionPercentageAsync(isTransfer: false));
    }

    [Fact]
    public async Task ProcessAsync_WhenCommissionUnresolvable_StillThrowsVerbatimMessage()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var settings = SettingsMock(transfer: null, cash: null);
        var (coordinator, drawerService) = CreateProcessableCoordinator(context, settings.Object);
        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ProcessAsync(
                sessionId: session.Id,
                requestedAmountLocal: 1000m,
                paymentMethodId: 2,
                paymentMethodName: "Transferencia Bancaria",
                isTransfer: true,
                exchangeRate: 50.0m));

        Assert.Contains("no está configurada", ex.Message);
        Assert.Contains("CashAdvance.TransferCommissionPct", ex.Message);
    }

    [Fact]
    public async Task PreviewedPercentage_EqualsChargedPercentage()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var settings = SettingsMock(transfer: "5.5", cash: "10.0");
        var (coordinator, drawerService) = CreateProcessableCoordinator(context, settings.Object);
        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        var previewed = await coordinator.TryGetCommissionPercentageAsync(isTransfer: true);
        var charge = await coordinator.ProcessAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia Bancaria",
            isTransfer: true,
            exchangeRate: 50.0m);

        Assert.Equal(5.5m, previewed);
        Assert.Equal(previewed, charge.CommissionPercentage);
    }

    [Fact]
    public async Task ClientGetAdvanceCommission_ReadsTheNewRoute_AndReturnsServerPercentage()
    {
        Uri? requestedUri = null;
        var handler = new MockHttpMessageHandler((request, _) =>
        {
            requestedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"isTransfer\":true,\"percentage\":5.5}", Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var service = new Desktop.Client.Services.CashDrawerService(client);

        var percentage = await service.GetAdvanceCommissionAsync(isTransfer: true);

        Assert.Equal(5.5m, percentage);
        Assert.NotNull(requestedUri);
        Assert.Equal("/api/cashdrawer/advance-commission?isTransfer=true", requestedUri!.PathAndQuery);
    }

    [Fact]
    public async Task ClientGetAdvanceCommission_OnUnprocessableEntity_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent("{\"status\":422}", Encoding.UTF8, "application/json")
            }));

        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var service = new Desktop.Client.Services.CashDrawerService(client);

        Assert.Null(await service.GetAdvanceCommissionAsync(isTransfer: false));
    }
}
