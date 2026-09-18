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
    public async Task DailyClosure_WhenNoTodayBcvRateAndNoActiveSessionRate_ReturnsBadRequest()
    {
        var mockClosure = new Mock<IDailyClosureService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        mockClosure.Setup(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No se puede cerrar el turno: no existe una tasa BCV registrada para hoy."));

        var controller = new DailyClosureController(
            mockClosure.Object,
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

        var result = await controller.CreateClosure(request, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Contains("tasa BCV", problemDetails.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateClosure_WhenNoTodayBcvRate_ButActiveSessionHasOpeningRate_UsesFallbackRateAndProceeds()
    {
        var mockClosure = new Mock<IDailyClosureService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var expectedResult = new CloseShiftResult(
            1, "Admin", "V-00000000", DateTime.UtcNow, 50m,
            new List<ShiftReportDetailResult>
            {
                new(1, "Efectivo USD", "USD", 20m, 1000m, 0m, "Balanced")
            });

        mockClosure.Setup(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var controller = new DailyClosureController(
            mockClosure.Object,
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

        var result = await controller.CreateClosure(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        mockClosure.Verify(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()), Times.Once);
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

        mockDailyClosure
            .Setup(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No se puede cerrar el turno: no existe una tasa BCV registrada para hoy."));

        var controller = new ShiftsController(
            mockDailyClosure.Object,
            mockUser.Object);
        AttachUser(controller, CreateUser("1", "Admin"));

        var request = new CloseShiftRequest
        {
            DeclaredAmounts = new List<DeclaredAmountDto>()
        };

        var result = await controller.CloseShift(request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Contains("tasa BCV", problemDetails.Detail, StringComparison.OrdinalIgnoreCase);

        mockDailyClosure.Verify(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}