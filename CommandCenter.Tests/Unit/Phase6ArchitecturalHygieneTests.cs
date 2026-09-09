using System;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.Common;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class Phase6ArchitecturalHygieneTests
{
    private InventoryDbContext CreateInMemoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new InventoryDbContext(options);
    }

    [Fact]
    public void AppVersionHelper_ReturnsCentralizedVersion()
    {
        var version = AppVersionHelper.CurrentVersion;
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Equal("1.0.0", version);
    }

    [Fact]
    public void TimeZoneHelper_GetTimeZone_WithValidAndInvalidId_BehavesSafely()
    {
        // Null or empty falls back to Venezuela
        var tzNull = TimeZoneHelper.GetTimeZone(null);
        Assert.NotNull(tzNull);
        Assert.Equal(TimeSpan.FromHours(-4), tzNull.BaseUtcOffset);

        var tzEmpty = TimeZoneHelper.GetTimeZone("");
        Assert.NotNull(tzEmpty);
        Assert.Equal(TimeSpan.FromHours(-4), tzEmpty.BaseUtcOffset);

        // Unknown timezone fallback to Venezuela
        var tzUnknown = TimeZoneHelper.GetTimeZone("NonExistent/Timezone_123");
        Assert.NotNull(tzUnknown);
        Assert.Equal(TimeSpan.FromHours(-4), tzUnknown.BaseUtcOffset);
    }

    [Fact]
    public async Task SettingsController_SetExchangeRate_SynchronizesSystemSettingAndExchangeRateHistory()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var currentUserServiceMock = new Mock<ICurrentUserService>();
        currentUserServiceMock.Setup(c => c.CanMutateSettings).Returns(true);

        var controller = new SettingsController(db, currentUserServiceMock.Object, new SystemSettingsService(db));

        var request = new SetExchangeRateRequest { Value = 45.75m };
        var result = await controller.SetExchangeRate(request);

        Assert.IsType<OkObjectResult>(result);

        // Verify SystemSetting was persisted
        var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == "ExchangeRate");
        Assert.NotNull(setting);
        Assert.Equal("45.75", setting.Value);

        // Verify ExchangeRateHistory was synchronized (H-API-7)
        var today = TimeZoneHelper.GetVenezuelaDate();
        var historyRecord = await db.ExchangeRateHistory.FirstOrDefaultAsync(r => r.Date == today);
        Assert.NotNull(historyRecord);
        Assert.Equal(45.75m, historyRecord.Rate);

        // Verify GetExchangeRate returns authoritative history rate
        var getResult = await controller.GetExchangeRate();
        var okResult = Assert.IsType<OkObjectResult>(getResult);
        Assert.NotNull(okResult.Value);
    }
}
