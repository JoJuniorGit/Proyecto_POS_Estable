using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using CommandCenter.Tests.TestHelpers;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class CancellationPropagationTests
{
    private static readonly CancellationToken Cancelled = new(canceled: true);

    private static ClaimsPrincipal AdminUser() => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, "1"),
        new Claim(ClaimTypes.Role, "Admin")
    }, "TestAuth"));

    private static ControllerContext HttpContextOf(ClaimsPrincipal user) =>
        new() { HttpContext = new DefaultHttpContext { User = user } };

    private static void AssertTokenIsLastParameter(MethodInfo method)
    {
        var parameters = method.GetParameters();
        Assert.True(parameters.Length > 0, $"{method.DeclaringType?.Name}.{method.Name} has no parameters");
        Assert.Equal(typeof(CancellationToken), parameters[^1].ParameterType);
    }

    [Fact]
    public async Task ShiftsController_GetReportById_ForwardsRequestTokenToService()
    {
        var closureService = new Mock<IDailyClosureService>();
        var closure = new DailyClosureResponseDto(7, default, "1", 50m, 0m, 0m, 0m, null, Array.Empty<ClosureDetailResponseDto>());
        closureService.Setup(s => s.GetClosureAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(closure);
        closureService.Setup(s => s.GetCashierDisplayNameAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync("Admin");

        var controller = new ShiftsController(closureService.Object, Mock.Of<ICurrentUserService>())
        {
            ControllerContext = HttpContextOf(AdminUser())
        };

        using var cts = new CancellationTokenSource();
        var result = await controller.GetReportByIdAsync(7, cts.Token);

        Assert.IsType<OkObjectResult>(result);
        closureService.Verify(s => s.GetClosureAsync(7, cts.Token), Times.Once);
        closureService.Verify(s => s.GetCashierDisplayNameAsync(1, cts.Token), Times.Once);
    }

    [Fact]
    public async Task ShiftsController_GetCurrentReport_ForwardsRequestTokenToService()
    {
        var closureService = new Mock<IDailyClosureService>();
        var closure = new DailyClosureResponseDto(7, default, "1", 50m, 0m, 0m, 0m, null, Array.Empty<ClosureDetailResponseDto>());
        closureService.Setup(s => s.GetLatestClosureAsync(It.IsAny<CancellationToken>())).ReturnsAsync(closure);
        closureService.Setup(s => s.GetClosureAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(closure);

        var controller = new ShiftsController(closureService.Object, Mock.Of<ICurrentUserService>())
        {
            ControllerContext = HttpContextOf(AdminUser())
        };

        using var cts = new CancellationTokenSource();
        var result = await controller.GetCurrentReportAsync(cts.Token);

        Assert.IsType<OkObjectResult>(result);
        closureService.Verify(s => s.GetLatestClosureAsync(cts.Token), Times.Once);
        closureService.Verify(s => s.GetClosureAsync(7, cts.Token), Times.Once);
    }

    [Fact]
    public async Task DailyClosureController_GetExpectedTotals_ForwardsRequestTokenToService()
    {
        var closureService = new Mock<IDailyClosureService>();
        var date = new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc);
        closureService.Setup(s => s.GetExpectedTotalsByPaymentMethodAsync(date, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExpectedTotalDto>());

        var controller = new DailyClosureController(closureService.Object, Mock.Of<ICurrentUserService>());

        using var cts = new CancellationTokenSource();
        var result = await controller.GetExpectedTotals(date, cts.Token);

        Assert.IsType<OkObjectResult>(result.Result);
        closureService.Verify(s => s.GetExpectedTotalsByPaymentMethodAsync(date, cts.Token), Times.Once);
    }

    [Fact]
    public async Task DailyClosureController_GetClosure_ForwardsRequestTokenToService()
    {
        var closureService = new Mock<IDailyClosureService>();
        closureService.Setup(s => s.GetClosureAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DailyClosureResponseDto(7, default, null, 0m, 0m, 0m, 0m, null, Array.Empty<ClosureDetailResponseDto>()));

        var controller = new DailyClosureController(closureService.Object, Mock.Of<ICurrentUserService>());

        using var cts = new CancellationTokenSource();
        var result = await controller.GetClosure(7, cts.Token);

        Assert.IsType<OkObjectResult>(result.Result);
        closureService.Verify(s => s.GetClosureAsync(7, cts.Token), Times.Once);
    }

    [Fact]
    public async Task CashDrawerController_GetActiveSession_ForwardsRequestTokenToService()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();

        var drawerService = new Mock<ICashDrawerService>();
        drawerService.Setup(s => s.GetActiveSessionWithTransactionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((CashDrawerSessionResponseDto?)null);

        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        var coordinator = new CashAdvanceCoordinator(salesDb, Mock.Of<ISalesService>(), drawerService.Object, settings.Object);
        var controller = ControllerFactory.CreateCashDrawerController(
            drawerService.Object, settings.Object, salesDb, Mock.Of<ICurrentUserService>(), inventoryDb, coordinator);
        controller.ControllerContext = HttpContextOf(AdminUser());

        using var cts = new CancellationTokenSource();
        var result = await controller.GetActiveSession(cts.Token);

        Assert.IsType<OkObjectResult>(result.Result);
        drawerService.Verify(s => s.GetActiveSessionWithTransactionsAsync(cts.Token), Times.Once);
    }

    [Fact]
    public async Task CashDrawerController_AddTransaction_ForwardsRequestTokenToService()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();

        var drawerService = new Mock<ICashDrawerService>();
        drawerService.Setup(s => s.AddTransactionAsync(
                It.IsAny<int>(), It.IsAny<CashTransactionType>(), It.IsAny<CashTransactionSource>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashTransactionResponseDto { Id = 1, SessionId = 3 });

        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(u => u.UserRole).Returns(Core.Entities.UserRole.Admin);

        var coordinator = new CashAdvanceCoordinator(salesDb, Mock.Of<ISalesService>(), drawerService.Object, settings.Object);
        var controller = ControllerFactory.CreateCashDrawerController(
            drawerService.Object, settings.Object, salesDb, currentUser.Object, inventoryDb, coordinator);
        controller.ControllerContext = HttpContextOf(AdminUser());

        using var cts = new CancellationTokenSource();
        var result = await controller.AddTransaction(new AddTransactionRequest
        {
            SessionId = 3,
            Type = CashTransactionType.Income,
            Source = CashTransactionSource.ManualAdjustment,
            AmountLocal = 100m,
            ExchangeRate = 50m,
            Description = "Ingreso manual"
        }, cts.Token);

        Assert.IsType<OkObjectResult>(result.Result);
        drawerService.Verify(s => s.AddTransactionAsync(
            3, CashTransactionType.Income, CashTransactionSource.ManualAdjustment,
            100m, 2m, 50m, "Ingreso manual", null, true, null, cts.Token), Times.Once);
    }

    [Fact]
    public async Task CashDrawerController_ProcessCashAdvance_ForwardsRequestTokenToDrawerWrites()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();

        var drawerService = new Mock<ICashDrawerService>();
        drawerService.Setup(s => s.GetCurrentBalanceLocalAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(1000m);
        drawerService.Setup(s => s.AddTransactionAsync(
                It.IsAny<int>(), It.IsAny<CashTransactionType>(), It.IsAny<CashTransactionSource>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashTransactionResponseDto { Id = 1, SessionId = 3 });

        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct)).ReturnsAsync("5");
        settings.Setup(s => s.GetSettingAsync("SelectedTimeZoneId")).ReturnsAsync((string?)null);

        var sales = new Mock<ISalesService>();
        sales.Setup(s => s.CreateCashAdvanceSaleAsync(
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<decimal>(), It.IsAny<int?>(), It.IsAny<string>(),
                It.IsAny<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?>()))
            .ReturnsAsync((SaleDto)null!);

        var coordinator = new CashAdvanceCoordinator(salesDb, sales.Object, drawerService.Object, settings.Object);
        var controller = ControllerFactory.CreateCashDrawerController(
            drawerService.Object, settings.Object, salesDb, Mock.Of<ICurrentUserService>(), inventoryDb, coordinator);
        controller.ControllerContext = HttpContextOf(AdminUser());

        using var cts = new CancellationTokenSource();
        var result = await controller.ProcessCashAdvance(new CashAdvanceRequest
        {
            SessionId = 3,
            RequestedAmountLocal = 100m,
            PaymentMethodId = 1,
            PaymentMethodName = "Efectivo Bs.S",
            ExchangeRate = 50m,
            UserName = "Admin"
        }, cts.Token);

        Assert.IsType<OkObjectResult>(result.Result);
        drawerService.Verify(s => s.GetCurrentBalanceLocalAsync(3, cts.Token), Times.Once);
        drawerService.Verify(s => s.AddTransactionAsync(
            3, CashTransactionType.Expense, CashTransactionSource.CashAdvance,
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string>(),
            It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<int?>(), cts.Token), Times.Once);
    }

    [Fact]
    public void TouchedActionsAndHelpers_DeclareCancellationTokenAsLastParameter()
    {
        var actions = new (Type Type, string Method)[]
        {
            (typeof(ShiftsController), nameof(ShiftsController.CloseShiftAsync)),
            (typeof(ShiftsController), nameof(ShiftsController.GetCurrentReportAsync)),
            (typeof(ShiftsController), nameof(ShiftsController.GetReportByIdAsync)),
            (typeof(DailyClosureController), nameof(DailyClosureController.GetExpectedTotals)),
            (typeof(DailyClosureController), nameof(DailyClosureController.CreateClosure)),
            (typeof(DailyClosureController), nameof(DailyClosureController.GetClosure)),
            (typeof(CashDrawerController), nameof(CashDrawerController.GetActiveSession)),
            (typeof(CashDrawerController), nameof(CashDrawerController.GetHistory)),
            (typeof(CashDrawerController), nameof(CashDrawerController.OpenSession)),
            (typeof(CashDrawerController), nameof(CashDrawerController.CloseSession)),
            (typeof(CashDrawerController), nameof(CashDrawerController.GetCurrentBalance)),
            (typeof(CashDrawerController), nameof(CashDrawerController.AddTransaction)),
            (typeof(CashDrawerController), nameof(CashDrawerController.ProcessCashAdvance))
        };

        foreach (var (type, name) in actions)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.NotNull(method);
            AssertTokenIsLastParameter(method!);
        }

        foreach (var name in new[] { "ResolveAnchoredRateAsync", "MapLocalTimesAsync" })
        {
            var helper = typeof(CashDrawerController).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.NotNull(helper);
            AssertTokenIsLastParameter(helper!);
        }
    }

    [Fact]
    public async Task DailyClosureService_CreateClosureFromCommandAsync_WhenTokenCancelled_ThrowsAndPersistsNothing()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            if (!await context.PaymentMethods.AnyAsync(p => p.Id == 1))
            {
                context.PaymentMethods.Add(new PaymentMethod
                {
                    Id = 1,
                    Name = "Efectivo Bs.S",
                    IsCash = true,
                    IsActive = true,
                    IsDeleted = false,
                    DisplayOrder = 1
                });
                await context.SaveChangesAsync();
            }

            var (rateProvider, cashDrawer) = DailyClosureTestHelper.CreateMocks();
            var service = new DailyClosureService(context, rateProvider.Object, cashDrawer.Object);
            var command = new CreateClosureCommand(
                DateTime.UtcNow,
                "Admin",
                "",
                new List<DeclaredPaymentAmount> { new(1, 100m) });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.CreateClosureFromCommandAsync(command, Cancelled));

            context.ChangeTracker.Clear();
            Assert.Equal(0, await context.DailyClosures.AsNoTracking().CountAsync());
            cashDrawer.Verify(c => c.RolloverSessionAfterClosureAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task DailyClosureService_GetClosureAsync_WhenTokenCancelled_ThrowsOperationCanceled()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            var (rateProvider, cashDrawer) = DailyClosureTestHelper.CreateMocks();
            var service = new DailyClosureService(context, rateProvider.Object, cashDrawer.Object);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.GetClosureAsync(1, Cancelled));
        }
    }

    [Fact]
    public async Task CashDrawerService_AddTransactionAsync_WhenTokenCancelled_ThrowsAndPersistsNothing()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            var service = new CashDrawerService(context);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.AddTransactionAsync(
                    3,
                    CashTransactionType.Income,
                    CashTransactionSource.ManualAdjustment,
                    100m,
                    2m,
                    50m,
                    "Ingreso manual",
                    cancellationToken: Cancelled));

            context.ChangeTracker.Clear();
            Assert.Equal(0, await context.CashTransactions.AsNoTracking().CountAsync());
        }
    }

    [Fact]
    public void TouchedAsyncTypes_DoNotBlockSynchronouslyOnAsyncPaths()
    {
        Type[] touchedTypes =
        [
            typeof(ShiftsController),
            typeof(DailyClosureController),
            typeof(CashDrawerController),
            typeof(DailyClosureService),
            typeof(CashDrawerService),
            typeof(CashAdvanceCoordinator)
        ];

        var offenders = CollectBlockingCallOffenders(touchedTypes);

        Assert.Empty(offenders);
    }

    private sealed class AsyncBodyBlockingProbe
    {
        public static async Task RunAsync()
        {
            await Task.Yield();
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void BlockingScanner_DetectsBlockingCallInsideAsyncStateMachine()
    {
        var offenders = CollectBlockingCallOffenders(new[] { typeof(AsyncBodyBlockingProbe) });

        Assert.Contains(offenders, offender => offender.Contains("Thread.Sleep"));
    }

    private static List<string> CollectBlockingCallOffenders(IEnumerable<Type> touchedTypes)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var offenders = new List<string>();

        foreach (var type in touchedTypes)
        {
            foreach (var scannedType in EnumerateTypeAndNestedStateMachines(type))
            {
                foreach (var method in scannedType.GetMethods(declared))
                {
                    var il = method.GetMethodBody()?.GetILAsByteArray();
                    if (il is null) continue;

                    MethodBase? previousCall = null;

                    for (int i = 0; i < il.Length - 4; i++)
                    {
                        if (il[i] != 0x28 && il[i] != 0x6F) continue;

                        MethodBase? called;
                        try
                        {
                            called = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1), null, null);
                        }
                        catch (ArgumentException)
                        {
                            continue;
                        }

                        if (called is null) continue;

                        string? blocking = called.Name switch
                        {
                            "get_Result" when called.DeclaringType?.Namespace == "System.Threading.Tasks" => "Task.Result",
                            "Wait" when typeof(Task).IsAssignableFrom(called.DeclaringType) => "Task.Wait",
                            "WaitAll" or "WaitAny" when called.DeclaringType == typeof(Task) => "Task." + called.Name,
                            "Sleep" when called.DeclaringType == typeof(Thread) => "Thread.Sleep",
                            "GetResult" when called.DeclaringType?.Name.Contains("Awaiter") == true
                                && previousCall?.Name == "GetAwaiter" => "GetAwaiter().GetResult",
                            _ => null
                        };

                        if (blocking is not null)
                        {
                            offenders.Add($"{type.Name}.{scannedType.Name}.{method.Name} -> {blocking}");
                        }

                        previousCall = called;
                    }
                }
            }
        }

        return offenders;
    }

    private static IEnumerable<Type> EnumerateTypeAndNestedStateMachines(Type type)
    {
        yield return type;

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            yield return nested;
        }
    }

    [Fact]
    public void MainWindow_OnClosing_IsNotAsyncVoid()
    {
        var method = typeof(Desktop.Client.MainWindow).GetMethod(
            "OnClosing", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
        Assert.Null(method.GetCustomAttribute<AsyncStateMachineAttribute>());
    }

    [Fact]
    public void MainWindow_RunShutdownAsync_ReturnsObservableTask()
    {
        var method = typeof(Desktop.Client.MainWindow).GetMethod(
            "RunShutdownAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method!.ReturnType);
        Assert.False(IsAsyncVoid(method));
    }

    private static bool IsAsyncVoid(MethodInfo method) =>
        method.ReturnType == typeof(void) && method.GetCustomAttribute<AsyncStateMachineAttribute>() is not null;
}
