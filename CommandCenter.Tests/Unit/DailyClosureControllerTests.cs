using Backend.API.Controllers;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System.Reflection;
using System.Security.Claims;
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
            new Mock<ICurrentUserService>().Object);
    }

    private static Backend.API.Controllers.ShiftsController CreateShiftsController(
        Mock<IDailyClosureService>? closureService = null,
        Mock<ICurrentUserService>? userService = null)
    {
        closureService ??= new Mock<IDailyClosureService>();
        userService ??= new Mock<ICurrentUserService>();
        return new Backend.API.Controllers.ShiftsController(
            closureService.Object,
            userService.Object);
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

    [Fact]
    public async Task CreateClosure_DelegatesToService_AndPersistsNothingDirectly()
    {
        var mockClosure = new Mock<IDailyClosureService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var expectedResult = new CloseShiftResult(
            42, "Admin", "V-12345678", DateTime.UtcNow, 50m,
            new System.Collections.Generic.List<ShiftReportDetailResult>());

        mockClosure.Setup(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var controller = CreateController(mockClosure);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "1"),
                    new Claim(ClaimTypes.Role, "Admin")
                }, "TestAuth"))
            }
        };

        var request = new CreateClosureRequest
        {
            UserId = "1",
            Details = new System.Collections.Generic.List<CreateClosureDetailRequest>
            {
                new CreateClosureDetailRequest { PaymentMethodId = 1, PaymentMethodName = "Efectivo", ActualAmountBsS = 1000m }
            }
        };

        var result = await controller.CreateClosure(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        mockClosure.Verify(c => c.CreateClosureFromCommandAsync(
            It.Is<CreateClosureCommand>(cmd =>
                cmd.UserId == "1" &&
                cmd.Declarations.Count == 1 &&
                cmd.Declarations[0].PaymentMethodId == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateClosure_WhenServiceThrowsDbUpdateException_Returns409ProblemDetails()
    {
        var mockClosure = new Mock<IDailyClosureService>();
        mockClosure.Setup(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("conflict"));

        var controller = CreateController(mockClosure);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "1"),
                    new Claim(ClaimTypes.Role, "Admin")
                }, "TestAuth"))
            }
        };

        var request = new CreateClosureRequest
        {
            Details = new System.Collections.Generic.List<CreateClosureDetailRequest>
            {
                new CreateClosureDetailRequest { PaymentMethodId = 1, ActualAmountBsS = 1000m }
            }
        };

        var result = await controller.CreateClosure(request, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal(409, problemDetails.Status);
    }

    [Fact]
    public async Task CloseShift_WhenServiceThrowsDbUpdateException_Returns409ProblemDetails()
    {
        var mockClosure = new Mock<IDailyClosureService>();
        mockClosure.Setup(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("conflict"));

        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");
        var controller = CreateShiftsController(mockClosure, mockUser);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "1"),
                    new Claim(ClaimTypes.Role, "Admin")
                }, "TestAuth"))
            }
        };

        var request = new CloseShiftRequest
        {
            DeclaredAmounts = new System.Collections.Generic.List<DeclaredAmountDto>
            {
                new DeclaredAmountDto { PaymentMethodId = 1, Amount = 100m }
            }
        };

        var result = await controller.CloseShift(request, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal(409, problemDetails.Status);
    }

    [Fact]
    public void DailyClosureController_HasNoDbContextInConstructor()
    {
        var constructor = typeof(DailyClosureController).GetConstructors().First();
        var parameterTypes = constructor.GetParameters().Select(p => p.ParameterType).ToList();

        Assert.DoesNotContain(parameterTypes, t =>
            t.Name.Contains("DbContext") ||
            t.Name.Contains("SalesDbContext") ||
            t.Name.Contains("InventoryDbContext"));
    }

    [Fact]
    public void ShiftsController_HasNoDbContextInConstructor()
    {
        var constructor = typeof(Backend.API.Controllers.ShiftsController).GetConstructors().First();
        var parameterTypes = constructor.GetParameters().Select(p => p.ParameterType).ToList();

        Assert.DoesNotContain(parameterTypes, t =>
            t.Name.Contains("DbContext") ||
            t.Name.Contains("SalesDbContext") ||
            t.Name.Contains("InventoryDbContext"));
    }

    [Fact]
    public void DailyClosureController_HasNoDbContextFields()
    {
        var fields = typeof(DailyClosureController).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.DoesNotContain(fields, f =>
            f.FieldType.Name.Contains("DbContext"));
    }

    [Fact]
    public void ShiftsController_HasNoDbContextFields()
    {
        var fields = typeof(Backend.API.Controllers.ShiftsController).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.DoesNotContain(fields, f =>
            f.FieldType.Name.Contains("DbContext"));
    }
}
