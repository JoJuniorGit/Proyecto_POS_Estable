using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class Phase7ClosureWithoutRateTests
{
    private SalesDbContext CreateInMemorySalesDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private InventoryDbContext CreateInMemoryInventoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    private static ClaimsPrincipal CreateUser(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static void AttachUser(ControllerBase controller, ClaimsPrincipal user)
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
    }

    [Fact]
    public async Task DailyClosure_WithoutTodayBcvRate_ReturnsBadRequestWithClearMessage()
    {
        using var salesDb = CreateInMemorySalesDbContext();
        using var inventoryDb = CreateInMemoryInventoryDbContext();
        var mockClosure = new Mock<IDailyClosureService>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var controller = new DailyClosureController(
            mockClosure.Object,
            mockCashDrawer.Object,
            inventoryDb,
            mockSettings.Object,
            salesDb,
            mockUser.Object);
        AttachUser(controller, CreateUser("1", "Admin"));

        var request = new CreateClosureRequest
        {
            UserId = "1",
            Details = new List<CreateClosureDetailRequest>
            {
                new CreateClosureDetailRequest { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ActualAmountBsS = 1000m }
            }
        };

        var result = await controller.CreateClosure(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
        var message = badRequest.Value?.GetType().GetProperty("Message")?.GetValue(badRequest.Value)?.ToString();
        Assert.Contains("tasa BCV", message, StringComparison.OrdinalIgnoreCase);

        // No closure should be persisted and no cash drawer rollover should occur
        mockClosure.Verify(c => c.CreateClosureAsync(It.IsAny<DailyClosure>()), Times.Never);
        mockCashDrawer.Verify(c => c.RolloverSessionAfterClosureAsync(It.IsAny<decimal>()), Times.Never);
    }

    [Fact]
    public async Task DailyClosure_WithBcvRate_ButNoRegistration_IsBlockedEvenWithActiveSessionOpeningRate()
    {
        // Even if an active cash drawer session has an opening rate, a closure without a
        // registered BCV rate for today must be blocked (8.2-M2 stricter than fallback).
        using var salesDb = CreateInMemorySalesDbContext();
        using var inventoryDb = CreateInMemoryInventoryDbContext();
        var mockClosure = new Mock<IDailyClosureService>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        mockCashDrawer.Setup(c => c.GetActiveSessionAsync()).ReturnsAsync(
            new Sales.Module.Entities.CashDrawerSession { OpeningExchangeRate = 50m });

        var controller = new DailyClosureController(
            mockClosure.Object,
            mockCashDrawer.Object,
            inventoryDb,
            mockSettings.Object,
            salesDb,
            mockUser.Object);
        AttachUser(controller, CreateUser("1", "Admin"));

        var request = new CreateClosureRequest
        {
            UserId = "1",
            Details = new List<CreateClosureDetailRequest>
            {
                new CreateClosureDetailRequest { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ActualAmountBsS = 1000m }
            }
        };

        var result = await controller.CreateClosure(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
        mockClosure.Verify(c => c.CreateClosureAsync(It.IsAny<DailyClosure>()), Times.Never);
    }

    [Fact]
    public async Task ShiftsClose_WithoutTodayBcvRate_ReturnsBadRequestWithClearMessage()
    {
        using var salesDb = CreateInMemorySalesDbContext();
        using var inventoryDb = CreateInMemoryInventoryDbContext();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockDailyClosure = new Mock<IDailyClosureService>();
        var mockPaymentMethod = new Mock<IPaymentMethodService>();
        var mockSettings = new Mock<ISystemSettingsService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var controller = new ShiftsController(
            mockCashDrawer.Object,
            mockDailyClosure.Object,
            mockPaymentMethod.Object,
            mockSettings.Object,
            inventoryDb,
            salesDb,
            mockUser.Object);
        AttachUser(controller, CreateUser("1", "Admin"));

        var request = new CloseShiftRequest
        {
            CashierName = "Cajero",
            DeclaredAmounts = new List<DeclaredAmountDto>()
        };

        var result = await controller.CloseShift(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var message = badRequest.Value?.ToString();
        Assert.Contains("tasa BCV", message, StringComparison.OrdinalIgnoreCase);

        mockDailyClosure.Verify(c => c.CreateClosureAsync(It.IsAny<DailyClosure>()), Times.Never);
        mockCashDrawer.Verify(c => c.RolloverSessionAfterClosureAsync(It.IsAny<decimal>()), Times.Never);
    }
}