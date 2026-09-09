using System;
using System.Threading.Tasks;
using Backend.API.Controllers;
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
        var controller = new SettingsController(db, mockUser.Object, new SystemSettingsService(db));

        var result = await controller.GetAllowNegativeStock();

        var ok = Assert.IsType<OkObjectResult>(result);
        var allowed = (bool?)ok.Value?.GetType().GetProperty("allowed")?.GetValue(ok.Value);
        Assert.False(allowed);
    }

    [Fact]
    public async Task SetAllowNegativeStock_ThenGet_PersistsTrue()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateSettings).Returns(true);
        var controller = new SettingsController(db, mockUser.Object, new SystemSettingsService(db));

        await controller.SetAllowNegativeStock(new SetAllowNegativeStockRequest { Allowed = true });

        var result = await controller.GetAllowNegativeStock();
        var ok = Assert.IsType<OkObjectResult>(result);
        var allowed = (bool?)ok.Value?.GetType().GetProperty("allowed")?.GetValue(ok.Value);
        Assert.True(allowed);
    }

    [Fact]
    public async Task SetAllowNegativeStock_AsCashier_ReturnsForbidden()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateSettings).Returns(false);
        var controller = new SettingsController(db, mockUser.Object, new SystemSettingsService(db));

        var result = await controller.SetAllowNegativeStock(new SetAllowNegativeStockRequest { Allowed = true });

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }
}