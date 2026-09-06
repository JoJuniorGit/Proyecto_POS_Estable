using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Hubs;
using Core.Entities;
using Core.Interfaces;
using Desktop.Client.Behaviors;
using Inventory.Module.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class CurrencyFormatSettingsTests
{
    private InventoryDbContext CreateInMemoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new InventoryDbContext(options);
    }

    [Fact]
    public async Task GetCurrencyFormat_WhenNotConfigured_ReturnsVenezuelanDefault()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var mockUserService = new Mock<ICurrentUserService>();
        mockUserService.Setup(u => u.CanMutateSettings).Returns(true);

        var controller = new SettingsController(db, mockUserService.Object);

        var result = await controller.GetCurrencyFormat();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var formatProp = okResult.Value?.GetType().GetProperty("Format")?.GetValue(okResult.Value) as string;
        Assert.Equal("Venezuelan", formatProp);
    }

    [Fact]
    public async Task SetCurrencyFormat_ValidVenezuelan_PersistsAndEmitsSignalR()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var mockUserService = new Mock<ICurrentUserService>();
        mockUserService.Setup(u => u.CanMutateSettings).Returns(true);

        var mockHubContext = new Mock<IHubContext<ExchangeRateHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();

        mockHubContext.SetupGet(h => h.Clients).Returns(mockClients.Object);
        mockClients.SetupGet(c => c.All).Returns(mockClientProxy.Object);

        var controller = new SettingsController(db, mockUserService.Object, mockHubContext.Object);

        var request = new SetCurrencyFormatRequest { Format = "Venezuelan" };
        var result = await controller.SetCurrencyFormat(request);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var formatProp = okResult.Value?.GetType().GetProperty("Format")?.GetValue(okResult.Value) as string;
        Assert.Equal("Venezuelan", formatProp);

        // Verificar en base de datos
        var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == "CurrencyFormat");
        Assert.NotNull(setting);
        Assert.Equal("Venezuelan", setting.Value);

        // Verificar emisión de SignalR
        mockClientProxy.Verify(
            c => c.SendCoreAsync("OnCurrencyFormatUpdated", It.Is<object[]>(o => o.Length == 1 && (string)o[0] == "Venezuelan"), default),
            Times.Once);
    }

    [Fact]
    public async Task SetCurrencyFormat_ValidInternational_PersistsAndEmitsSignalR()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var mockUserService = new Mock<ICurrentUserService>();
        mockUserService.Setup(u => u.CanMutateSettings).Returns(true);

        var mockHubContext = new Mock<IHubContext<ExchangeRateHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();

        mockHubContext.SetupGet(h => h.Clients).Returns(mockClients.Object);
        mockClients.SetupGet(c => c.All).Returns(mockClientProxy.Object);

        var controller = new SettingsController(db, mockUserService.Object, mockHubContext.Object);

        var request = new SetCurrencyFormatRequest { Format = "International" };
        var result = await controller.SetCurrencyFormat(request);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var formatProp = okResult.Value?.GetType().GetProperty("Format")?.GetValue(okResult.Value) as string;
        Assert.Equal("International", formatProp);

        var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == "CurrencyFormat");
        Assert.NotNull(setting);
        Assert.Equal("International", setting.Value);

        mockClientProxy.Verify(
            c => c.SendCoreAsync("OnCurrencyFormatUpdated", It.Is<object[]>(o => o.Length == 1 && (string)o[0] == "International"), default),
            Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Euro")]
    [InlineData("JapaneseYen")]
    public async Task SetCurrencyFormat_InvalidFormat_ReturnsBadRequest(string invalidFormat)
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var mockUserService = new Mock<ICurrentUserService>();
        mockUserService.Setup(u => u.CanMutateSettings).Returns(true);

        var controller = new SettingsController(db, mockUserService.Object);

        var request = new SetCurrencyFormatRequest { Format = invalidFormat };
        var result = await controller.SetCurrencyFormat(request);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SetCurrencyFormat_CashierCannotMutate_ReturnsForbidden()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var mockUserService = new Mock<ICurrentUserService>();
        mockUserService.Setup(u => u.CanMutateSettings).Returns(false); // Cajero

        var controller = new SettingsController(db, mockUserService.Object);

        var request = new SetCurrencyFormatRequest { Format = "International" };
        var result = await controller.SetCurrencyFormat(request);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
    }

    [Fact]
    public void NumericCalculatorBehavior_ParseAmount_CoversAllFormattingRules()
    {
        // Sin separadores -> entero independiente del formato
        Assert.Equal(72915m, NumericCalculatorBehavior.ParseAmount("72915"));
        Assert.Equal(100m, NumericCalculatorBehavior.ParseAmount("100"));

        // Formato Venezolano (punto miles, coma decimal)
        Assert.Equal(172786.94m, NumericCalculatorBehavior.ParseAmount("172.786,94"));
        Assert.Equal(1234567.89m, NumericCalculatorBehavior.ParseAmount("1.234.567,89"));
        Assert.Equal(172786.94m, NumericCalculatorBehavior.ParseAmount("Bs.S 172.786,94"));

        // Formato Internacional (coma miles, punto decimal)
        Assert.Equal(172786.94m, NumericCalculatorBehavior.ParseAmount("172,786.94"));
        Assert.Equal(1234567.89m, NumericCalculatorBehavior.ParseAmount("1,234,567.89"));
        Assert.Equal(172786.94m, NumericCalculatorBehavior.ParseAmount("$ 172,786.94"));

        // Un solo separador (asumido decimal)
        Assert.Equal(172.94m, NumericCalculatorBehavior.ParseAmount("172,94"));
        Assert.Equal(172.94m, NumericCalculatorBehavior.ParseAmount("172.94"));
        Assert.Equal(72915.00m, NumericCalculatorBehavior.ParseAmount("72915,00"));
        Assert.Equal(72915.00m, NumericCalculatorBehavior.ParseAmount("72915.00"));
        Assert.Equal(0.50m, NumericCalculatorBehavior.ParseAmount(",50"));
        Assert.Equal(0.50m, NumericCalculatorBehavior.ParseAmount(".50"));

        // Inválidos / Vacíos -> 0
        Assert.Equal(0m, NumericCalculatorBehavior.ParseAmount(""));
        Assert.Equal(0m, NumericCalculatorBehavior.ParseAmount(null));
        Assert.Equal(0m, NumericCalculatorBehavior.ParseAmount("   "));
        Assert.Equal(0m, NumericCalculatorBehavior.ParseAmount("abc"));
        Assert.Equal(0m, NumericCalculatorBehavior.ParseAmount("172,,94"));
        Assert.Equal(0m, NumericCalculatorBehavior.ParseAmount("172..94"));
    }
}
