using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.DTOs;
using Backend.API.Attributes;
using Core.Entities;
using Core.Interfaces;
using Core.Logging;
using Inventory.Module.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using CommandCenter.Tests.Builders;
using CommandCenter.Tests.TestHelpers;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ErrorContractTests
{
    private InventoryDbContext GetInMemoryInventoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new InventoryDbContext(options);
    }

    private SalesDbContext GetInMemorySalesDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
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
        var controller = new ShiftsController(
            mockDailyClosure.Object,
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
        var controller = new DailyClosureController(
            mockClosure.Object,
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

        var controller = ControllerFactory.CreateCashDrawerController(
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

        var result = await controller.CloseShiftAsync(request, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(400, problemDetails.Status);
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

        var result = await controller.CloseShiftAsync(request, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(403, problemDetails.Status);
        Assert.NotNull(problemDetails.Detail);
    }

    [Fact]
    public async Task ShiftsController_NoReportFound_ReturnsProblemDetails404()
    {
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockDailyClosure = new Mock<IDailyClosureService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        mockDailyClosure.Setup(c => c.GetClosureAsync(It.IsAny<int>()))
            .ReturnsAsync((DailyClosureResponseDto?)null);

        var controller = CreateShiftsController(mockCashDrawer, mockDailyClosure, mockUser);

        var result = await controller.GetReportByIdAsync(999, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(404, problemDetails.Status);
    }

    [Fact]
    public async Task ShiftsController_AnonymousUserNoReport_ReturnsProblemDetails404()
    {
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockDailyClosure = new Mock<IDailyClosureService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("999");

        mockDailyClosure.Setup(c => c.GetClosureAsync(It.IsAny<int>()))
            .ReturnsAsync((DailyClosureResponseDto?)null);

        var controller = new ShiftsController(
            mockDailyClosure.Object,
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

        var result = await controller.GetReportByIdAsync(1, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(404, problemDetails.Status);
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

        mockDailyClosure.Setup(c => c.GetClosureAsync(1)).ReturnsAsync(ShiftReportMapper.MapClosure(closure));

        var controller = new ShiftsController(
            mockDailyClosure.Object,
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

        var result = await controller.GetReportByIdAsync(1, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(403, problemDetails.Status);
    }

    [Fact]
    public async Task ShiftsController_CurrentReportNoClosures_ReturnsProblemDetails404()
    {
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockDailyClosure = new Mock<IDailyClosureService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var controller = new ShiftsController(
            mockDailyClosure.Object,
            mockUser.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = AdminUser() }
        };

        var result = await controller.GetCurrentReportAsync(CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(404, problemDetails.Status);
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

        var result = await controller.AddTransaction(request, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(400, problemDetails.Status);
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

        var result = await controller.AddTransaction(request, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(403, problemDetails.Status);
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

        var result = await controller.AddTransaction(request, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(400, problemDetails.Status);
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

        var result = await controller.AddTransaction(request, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(400, problemDetails.Status);
    }

    // ---- DailyClosureController error sites ----

    [Fact]
    public async Task DailyClosureController_DriverRole_ReturnsProblemDetails403()
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

        var result = await controller.CreateClosure(request, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(403, problemDetails.Status);
        Assert.NotNull(problemDetails.Detail);
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

        var result = await controller.CreateClosure(request, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(400, problemDetails.Status);
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

        var result = await controller.CreateClosure(request, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(400, problemDetails.Status);
    }

    [Fact]
    public async Task DailyClosureController_NullRequest_ReturnsProblemDetails400()
    {
        var mockClosure = new Mock<IDailyClosureService>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var controller = CreateDailyClosureController(mockClosure, mockCashDrawer, mockUser);

        var result = await controller.CreateClosure(null!, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(400, problemDetails.Status);
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

        mockClosure.Setup(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("El desglose contiene métodos de pago no reconocidos: 999."));

        var controller = new DailyClosureController(
            mockClosure.Object,
            mockUser.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = AdminUser() }
        };

        var request = new CreateClosureRequest
        {
            Details = new List<CreateClosureDetailRequest>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo", ActualAmountBsS = 100m },
                new() { PaymentMethodId = 999, PaymentMethodName = "Invented", ActualAmountBsS = 50m }
            }
        };

        var result = await controller.CreateClosure(request, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(400, problemDetails.Status);
        Assert.Contains("999", problemDetails.Detail);
    }

    [Fact]
    public async Task DailyClosureController_PreviewDate400_KeepsProblemSemantics()
    {
        var mockClosure = new Mock<IDailyClosureService>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var controller = CreateDailyClosureController(mockClosure, mockCashDrawer, mockUser);

        var result = await controller.GetExpectedTotals(default, CancellationToken.None);

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
            .ReturnsAsync(new CashDrawerSessionResponseDto { OpeningExchangeRate = 50m });

        var controller = CreateShiftsController(mockCashDrawer, mockDailyClosure, mockUser);

        var jsonWithExtraFields = @"{
            ""declaredAmounts"": [{""paymentMethodId"": 1, ""amount"": 100}],
            ""cashierName"": ""Juan"",
            ""cashierCedula"": ""V-12345678""
        }";
        var request = System.Text.Json.JsonSerializer.Deserialize<CloseShiftRequest>(jsonWithExtraFields,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var result = await controller.CloseShiftAsync(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public async Task WriteClosedClosureReceipts_IsFailOpen_DoesNotThrowOnWriteFailure()
    {
        var salesDb = GetInMemorySalesDbContext();
        var service = DailyClosureTestHelper.CreateService(salesDb);

        var closure = new DailyClosure
        {
            Id = 999,
            UserId = "Admin",
            Observation = "test",
            ExchangeRate = 50m,
            ClosureDate = DateTime.UtcNow,
            Details = new List<ClosureDetail>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo", ExpectedAmountBsS = 100m, ActualAmountBsS = 100m }
            }
        };

        var exception = await Record.ExceptionAsync(() => service.WriteClosedClosureReceiptsAsync(ShiftReportMapper.MapClosure(closure)));
        Assert.Null(exception);
    }

    [Fact]
    public async Task WriteClosedClosureReceipts_RetryLogsOnFailure()
    {
        var salesDb = GetInMemorySalesDbContext();
        var service = DailyClosureTestHelper.CreateService(salesDb);

        var closure = new DailyClosure
        {
            Id = 999,
            UserId = "Admin",
            Observation = "test",
            ExchangeRate = 50m,
            ClosureDate = DateTime.UtcNow,
            Details = new List<ClosureDetail>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo", ExpectedAmountBsS = 100m, ActualAmountBsS = 100m }
            }
        };

        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var primaryDir = Path.Combine(commonAppData, "CommandCenterPOS", "Closures");
        bool madeReadOnly = false;
        System.Security.AccessControl.DirectorySecurity? savedSecurity = null;
        try
        {
            if (Directory.Exists(primaryDir))
            {
                var dirInfo = new DirectoryInfo(primaryDir);
                savedSecurity = dirInfo.GetAccessControl();
                var readOnlySecurity = new System.Security.AccessControl.DirectorySecurity();
                readOnlySecurity.SetAccessRuleProtection(true, false);
                readOnlySecurity.SetAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    System.Security.Principal.WindowsIdentity.GetCurrent().Name,
                    System.Security.AccessControl.FileSystemRights.Write,
                    System.Security.AccessControl.AccessControlType.Deny));
                dirInfo.SetAccessControl(readOnlySecurity);
                madeReadOnly = true;
            }
        }
        catch { }

        var logBefore = File.Exists(AppLogger.WarnLogPath) ? new FileInfo(AppLogger.WarnLogPath).Length : 0;

        await service.WriteClosedClosureReceiptsAsync(ShiftReportMapper.MapClosure(closure));

        var logAfter = File.Exists(AppLogger.WarnLogPath) ? new FileInfo(AppLogger.WarnLogPath).Length : 0;

        if (madeReadOnly && savedSecurity != null)
        {
            try { new DirectoryInfo(primaryDir).SetAccessControl(savedSecurity); } catch { }
        }

        Assert.True(logAfter > logBefore, $"Expected log entry on receipt write failure (before={logBefore}, after={logAfter})");
    }
}
