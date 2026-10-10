using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Core.Services;
using Inventory.Module.Services;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.149 (SRE-02): idempotencia en endpoints mutacionales — items, transacción de caja,
/// cierre diario, guard de fecha duplicada e idempotencia de la anulación.
/// </summary>
public class MutationalEndpointsIdempotencyTests
{
    private const string ItemsPath = "/api/sales/1/items";
    private const string TransactionPath = "/api/cashdrawer/transaction";
    private const string ClosurePath = "/api/dailyclosure";

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static SalesController CreateSalesController(SalesDbContext context, Mock<ISalesService>? salesService = null, string userId = "1")
    {
        salesService ??= new Mock<ISalesService>();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(u => u.UserId).Returns(userId);

        return new SalesController(salesService.Object, currentUser.Object, new IdempotencyService(context));
    }

    private static CashDrawerController CreateCashDrawerController(
        SalesDbContext salesDb,
        Inventory.Module.Data.InventoryDbContext inventoryDb,
        ICashDrawerService cashDrawer,
        ISystemSettingsService settings,
        IIdempotencyService idempotencyService)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(u => u.UserId).Returns("1");
        currentUser.Setup(u => u.UserRole).Returns(UserRole.Admin);

        var coordinator = new CashAdvanceCoordinator(salesDb, new Mock<ISalesService>().Object, cashDrawer, settings);
        return new CashDrawerController(
            cashDrawer,
            settings,
            new InventoryService(inventoryDb),
            new UserService(salesDb),
            currentUser.Object,
            new TimeZoneProvider(settings),
            coordinator,
            idempotencyService);
    }

    private static DailyClosureController CreateDailyClosureController(
        IDailyClosureService closureService,
        IIdempotencyService idempotencyService,
        string userId = "1")
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(u => u.UserId).Returns(userId);
        return new DailyClosureController(closureService, currentUser.Object, idempotencyService);
    }

    private static Mock<ISystemSettingsService> CreateSettingsMock()
    {
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        return settings;
    }

    private static void AttachHttpContext(ControllerBase controller, string path, string? idempotencyKey)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "1"),
                new Claim(ClaimTypes.Role, "Admin")
            }, "TestAuth"))
        };

        if (idempotencyKey != null)
        {
            httpContext.Request.Headers["Idempotency-Key"] = idempotencyKey;
        }

        httpContext.Request.Path = path;
        httpContext.Request.Method = "POST";
        httpContext.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private static string? GetCacheLookup(ControllerBase controller)
        => controller.Response.Headers["X-Cache-Lookup"].ToString();

    private static string? GetProblemMessage(object? value)
    {
        if (value is ProblemDetails problem)
        {
            return problem.Extensions.TryGetValue("message", out var message) ? message?.ToString() : problem.Detail;
        }

        return value?.GetType()
            .GetProperty("message", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)
            ?.GetValue(value)?.ToString();
    }

    private static AddItemRequest CreateItemRequest(decimal quantity = 2m) => new()
    {
        ProductId = 10,
        Quantity = quantity,
        ExchangeRate = 50m
    };

    private static AddTransactionRequest CreateTransactionRequest() => new()
    {
        SessionId = 1,
        Type = CashTransactionType.Income,
        Source = CashTransactionSource.SalePayment,
        AmountLocal = 600m,
        ExchangeRate = 50m,
        Description = "Idempotency ledger pin"
    };

    private static CreateClosureRequest CreateClosureRequest(decimal actualAmount = 1000m) => new()
    {
        UserId = "1",
        Details = new List<CreateClosureDetailRequest>
        {
            new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ActualAmountBsS = actualAmount }
        }
    };

    private static CloseShiftResult CreateCloseShiftResult() => new(
        42, "Admin", "V-12345678", DateTime.UtcNow, 50m,
        new List<ShiftReportDetailResult>
        {
            new(1, "Efectivo USD", "USD", 1000m, 1000m, 0m, "Balanced")
        });

    // ------------------------------------------------------------------
    // POST /api/sales/{id}/items
    // ------------------------------------------------------------------

    [Fact]
    public async Task AddItem_WithoutIdempotencyKey_Returns400AndDoesNotCallService()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var salesService = new Mock<ISalesService>();
        var controller = CreateSalesController(context, salesService);
        AttachHttpContext(controller, ItemsPath, idempotencyKey: null);

        var result = await controller.AddItemAsync(1, CreateItemRequest());

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("Idempotency-Key", GetProblemMessage(badRequest.Value));
        salesService.Verify(s => s.AddItemAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>(),
            It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AddItem_WithIdempotencyKey_FirstMissThenReplayHit_DoesNotAddItemTwice()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var salesService = new Mock<ISalesService>();
        salesService.Setup(s => s.AddItemAsync(1, 10, 2m, 50m, null, null, true, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaleDto { Id = 1, Status = "Pending", TotalUSD = 100m });
        var controller = CreateSalesController(context, salesService);

        AttachHttpContext(controller, ItemsPath, "ITEMS-REPLAY-001");
        var first = await controller.AddItemAsync(1, CreateItemRequest());
        Assert.IsType<OkObjectResult>(first.Result);
        Assert.Equal("MISS", GetCacheLookup(controller));
        Assert.Equal(1, await context.IdempotentRequests.CountAsync());

        // Retry tras microcorte: mismo key y payload → replay sin segunda mutación.
        AttachHttpContext(controller, ItemsPath, "ITEMS-REPLAY-001");
        var replay = await controller.AddItemAsync(1, CreateItemRequest());

        var content = Assert.IsType<ContentResult>(replay.Result);
        Assert.Equal("HIT", GetCacheLookup(controller));
        var replayedSale = JsonSerializer.Deserialize<SaleDto>(content.Content!);
        Assert.NotNull(replayedSale);
        Assert.Equal(1, replayedSale!.Id);

        salesService.Verify(s => s.AddItemAsync(1, 10, 2m, 50m, null, null, true, 1, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(1, await context.IdempotentRequests.CountAsync());
    }

    [Fact]
    public async Task AddItem_WithReusedKeyAndDifferentPayload_Returns422()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var salesService = new Mock<ISalesService>();
        salesService.Setup(s => s.AddItemAsync(1, 10, 2m, 50m, null, null, true, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaleDto { Id = 1, Status = "Pending" });
        var controller = CreateSalesController(context, salesService);

        AttachHttpContext(controller, ItemsPath, "ITEMS-MISMATCH-001");
        await controller.AddItemAsync(1, CreateItemRequest(quantity: 2m));

        AttachHttpContext(controller, ItemsPath, "ITEMS-MISMATCH-001");
        var mismatch = await controller.AddItemAsync(1, CreateItemRequest(quantity: 3m));

        var objectResult = Assert.IsType<ObjectResult>(mismatch.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, objectResult.StatusCode);
        salesService.Verify(s => s.AddItemAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>(),
            It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ------------------------------------------------------------------
    // POST /api/cashdrawer/transaction
    // ------------------------------------------------------------------

    [Fact]
    public async Task AddTransaction_WithoutIdempotencyKey_Returns400AndDoesNotWriteLedger()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();
        var controller = CreateCashDrawerController(
            salesDb, inventoryDb, new CashDrawerService(salesDb), CreateSettingsMock().Object, new IdempotencyService(salesDb));
        AttachHttpContext(controller, TransactionPath, idempotencyKey: null);

        var result = await controller.AddTransactionAsync(CreateTransactionRequest(), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("Idempotency-Key", GetProblemMessage(badRequest.Value));
        Assert.Equal(0, await salesDb.CashTransactions.CountAsync());
    }

    [Fact]
    public async Task AddTransaction_WithIdempotencyKey_FirstMissThenReplayHit_WritesSingleLedgerRow()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();
        var controller = CreateCashDrawerController(
            salesDb, inventoryDb, new CashDrawerService(salesDb), CreateSettingsMock().Object, new IdempotencyService(salesDb));

        AttachHttpContext(controller, TransactionPath, "TXN-REPLAY-001");
        var first = await controller.AddTransactionAsync(CreateTransactionRequest(), CancellationToken.None);
        Assert.IsType<OkObjectResult>(first.Result);
        Assert.Equal("MISS", GetCacheLookup(controller));

        AttachHttpContext(controller, TransactionPath, "TXN-REPLAY-001");
        var replay = await controller.AddTransactionAsync(CreateTransactionRequest(), CancellationToken.None);

        var content = Assert.IsType<ContentResult>(replay.Result);
        Assert.Equal("HIT", GetCacheLookup(controller));
        var replayedTransaction = JsonSerializer.Deserialize<CashTransactionResponseDto>(content.Content!);
        Assert.NotNull(replayedTransaction);
        Assert.Equal(600m, replayedTransaction!.AmountLocal);

        // El libro de caja tiene exactamente una fila y una sola clave registrada.
        Assert.Equal(1, await salesDb.CashTransactions.CountAsync());
        Assert.Equal(1, await salesDb.IdempotentRequests.CountAsync());
    }

    [Fact]
    public async Task AddTransaction_WithReusedKeyAndDifferentPayload_Returns422()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();
        var controller = CreateCashDrawerController(
            salesDb, inventoryDb, new CashDrawerService(salesDb), CreateSettingsMock().Object, new IdempotencyService(salesDb));

        AttachHttpContext(controller, TransactionPath, "TXN-MISMATCH-001");
        await controller.AddTransactionAsync(CreateTransactionRequest(), CancellationToken.None);

        var differentPayload = CreateTransactionRequest();
        differentPayload.AmountLocal = 999m;

        AttachHttpContext(controller, TransactionPath, "TXN-MISMATCH-001");
        var mismatch = await controller.AddTransactionAsync(differentPayload, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(mismatch.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, objectResult.StatusCode);
        Assert.Equal(1, await salesDb.CashTransactions.CountAsync());
    }

    // ------------------------------------------------------------------
    // POST /api/dailyclosure
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateClosure_WithoutIdempotencyKey_Returns400AndDoesNotCallService()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var closureService = new Mock<IDailyClosureService>();
        var controller = CreateDailyClosureController(closureService.Object, new IdempotencyService(context));
        AttachHttpContext(controller, ClosurePath, idempotencyKey: null);

        var result = await controller.CreateClosureAsync(CreateClosureRequest(), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Idempotency-Key", GetProblemMessage(badRequest.Value));
        closureService.Verify(s => s.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateClosure_WithIdempotencyKey_FirstMissThenReplayHit_DoesNotCreateSecondClosure()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var closureService = new Mock<IDailyClosureService>();
        closureService.Setup(s => s.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCloseShiftResult());
        var controller = CreateDailyClosureController(closureService.Object, new IdempotencyService(context));

        AttachHttpContext(controller, ClosurePath, "CLOSURE-REPLAY-001");
        var first = await controller.CreateClosureAsync(CreateClosureRequest(), CancellationToken.None);
        Assert.IsType<OkObjectResult>(first);
        Assert.Equal("MISS", GetCacheLookup(controller));
        Assert.Equal(1, await context.IdempotentRequests.CountAsync());

        AttachHttpContext(controller, ClosurePath, "CLOSURE-REPLAY-001");
        var replay = await controller.CreateClosureAsync(CreateClosureRequest(), CancellationToken.None);

        var content = Assert.IsType<ContentResult>(replay);
        Assert.Equal("HIT", GetCacheLookup(controller));
        var replayed = JsonSerializer.Deserialize<CloseShiftResult>(content.Content!);
        Assert.NotNull(replayed);
        Assert.Equal(42, replayed!.ClosureId);

        closureService.Verify(s => s.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(1, await context.IdempotentRequests.CountAsync());
    }

    [Fact]
    public async Task CreateClosure_WithReusedKeyAndDifferentPayload_Returns422()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var closureService = new Mock<IDailyClosureService>();
        closureService.Setup(s => s.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCloseShiftResult());
        var controller = CreateDailyClosureController(closureService.Object, new IdempotencyService(context));

        AttachHttpContext(controller, ClosurePath, "CLOSURE-MISMATCH-001");
        await controller.CreateClosureAsync(CreateClosureRequest(actualAmount: 1000m), CancellationToken.None);

        AttachHttpContext(controller, ClosurePath, "CLOSURE-MISMATCH-001");
        var mismatch = await controller.CreateClosureAsync(CreateClosureRequest(actualAmount: 555m), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(mismatch);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, objectResult.StatusCode);
        closureService.Verify(s => s.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateClosure_WhenDuplicateDateGuardFires_Returns409WithExactMessage()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var closureService = new Mock<IDailyClosureService>();
        closureService.Setup(s => s.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(DailyClosureService.DuplicateClosureDateMessage));
        var controller = CreateDailyClosureController(closureService.Object, new IdempotencyService(context));

        AttachHttpContext(controller, ClosurePath, "CLOSURE-DUP-001");
        var result = await controller.CreateClosureAsync(CreateClosureRequest(), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Equal(DailyClosureService.DuplicateClosureDateMessage, GetProblemMessage(conflict.Value));
        Assert.Equal("Ya existe un cierre para esta fecha.", GetProblemMessage(conflict.Value));
    }

    [Fact]
    public async Task CreateClosureFromCommand_DuplicateDate_ThrowsExactMessageAndKeepsSingleClosure()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var closureDate = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        context.DailyClosures.Add(new DailyClosure
        {
            Id = 1,
            ClosureDate = closureDate,
            UserId = "1",
            Observation = string.Empty,
            ExchangeRate = 50m,
            Details = new List<ClosureDetail>()
        });
        await context.SaveChangesAsync();

        var rateProvider = new Mock<ITodayExchangeRateProvider>();
        rateProvider.Setup(r => r.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(50m);
        var service = new DailyClosureService(context, rateProvider.Object, new Mock<ICashDrawerService>().Object);

        var command = new CreateClosureCommand(
            closureDate,
            "1",
            null,
            new List<DeclaredPaymentAmount> { new(1, 1000m) });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateClosureFromCommandAsync(command, CancellationToken.None));

        Assert.Equal(DailyClosureService.DuplicateClosureDateMessage, exception.Message);
        Assert.Equal("Ya existe un cierre para esta fecha.", exception.Message);
        Assert.Equal(1, await context.DailyClosures.CountAsync());
    }

    [Fact]
    public void DailyClosure_ClosureDateIndex_IsUnique()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var entityType = context.Model.FindEntityType(typeof(DailyClosure));
        Assert.NotNull(entityType);

        var closureDateIndex = entityType!.GetIndexes()
            .Single(index => index.Properties.Any(property => property.Name == "ClosureDate"));
        Assert.True(closureDateIndex.IsUnique, "El índice de ClosureDate debe ser único (8.149 SRE-02).");
    }

    // ------------------------------------------------------------------
    // POST /api/sales/{id}/cancel — idempotencia
    // ------------------------------------------------------------------

    [Fact]
    public async Task CancelSaleAsync_WhenAlreadyCancelled_ReturnsWithoutThrowing()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Sales.Add(new Sale
        {
            Id = 1,
            TotalUSD = 50m,
            Status = SaleStatus.Cancelled,
            DeliveryStatus = SaleDeliveryStatus.PendingPickup
        });
        await context.SaveChangesAsync();

        var service = new SalesService(
            context,
            Mock.Of<IInventoryService>(),
            Mock.Of<IMediator>(),
            Mock.Of<ICashDrawerService>(),
            Mock.Of<ISystemSettingsService>());

        var exception = await Record.ExceptionAsync(() => service.CancelSaleAsync(1));

        Assert.Null(exception);
        var sale = await context.Sales.FindAsync(1);
        Assert.NotNull(sale);
        Assert.Equal(SaleStatus.Cancelled, sale!.Status);
    }

    [Fact]
    public async Task CancelSaleAsync_WithPayments_StillThrows()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var sale = new Sale
        {
            Id = 1,
            TotalUSD = 50m,
            Status = SaleStatus.OnHold,
            DeliveryStatus = SaleDeliveryStatus.PendingPickup,
            ClaimedByUserId = 7,
            ClaimedByUserName = "Test Actor",
            ClaimAction = SaleClaimAction.Editing,
            ClaimedAtUtc = DateTime.UtcNow
        };
        sale.Payments.Add(new SalePayment { Id = 1, SaleId = 1, PaymentMethodId = 1, Amount = 10m, AmountBsS = 500m });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(
            context,
            Mock.Of<IInventoryService>(),
            Mock.Of<IMediator>(),
            Mock.Of<ICashDrawerService>(),
            Mock.Of<ISystemSettingsService>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelSaleAsync(1, actingUserId: 7));

        Assert.Contains("abonos acumulados", exception.Message);
    }
}
