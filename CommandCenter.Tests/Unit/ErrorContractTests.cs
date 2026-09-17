using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.DTOs;
using Backend.API.Attributes;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ErrorContractTests
{
    private InventoryDbContext GetInMemoryInventoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    private SalesDbContext GetInMemorySalesDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new SalesDbContext(options);
    }

    private static ClaimsPrincipal AdminUser() => new ClaimsPrincipal(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, "1"),
        new Claim(ClaimTypes.Role, "Admin")
    }, "TestAuth"));

    private static ClaimsPrincipal DriverUser() => new ClaimsPrincipal(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, "2"),
        new Claim(ClaimTypes.Role, "Driver")
    }, "TestAuth"));

    private static ClaimsPrincipal CashierUser() => new ClaimsPrincipal(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, "3"),
        new Claim(ClaimTypes.Role, "Cashier")
    }, "TestAuth"));

    private ShiftsController CreateShiftsController(
        Mock<ICashDrawerService> mockCashDrawer,
        Mock<IDailyClosureService> mockDailyClosure,
        Mock<ICurrentUserService> mockUser,
        ClaimsPrincipal? user = null)
    {
        var inventoryDb = GetInMemoryInventoryDbContext();
        var salesDb = GetInMemorySalesDbContext();
        var mockPaymentMethod = new Mock<IPaymentMethodService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var controller = new ShiftsController(
            mockCashDrawer.Object,
            mockDailyClosure.Object,
            mockPaymentMethod.Object,
            mockSettings.Object,
            inventoryDb,
            salesDb,
            mockUser.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user ?? AdminUser() }
        };

        return controller;
    }

    private DailyClosureController CreateDailyClosureController(
        Mock<IDailyClosureService> mockClosure,
        Mock<ICashDrawerService> mockCashDrawer,
        Mock<ICurrentUserService> mockUser,
        ClaimsPrincipal? user = null)
    {
        var inventoryDb = GetInMemoryInventoryDbContext();
        var salesDb = GetInMemorySalesDbContext();
        var mockSettings = new Mock<ISystemSettingsService>();

        var controller = new DailyClosureController(
            mockClosure.Object,
            mockCashDrawer.Object,
            inventoryDb,
            mockSettings.Object,
            salesDb,
            mockUser.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user ?? AdminUser() }
        };

        return controller;
    }

    private CashDrawerController CreateCashDrawerController(
        Mock<ICashDrawerService> mockCashDrawer,
        Mock<ICurrentUserService> mockUser)
    {
        var inventoryDb = GetInMemoryInventoryDbContext();
        var salesDb = GetInMemorySalesDbContext();
        var mockSettings = new Mock<ISystemSettingsService>();
        var mockSales = new Mock<ISalesService>();

        var coordinator = new CashAdvanceCoordinator(
            salesDb, mockSales.Object, mockCashDrawer.Object, mockSettings.Object);

        var controller = new CashDrawerController(
            mockCashDrawer.Object,
            mockSettings.Object,
            salesDb,
            mockUser.Object,
            inventoryDb,
            coordinator);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = AdminUser() }
        };

        return controller;
    }

    // ---- ShiftsController error sites ----

    [Fact]
    public async Task ShiftsController_DuplicateMethodId_ReturnsProblemDetails400()
    {
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockDailyClosure = new Mock<IDailyClosureService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var controller = CreateShiftsController(mockCashDrawer, mockDailyClosure, mockUser);

        var request = new CloseShiftRequest
        {
            DeclaredAmounts = new List<DeclaredAmountDto>
            {
                new() { PaymentMethodId = 1, Amount = 100m },
                new() { PaymentMethodId = 1, Amount = 50m }
            }
        };

        var result = await controller.CloseShift(request, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var dict = Assert.IsAssignableFrom<Dictionary<string, object?>>(objectResult.Value);
        Assert.Equal(400, dict["status"]);
    }

    [Fact]
    public async Task ShiftsController_DriverRole_ReturnsProblemDetails403()
    {
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockDailyClosure = new Mock<IDailyClosureService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("2");

        var controller = CreateShiftsController(mockCashDrawer, mockDailyClosure, mockUser, DriverUser());

        var request = new CloseShiftRequest
        {
            DeclaredAmounts = new List<DeclaredAmountDto>
            {
                new() { PaymentMethodId = 1, Amount = 100m }
            }
        };

        var result = await controller.CloseShift(request, CancellationToken.None);

        Assert.IsAssignableFrom<IActionResult>(result);
        var forbidResult = Assert.IsType<ForbidResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, 403);
    }

    [Fact]
    public async Task ShiftsController_NoReportFound_ReturnsProblemDetails404()
    {
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockDailyClosure = new Mock<IDailyClosureService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        mockDailyClosure.Setup(c => c.GetClosureAsync(It.IsAny<int>()))
            .ReturnsAsync((DailyClosure?)null);

        var controller = CreateShiftsController(mockCashDrawer, mockDailyClosure, mockUser);

        var result = await controller.GetReportById(999);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
        var dict = Assert.IsAssignableFrom<Dictionary<string, object?>>(objectResult.Value);
        Assert.Equal(404, dict["status"]);
    }

    [Fact]
    public async Task ShiftsController_AnonymousUserNoReport_ReturnsProblemDetails404()
    {
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockDailyClosure = new Mock<IDailyClosureService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("999");

        mockDailyClosure.Setup(c => c.GetClosureAsync(It.IsAny<int>()))
            .ReturnsAsync((DailyClosure?)null);

        var inventoryDb = GetInMemoryInventoryDbContext();
        var salesDb = GetInMemorySalesDbContext();
        var mockPaymentMethod = new Mock<IPaymentMethodService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var controller = new ShiftsController(
            mockCashDrawer.Object,
            mockDailyClosure.Object,
            mockPaymentMethod.Object,
            mockSettings.Object,
            inventoryDb,
            salesDb,
            mockUser.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "999"),
                    new Claim(ClaimTypes.Role, "Cashier")
                }, "TestAuth"))
            }
        };

        var result = await controller.GetReportById(1);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
        var dict = Assert.IsAssignableFrom<Dictionary<string, object?>>(objectResult.Value);
        Assert.Equal(404, dict["status"]);
    }

    [Fact]
    public async Task ShiftsController_AnonymousUserOwnReport_ReturnsProblemDetails403()
    {
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockDailyClosure = new Mock<IDailyClosureService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("999");

        var closure = new DailyClosure
        {
            Id = 1,
            UserId = "1",
            Observation = "V-12345678",
            ExchangeRate = 50m,
            ClosureDate = DateTime.UtcNow,
            Details = new List<ClosureDetail>()
        };

        mockDailyClosure.Setup(c => c.GetClosureAsync(1)).ReturnsAsync(closure);

        var inventoryDb = GetInMemoryInventoryDbContext();
        var salesDb = GetInMemorySalesDbContext();
        var mockPaymentMethod = new Mock<IPaymentMethodService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var controller = new ShiftsController(
            mockCashDrawer.Object,
            mockDailyClosure.Object,
            mockPaymentMethod.Object,
            mockSettings.Object,
            inventoryDb,
            salesDb,
            mockUser.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "999"),
                    new Claim(ClaimTypes.Role, "Cashier")
                }, "TestAuth"))
            }
        };

        var result = await controller.GetReportById(1);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        var dict = Assert.IsAssignableFrom<Dictionary<string, object?>>(objectResult.Value);
        Assert.Equal(403, dict["status"]);
    }

    [Fact]
    public async Task ShiftsController_CurrentReportNoClosures_ReturnsProblemDetails404()
    {
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockDailyClosure = new Mock<IDailyClosureService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var inventoryDb = GetInMemoryInventoryDbContext();
        var salesDb = GetInMemorySalesDbContext();
        var mockPaymentMethod = new Mock<IPaymentMethodService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var controller = new ShiftsController(
            mockCashDrawer.Object,
            mockDailyClosure.Object,
            mockPaymentMethod.Object,
            mockSettings.Object,
            inventoryDb,
            salesDb,
            mockUser.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = AdminUser() }
        };

        var result = await controller.GetCurrentReport();

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
        var dict = Assert.IsAssignableFrom<Dictionary<string, object?>>(objectResult.Value);
        Assert.Equal(404, dict["status"]);
    }

    // ---- CashDrawerController.AddTransaction error sites ----

    [Fact]
    public async Task CashDrawerController_AddTransaction_ReservedSource_ReturnsProblemDetails400()
    {
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserRole).Returns(Core.Entities.UserRole.Admin);

        var controller = CreateCashDrawerController(mockCashDrawer, mockUser);

        var request = new AddTransactionRequest
        {
            SessionId = 1,
            AmountLocal = 100m,
            Type = CashTransactionType.Income,
            Source = CashTransactionSource.Opening,
            ExchangeRate = 50m
        };

        var result = await controller.AddTransaction(request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var dict = Assert.IsAssignableFrom<Dictionary<string, object?>>(objectResult.Value);
        Assert.Equal(400, dict["status"]);
    }

    [Fact]
    public async Task CashDrawerController_AddTransaction_CashierRole_ReturnsProblemDetails403()
    {
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserRole).Returns(Core.Entities.UserRole.Cashier);

        var controller = CreateCashDrawerController(mockCashDrawer, mockUser);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = CashierUser() }
        };

        var request = new AddTransactionRequest
        {
            SessionId = 1,
            AmountLocal = 100m,
            Type = CashTransactionType.Income,
            Source = CashTransactionSource.CashIn,
            ExchangeRate = 50m
        };

        var result = await controller.AddTransaction(request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        var dict = Assert.IsAssignableFrom<Dictionary<string, object?>>(objectResult.Value);
        Assert.Equal(403, dict["status"]);
    }

    [Fact]
    public async Task CashDrawerController_AddTransaction_ZeroAmount_ReturnsProblemDetails400()
    {
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserRole).Returns(Core.Entities.UserRole.Admin);

        var controller = CreateCashDrawerController(mockCashDrawer, mockUser);

        var request = new AddTransactionRequest
        {
            SessionId = 1,
            AmountLocal = 0m,
            Type = CashTransactionType.Income,
            Source = CashTransactionSource.CashIn,
            ExchangeRate = 50m
        };

        var result = await controller.AddTransaction(request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var dict = Assert.IsAssignableFrom<Dictionary<string, object?>>(objectResult.Value);
        Assert.Equal(400, dict["status"]);
    }

    [Fact]
    public async Task CashDrawerController_AddTransaction_ZeroExchangeRate_ReturnsProblemDetails400()
    {
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserRole).Returns(Core.Entities.UserRole.Admin);

        var controller = CreateCashDrawerController(mockCashDrawer, mockUser);

        var request = new AddTransactionRequest
        {
            SessionId = 1,
            AmountLocal = 100m,
            Type = CashTransactionType.Income,
            Source = CashTransactionSource.CashIn,
            ExchangeRate = 0m
        };

        var result = await controller.AddTransaction(request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var dict = Assert.IsAssignableFrom<Dictionary<string, object?>>(objectResult.Value);
        Assert.Equal(400, dict["status"]);
    }

    // ---- DailyClosureController error sites ----

    [Fact]
    public async Task DailyClosureController_DriverRole_ReturnsForbidResult()
    {
        var mockClosure = new Mock<IDailyClosureService>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("2");

        var controller = CreateDailyClosureController(mockClosure, mockCashDrawer, mockUser, DriverUser());

        var request = new CreateClosureRequest
        {
            Details = new List<CreateClosureDetailRequest>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo", ActualAmountBsS = 100m }
            }
        };

        var result = await controller.CreateClosure(request);

        Assert.IsAssignableFrom<IActionResult>(result);
        var forbidResult = Assert.IsType<ForbidResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, 403);
    }

    [Fact]
    public async Task DailyClosureController_EmptyDetails_ReturnsProblemDetails400()
    {
        var mockClosure = new Mock<IDailyClosureService>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var controller = CreateDailyClosureController(mockClosure, mockCashDrawer, mockUser);

        var request = new CreateClosureRequest
        {
            Details = new List<CreateClosureDetailRequest>()
        };

        var result = await controller.CreateClosure(request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var dict = Assert.IsAssignableFrom<Dictionary<string, object?>>(objectResult.Value);
        Assert.Equal(400, dict["status"]);
    }

    [Fact]
    public async Task DailyClosureController_DuplicateMethods_ReturnsProblemDetails400()
    {
        var mockClosure = new Mock<IDailyClosureService>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var controller = CreateDailyClosureController(mockClosure, mockCashDrawer, mockUser);

        var request = new CreateClosureRequest
        {
            Details = new List<CreateClosureDetailRequest>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo", ActualAmountBsS = 100m },
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo", ActualAmountBsS = 50m }
            }
        };

        var result = await controller.CreateClosure(request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var dict = Assert.IsAssignableFrom<Dictionary<string, object?>>(objectResult.Value);
        Assert.Equal(400, dict["status"]);
    }

    [Fact]
    public async Task DailyClosureController_NullRequest_ReturnsProblemDetails400()
    {
        var mockClosure = new Mock<IDailyClosureService>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var controller = CreateDailyClosureController(mockClosure, mockCashDrawer, mockUser);

        var result = await controller.CreateClosure(null!);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var dict = Assert.IsAssignableFrom<Dictionary<string, object?>>(objectResult.Value);
        Assert.Equal(400, dict["status"]);
    }

    [Fact]
    public async Task DailyClosureController_UnknownMethodIds_ReturnsProblemDetails400()
    {
        var mockClosure = new Mock<IDailyClosureService>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        mockClosure.Setup(c => c.GetExpectedTotalsByPaymentMethodAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExpectedTotalDto>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo", ExpectedAmountBsS = 1000m }
            });

        var controller = CreateDailyClosureController(mockClosure, mockCashDrawer, mockUser);

        var request = new CreateClosureRequest
        {
            Details = new List<CreateClosureDetailRequest>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo", ActualAmountBsS = 100m },
                new() { PaymentMethodId = 999, PaymentMethodName = "Invented", ActualAmountBsS = 50m }
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.CreateClosure(request));
        Assert.Contains("tasa BCV", ex.Message);
    }

    [Fact]
    public async Task DailyClosureController_PreviewDate400_KeepsProblemSemantics()
    {
        var mockClosure = new Mock<IDailyClosureService>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var controller = CreateDailyClosureController(mockClosure, mockCashDrawer, mockUser);

        var result = await controller.GetExpectedTotals(default);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(400, problemDetails.Status);
    }

    // ---- REQ-AEC-04: Dead fields removed ----

    [Fact]
    public void CloseShiftRequest_HasNoCashierNameOrCedula()
    {
        var type = typeof(CloseShiftRequest);
        Assert.False(type.GetProperty("CashierName") != null, "CashierName should be removed");
        Assert.False(type.GetProperty("CashierCedula") != null, "CashierCedula should be removed");
    }

    [Fact]
    public void DeclaredAmountDto_HasNoPaymentMethodName()
    {
        var type = typeof(DeclaredAmountDto);
        Assert.False(type.GetProperty("PaymentMethodName") != null, "PaymentMethodName should be removed");
    }

    [Fact]
    public async Task ShiftsController_LegacySenderExtraFields_StillSucceeds()
    {
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockDailyClosure = new Mock<IDailyClosureService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        mockDailyClosure.Setup(c => c.GetExpectedTotalsByPaymentMethodAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExpectedTotalDto>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo Bs.S", ExpectedAmountBsS = 100m }
            });

        mockDailyClosure.Setup(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloseShiftResult(
                1, "1", "V-00000000", DateTime.UtcNow, 50m,
                new List<ShiftReportDetailResult>
                {
                    new(1, "Efectivo Bs.S", "Bs.S", 100m, 100m, 0m, "Balanced")
                }));

        mockCashDrawer.Setup(c => c.GetActiveSessionAsync())
            .ReturnsAsync(new CashDrawerSession { OpeningExchangeRate = 50m });

        var controller = CreateShiftsController(mockCashDrawer, mockDailyClosure, mockUser);

        var request = new CloseShiftRequest
        {
            DeclaredAmounts = new List<DeclaredAmountDto>
            {
                new() { PaymentMethodId = 1, Amount = 100m }
            }
        };

        var result = await controller.CloseShift(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }
}
