using Backend.API.Controllers;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class DailyClosureControllerTests
{
    private static SalesDbContext CreateInMemorySalesContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new SalesDbContext(options);
    }

    private static InventoryDbContext CreateInMemoryInventoryContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    private static DailyClosureController CreateController(
        Mock<IDailyClosureService>? closureService = null)
    {
        closureService ??= new Mock<IDailyClosureService>();
        return new DailyClosureController(
            closureService.Object,
            new Mock<Sales.Module.Interfaces.ICashDrawerService>().Object,
            CreateInMemoryInventoryContext(),
            new Mock<ISystemSettingsService>().Object,
            CreateInMemorySalesContext(),
            new Mock<ICurrentUserService>().Object);
    }

    [Fact]
    public async Task GetExpectedTotals_WhenDefaultDate_Returns400ProblemDetails()
    {
        var controller = CreateController();

        var actionResult = await controller.GetExpectedTotals(default);

        var objectResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(400, objectResult.StatusCode);
        Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    [Fact]
    public async Task GetExpectedTotals_WhenValidDate_ReturnsOk()
    {
        var closureService = new Mock<IDailyClosureService>();
        closureService
            .Setup(s => s.GetExpectedTotalsByPaymentMethodAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new System.Collections.Generic.List<ExpectedTotalDto>());

        var controller = CreateController(closureService);

        var actionResult = await controller.GetExpectedTotals(new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc));

        Assert.IsType<OkObjectResult>(actionResult.Result);
    }
}
