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
    public async Task DailyClosure_WhenNoTodayBcvRateAndNoActiveSessionRate_ThrowsInvalidOperationException()
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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await controller.CreateClosure(request));
        Assert.Contains("tasa BCV", ex.Message, StringComparison.OrdinalIgnoreCase);

        // No closure should be persisted and no cash drawer rollover should occur
        mockClosure.Verify(c => c.CreateClosureAsync(It.IsAny<DailyClosure>()), Times.Never);
        mockCashDrawer.Verify(c => c.RolloverSessionAfterClosureAsync(It.IsAny<decimal>()), Times.Never);
    }

    [Fact]
    public async Task CreateClosure_WhenNoTodayBcvRate_ButActiveSessionHasOpeningRate_UsesFallbackRateAndProceeds()
    {
        // 8.2-M2: la tasa de apertura de la sesión activa es la tasa de reserva explícita para
        // cierres sin registro BCV del día (ExchangeRateResolver), evitando distorsiones con 1.0.
        using var salesDb = CreateInMemorySalesDbContext();
        using var inventoryDb = CreateInMemoryInventoryDbContext();
        var mockClosure = new Mock<IDailyClosureService>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        mockCashDrawer.Setup(c => c.GetActiveSessionAsync()).ReturnsAsync(
            new Sales.Module.Entities.CashDrawerSession { OpeningExchangeRate = 50m });
        mockClosure.Setup(c => c.GetExpectedTotalsByPaymentMethodAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ExpectedTotalDto>
            {
                new ExpectedTotalDto { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ExpectedAmountBsS = 1000m }
            });
        mockClosure.Setup(c => c.CreateClosureAsync(It.IsAny<DailyClosure>()))
            .ReturnsAsync((DailyClosure closure) => closure);

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

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        mockClosure.Verify(c => c.CreateClosureAsync(It.IsAny<DailyClosure>()), Times.Once);
        mockCashDrawer.Verify(c => c.RolloverSessionAfterClosureAsync(50m), Times.Once);
    }

    [Fact]
    public async Task ShiftsClose_WhenNoTodayBcvRateAndNoActiveSessionRate_ThrowsInvalidOperationException()
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

        var ex3 = await Assert.ThrowsAsync<InvalidOperationException>(async () => await controller.CloseShift(request));
        Assert.Contains("tasa BCV", ex3.Message, StringComparison.OrdinalIgnoreCase);

        mockDailyClosure.Verify(c => c.CreateClosureAsync(It.IsAny<DailyClosure>()), Times.Never);
        mockCashDrawer.Verify(c => c.RolloverSessionAfterClosureAsync(It.IsAny<decimal>()), Times.Never);
    }
}