using System;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class AllowNegativeStockSettingsTests
{
    private InventoryDbContext CreateInMemoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new InventoryDbContext(options);
    }

    [Fact]
    public async Task GetAllowNegativeStock_WhenNotConfigured_ReturnsFalseDefault()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateSettings).Returns(true);
        var controller = ControllerFactory.CreateSettingsController(db, mockUser.Object, new SystemSettingsService(db));

        var result = await controller.GetAllowNegativeStock();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<Backend.API.Controllers.AllowNegativeStockResponseDto>(ok.Value);
        Assert.False(dto.Allowed);
    }

    [Fact]
    public async Task SetAllowNegativeStock_ThenGet_PersistsTrue()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateSettings).Returns(true);
        var controller = ControllerFactory.CreateSettingsController(db, mockUser.Object, new SystemSettingsService(db));

        await controller.SetAllowNegativeStock(new SetAllowNegativeStockRequest { Allowed = true });

        var result = await controller.GetAllowNegativeStock();
        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<Backend.API.Controllers.AllowNegativeStockResponseDto>(ok.Value);
        Assert.True(dto.Allowed);
    }

    [Fact]
    public async Task SetAllowNegativeStock_AsCashier_ReturnsForbidden()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateSettings).Returns(false);
        var controller = ControllerFactory.CreateSettingsController(db, mockUser.Object, new SystemSettingsService(db));

        var result = await controller.SetAllowNegativeStock(new SetAllowNegativeStockRequest { Allowed = true });

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }
}