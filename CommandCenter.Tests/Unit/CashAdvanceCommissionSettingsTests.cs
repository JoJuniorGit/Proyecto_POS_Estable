using System;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Admin/Manager read and Admin-only write of the cash advance commission percentages. The
/// coordinator rejects the advance when either channel is missing or zero, so the settings
/// endpoint must persist both keys atomically.
/// </summary>
public sealed class CashAdvanceCommissionSettingsTests
{
    private static InventoryDbContext CreateInMemoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new InventoryDbContext(options);
    }

    private static SettingsController CreateController(
        InventoryDbContext db,
        bool canMutateSettings)
    {
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(user => user.CanMutateSettings).Returns(canMutateSettings);
        return ControllerFactory.CreateSettingsController(
            db,
            mockUser.Object,
            new SystemSettingsService(db));
    }

    [Fact]
    public async Task Get_WhenNotConfigured_ReturnsZeroForBothChannels()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var controller = CreateController(db, canMutateSettings: true);

        var result = await controller.GetCashAdvanceCommissionAsync();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<CashAdvanceCommissionSettingsDto>(ok.Value);
        Assert.Equal(0m, dto.TransferCommissionPct);
        Assert.Equal(0m, dto.CashCommissionPct);
    }

    [Fact]
    public async Task Set_AsAdmin_PersistsBothChannelsAndGetReturnsThem()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var controller = CreateController(db, canMutateSettings: true);

        var setResult = await controller.SetCashAdvanceCommissionAsync(new CashAdvanceCommissionSettingsDto
        {
            TransferCommissionPct = 7m,
            CashCommissionPct = 5m
        });

        Assert.IsType<OkObjectResult>(setResult.Result);

        var getResult = await controller.GetCashAdvanceCommissionAsync();
        var ok = Assert.IsType<OkObjectResult>(getResult.Result);
        var dto = Assert.IsType<CashAdvanceCommissionSettingsDto>(ok.Value);
        Assert.Equal(7m, dto.TransferCommissionPct);
        Assert.Equal(5m, dto.CashCommissionPct);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-1, 5)]
    [InlineData(101, 5)]
    [InlineData(7, 0)]
    [InlineData(7, -1)]
    [InlineData(7, 101)]
    public async Task Set_OutOfRange_ReturnsBadRequestAndDoesNotPersist(
        decimal transfer,
        decimal cash)
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var controller = CreateController(db, canMutateSettings: true);

        var result = await controller.SetCashAdvanceCommissionAsync(new CashAdvanceCommissionSettingsDto
        {
            TransferCommissionPct = transfer,
            CashCommissionPct = cash
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
        Assert.False(await db.SystemSettings.AnyAsync());
    }

    [Fact]
    public async Task Set_AsManager_ReturnsForbiddenAndDoesNotPersist()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var controller = CreateController(db, canMutateSettings: false);

        var result = await controller.SetCashAdvanceCommissionAsync(new CashAdvanceCommissionSettingsDto
        {
            TransferCommissionPct = 7m,
            CashCommissionPct = 5m
        });

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.False(await db.SystemSettings.AnyAsync());
    }
}
