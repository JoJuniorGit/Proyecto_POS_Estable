using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.154 (SEC-03): la identidad del adelanto sale solo del token. Seam de captura:
// ISalesService.CreateCashAdvanceSaleAsync (cashierId/userName que recibe el coordinador)
// y las descripciones que recibe ICashDrawerService.AddTransactionAsync (persistidas en CashTransactions).
public class CashDrawerCashAdvanceSecurityTests
{
    private const string LegacyIdentityJson =
        "{\"SessionId\":3,\"RequestedAmountLocal\":100,\"PaymentMethodId\":1,\"PaymentMethodName\":\"Efectivo Bs.S\",\"IsTransfer\":false,\"ExchangeRate\":50,\"cashierId\":99,\"userName\":\"Administrador General\"}";

    private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

    private sealed class AdvanceIdentityCapture
    {
        public int? CashierId { get; set; }
        public string? UserName { get; set; }
        public string? ExpenseDescription { get; set; }
        public string? CommissionDescription { get; set; }
    }

    private sealed record Harness(CashDrawerController Controller, AdvanceIdentityCapture Capture, Mock<IUserService> UserService);

    private static Harness CreateHarness(SalesDbContext salesDb, string? userId)
    {
        var capture = new AdvanceIdentityCapture();
        var userService = new Mock<IUserService>();

        var settings = new Mock<ISystemSettingsService>();
        // Moq: el setup registrado al final gana; el específico de comisión va después del amplio.
        settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        settings.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct)).ReturnsAsync("5");

        var drawer = new Mock<ICashDrawerService>();
        drawer.Setup(d => d.GetCurrentBalanceLocalAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(1000m);
        drawer.Setup(d => d.AddTransactionAsync(
                It.IsAny<int>(), It.IsAny<CashTransactionType>(), It.IsAny<CashTransactionSource>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int sessionId, CashTransactionType type, CashTransactionSource source, decimal amountLocal, decimal amountUsd, decimal exchangeRate, string description, int? referenceId, bool isPhysical, int? paymentMethodId, CancellationToken _) =>
            {
                if (type == CashTransactionType.Expense)
                {
                    capture.ExpenseDescription = description;
                }
                else
                {
                    capture.CommissionDescription = description;
                }

                return new CashTransactionResponseDto
                {
                    Id = 1,
                    SessionId = sessionId,
                    Type = type,
                    Source = source,
                    AmountLocal = amountLocal,
                    AmountUsd = amountUsd,
                    ExchangeRate = exchangeRate,
                    Description = description,
                    TransactionTime = DateTime.UtcNow
                };
            });

        var sales = new Mock<ISalesService>();
        sales.Setup(s => s.CreateCashAdvanceSaleAsync(
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<decimal>(), It.IsAny<int?>(), It.IsAny<string>(),
                It.IsAny<IDbContextTransaction?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal requestedAmountLocal, decimal commissionAmountLocal, int paymentMethodId, string paymentMethodName, bool isTransfer, decimal exchangeRate, int? cashierId, string? userName, IDbContextTransaction? existingTransaction, CancellationToken _) =>
            {
                capture.CashierId = cashierId;
                capture.UserName = userName;
                return (SaleDto)null!;
            });

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(u => u.UserId).Returns(userId);

        var inventory = new Mock<IInventoryService>();
        inventory.Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0m);

        var coordinator = new CashAdvanceCoordinator(salesDb, sales.Object, drawer.Object, settings.Object);
        var controller = new CashDrawerController(
            drawer.Object,
            settings.Object,
            inventory.Object,
            userService.Object,
            currentUser.Object,
            new TimeZoneProvider(settings.Object),
            coordinator);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        return new Harness(controller, capture, userService);
    }

    private static CashAdvanceRequest CreateRequest() => new()
    {
        SessionId = 3,
        RequestedAmountLocal = 100m,
        PaymentMethodId = 1,
        PaymentMethodName = "Efectivo Bs.S",
        IsTransfer = false,
        ExchangeRate = 50m
    };

    [Fact]
    public async Task ProcessCashAdvance_IdentityComesFromToken_NotFromLegacyUserNameLookup()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        var harness = CreateHarness(salesDb, "7");
        harness.UserService.Setup(s => s.GetUserAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDto { Id = 7, Name = "Cajero A" });
        harness.UserService.Setup(s => s.GetUserNameByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Nombre legado");

        var result = await harness.Controller.ProcessCashAdvanceAsync(CreateRequest(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(7, harness.Capture.CashierId);
        Assert.Equal("Cajero A", harness.Capture.UserName);
        Assert.NotNull(harness.Capture.ExpenseDescription);
        Assert.Contains("Cajero A", harness.Capture.ExpenseDescription!);
        Assert.NotNull(harness.Capture.CommissionDescription);
        Assert.Contains("Cajero A", harness.Capture.CommissionDescription!);
        harness.UserService.Verify(s => s.GetUserAsync(7, It.IsAny<CancellationToken>()), Times.Once);
        harness.UserService.Verify(s => s.GetUserNameByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessCashAdvance_LegacyBodyWithSpoofedIdentity_IgnoresBodyValues()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        var harness = CreateHarness(salesDb, "7");
        harness.UserService.Setup(s => s.GetUserAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDto { Id = 7, Name = "Cajero A" });

        var request = JsonSerializer.Deserialize<CashAdvanceRequest>(LegacyIdentityJson, CaseInsensitiveJson)!;

        var result = await harness.Controller.ProcessCashAdvanceAsync(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(7, harness.Capture.CashierId);
        Assert.Equal("Cajero A", harness.Capture.UserName);
        Assert.NotNull(harness.Capture.ExpenseDescription);
        Assert.DoesNotContain("Administrador General", harness.Capture.ExpenseDescription!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    public async Task ProcessCashAdvance_WithoutParsableSessionUserId_Returns401SesionInvalida(string? userId)
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        var harness = CreateHarness(salesDb, userId);

        var result = await harness.Controller.ProcessCashAdvanceAsync(CreateRequest(), CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal(401, unauthorized.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(unauthorized.Value);
        Assert.Equal("Sesión inválida.", Assert.IsType<string>(problem.Extensions["message"]));
    }

    [Fact]
    public async Task ProcessCashAdvance_WhenTokenUserDoesNotExist_ThrowsUsuarioNoEncontrado()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        var harness = CreateHarness(salesDb, "7");

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Controller.ProcessCashAdvanceAsync(CreateRequest(), CancellationToken.None));

        Assert.Equal("Usuario no encontrado.", ex.Message);
    }

    [Fact]
    public void CashAdvanceRequest_NoLongerExposesLegacyIdentityMembers()
    {
        Assert.Null(typeof(CashAdvanceRequest).GetProperty("CashierId"));
        Assert.Null(typeof(CashAdvanceRequest).GetProperty("UserName"));
    }

    [Fact]
    public void CashAdvanceRequest_LegacyJsonDeserializesWithoutIdentityMembers()
    {
        var request = JsonSerializer.Deserialize<CashAdvanceRequest>(LegacyIdentityJson, CaseInsensitiveJson);

        Assert.NotNull(request);
        Assert.Equal(3, request!.SessionId);
        Assert.Equal(100m, request.RequestedAmountLocal);
        Assert.Equal(50m, request.ExchangeRate);

        // Compatibilidad: STJ ignora miembros desconocidos y el DTO ya no los expone al serializar.
        var serialized = JsonSerializer.Serialize(request);
        Assert.DoesNotContain("CashierId", serialized);
        Assert.DoesNotContain("UserName", serialized);
    }
}
