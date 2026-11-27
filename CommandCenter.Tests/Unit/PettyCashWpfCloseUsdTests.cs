using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.Interfaces;
using Core.Services;
using Inventory.Module.Services;
using Moq;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T4 (Paso 13 / 8.188, S2): pass-through del conteo USD opcional en el cierre de caja. El
/// servidor persiste ClosingBalanceUsd solo cuando el request lo informa; sin él se conserva el
/// comportamiento previo.
/// </summary>
public class PettyCashWpfCloseUsdTests
{
    private static (CashDrawerController Controller, Mock<ICashDrawerService> Drawer) CreateController()
    {
        var drawer = new Mock<ICashDrawerService>();
        drawer.Setup(c => c.CloseSessionAsync(
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto
            {
                Id = 1,
                Status = CashDrawerStatus.Closed,
                OpeningBalanceLocal = 500m,
                OpeningExchangeRate = 60m,
                ClosingBalanceLocal = 700m,
                ClosingExchangeRate = 60m
            });

        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        var inventory = new Mock<IInventoryService>();
        inventory.Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0m);

        var controller = new CashDrawerController(
            drawer.Object,
            settings.Object,
            inventory.Object,
            new Mock<IUserService>().Object,
            new Mock<ICurrentUserService>().Object,
            new TimeZoneProvider(settings.Object),
            new CashAdvanceCoordinator(
                TestDatabaseFactory.CreateSalesDbContext(),
                new Mock<ISalesService>().Object,
                drawer.Object,
                settings.Object));

        return (controller, drawer);
    }

    [Fact]
    public async Task CloseSession_WithUsd_PassesOptionalCountToService()
    {
        var (controller, drawer) = CreateController();

        var result = await controller.CloseSessionAsync(
            new CloseSessionRequest
            {
                ActualClosingBalanceLocal = 700m,
                CurrentExchangeRate = 60m,
                ActualClosingBalanceUsd = 25.5m
            },
            CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
        drawer.Verify(c => c.CloseSessionAsync(700m, 60m, 25.5m, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CloseSession_WithoutUsd_PreservesLegacyNullBehavior()
    {
        var (controller, drawer) = CreateController();

        await controller.CloseSessionAsync(
            new CloseSessionRequest
            {
                ActualClosingBalanceLocal = 700m,
                CurrentExchangeRate = 60m
            },
            CancellationToken.None);

        drawer.Verify(c => c.CloseSessionAsync(700m, 60m, null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
