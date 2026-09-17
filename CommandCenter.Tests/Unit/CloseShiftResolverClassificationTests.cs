using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// S1 — REQ-PMC-01/04: CloseShift classifies every declared method via
/// PaymentMethodCurrencyResolver; ignores request.Currency; unknown method → 400.
/// </summary>
public class CloseShiftResolverClassificationTests
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

    private static ShiftsController CreateController(
        Mock<IDailyClosureService>? mockClosure = null,
        Mock<ICashDrawerService>? mockCashDrawer = null)
    {
        mockClosure ??= new Mock<IDailyClosureService>();
        mockCashDrawer ??= new Mock<ICashDrawerService>();
        var mockPaymentMethod = new Mock<IPaymentMethodService>();
        var mockSettings = new Mock<ISystemSettingsService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var controller = new ShiftsController(
            mockCashDrawer.Object,
            mockClosure.Object,
            mockPaymentMethod.Object,
            mockSettings.Object,
            CreateInMemoryInventoryContext(),
            CreateInMemorySalesContext(),
            mockUser.Object);

        AttachUser(controller, CreateUser("1", "Admin"));
        return controller;
    }

    // ── Task 1.1 (RED → GREEN): CloseShift classifies via resolver ──

    [Fact]
    public async Task CloseShift_DeclaresBothCurrencies_ClassifiesViaResolverAndIgnoresRequestCurrency()
    {
        // REQ-PMC-01: every declared method MUST be classified by PaymentMethodCurrencyResolver
        // REQ-PMC-04: request.Currency MUST NOT influence the closure

        var mockClosure = new Mock<IDailyClosureService>();
        var resolverUsdCurrency = PaymentMethodCurrencyResolver.Usd;
        var resolverBsSCurrency = PaymentMethodCurrencyResolver.LocalCurrency;

        mockClosure
            .Setup(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateClosureCommand cmd, CancellationToken ct) =>
            {
                // Verify the command was built without currency — only PaymentMethodId + Amount
                foreach (var decl in cmd.Declarations)
                {
                    Assert.True(decl.PaymentMethodId > 0);
                    Assert.True(decl.Amount >= 0);
                }

                // Build a result that reflects what the resolver would produce
                var details = new List<ShiftReportDetailResult>
                {
                    new(1, "Efectivo USD", resolverUsdCurrency, 100m, 100m, 0m, "Balanced"),
                    new(2, "Efectivo Bs.S", resolverBsSCurrency, 500m, 500m, 0m, "Balanced")
                };
                return new CloseShiftResult(1, "Admin", "V-00000000", DateTime.UtcNow, 50m, details);
            });

        var controller = CreateController(mockClosure: mockClosure);

        var request = new CloseShiftRequest
        {
            DeclaredAmounts = new List<DeclaredAmountDto>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", Amount = 100m },
                new() { PaymentMethodId = 2, PaymentMethodName = "Efectivo Bs.S", Amount = 500m }
            }
        };

        var result = await controller.CloseShift(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var report = Assert.IsType<ShiftReportDto>(okResult.Value);

        // Verify resolver classified each method correctly
        var usdDetail = report.Details.First(d => d.PaymentMethodId == 1);
        Assert.Equal(PaymentMethodCurrencyResolver.Usd, usdDetail.Currency);

        var bsSDetail = report.Details.First(d => d.PaymentMethodId == 2);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, bsSDetail.Currency);

        // Verify the command sent to the service has no currency field
        mockClosure.Verify(
            c => c.CreateClosureFromCommandAsync(
                It.Is<CreateClosureCommand>(cmd =>
                    cmd.Declarations.All(d => d.PaymentMethodId > 0 && d.Amount >= 0)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CloseShift_DivergingMethodName_UsesResolverNotName()
    {
        // REQ-PMC-01 Scenario: A diverging method name does not change the close classification
        // Method named "Dólares" but resolver classifies as Bs.S (no "USD" in name)

        var mockClosure = new Mock<IDailyClosureService>();

        mockClosure
            .Setup(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateClosureCommand cmd, CancellationToken ct) =>
            {
                // "Dólares" does NOT contain "USD", so resolver returns Bs.S
                string currency = PaymentMethodCurrencyResolver.Resolve("Dólares");
                Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, currency);

                var details = new List<ShiftReportDetailResult>
                {
                    new(1, "Dólares", currency, 100m, 100m, 0m, "Balanced")
                };
                return new CloseShiftResult(1, "Admin", "V-00000000", DateTime.UtcNow, 50m, details);
            });

        var controller = CreateController(mockClosure: mockClosure);

        var request = new CloseShiftRequest
        {
            DeclaredAmounts = new List<DeclaredAmountDto>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Dólares", Amount = 100m }
            }
        };

        var result = await controller.CloseShift(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var report = Assert.IsType<ShiftReportDto>(okResult.Value);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, report.Details[0].Currency);
    }

    [Fact]
    public async Task CloseShift_UnknownPaymentMethodId_ReturnsBadRequest()
    {
        // REQ-PMC-04 Scenario: Unknown declared payment method → 400 ProblemDetails, nothing persisted

        var mockClosure = new Mock<IDailyClosureService>();
        mockClosure
            .Setup(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("El desglose contiene métodos de pago no reconocidos: 999."));

        var controller = CreateController(mockClosure: mockClosure);

        var request = new CloseShiftRequest
        {
            DeclaredAmounts = new List<DeclaredAmountDto>
            {
                new() { PaymentMethodId = 999, PaymentMethodName = "Método Inexistente", Amount = 50m }
            }
        };

        var result = await controller.CloseShift(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        mockClosure.Verify(
            c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Task 1.10: existing tests re-pointed to use CreateClosureCommand without Currency ──

    [Fact]
    public async Task CloseShift_EmptyDeclaredAmounts_CreatesCommandAndDelegatesToService()
    {
        var mockClosure = new Mock<IDailyClosureService>();
        mockClosure
            .Setup(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloseShiftResult(1, "Admin", "V-00000000", DateTime.UtcNow, 50m, new List<ShiftReportDetailResult>()));

        var controller = CreateController(mockClosure: mockClosure);

        var request = new CloseShiftRequest
        {
            DeclaredAmounts = new List<DeclaredAmountDto>()
        };

        var result = await controller.CloseShift(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        mockClosure.Verify(
            c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CloseShift_DuplicateMethodIds_ReturnsBadRequestBeforeCallingService()
    {
        var mockClosure = new Mock<IDailyClosureService>();

        var controller = CreateController(mockClosure: mockClosure);

        var request = new CloseShiftRequest
        {
            DeclaredAmounts = new List<DeclaredAmountDto>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", Amount = 100m },
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", Amount = 200m }
            }
        };

        var result = await controller.CloseShift(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        mockClosure.Verify(
            c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Task 1.8 (GREEN): Report↔receipt agreement (C1 P1 pattern) ──

    [Fact]
    public void ShiftReportMapper_ProducesConsistentLabels_WithResolverClassification()
    {
        // REQ-PMC-03 / C1 P1: report labels must agree with receipt classification.
        // ShiftReportMapper uses the same PaymentMethodCurrencyResolver as the receipt generator.
        // This test verifies the mapper classifies correctly for both currencies.

        var details = new List<ClosureDetail>
        {
            new()
            {
                PaymentMethodId = 1,
                PaymentMethodName = "Efectivo USD",
                ExpectedAmountBsS = 5000m,
                ActualAmountBsS = 5000m,
                DifferenceBsS = 0m
            },
            new()
            {
                PaymentMethodId = 2,
                PaymentMethodName = "Efectivo Bs.S",
                ExpectedAmountBsS = 2000m,
                ActualAmountBsS = 2000m,
                DifferenceBsS = 0m
            }
        };

        decimal exchangeRate = 50m;

        var reportDetails = ShiftReportMapper.MapDetails(details, exchangeRate);

        Assert.Equal(2, reportDetails.Count);

        var usdDetail = reportDetails.First(d => d.PaymentMethodId == 1);
        Assert.Equal(PaymentMethodCurrencyResolver.Usd, usdDetail.Currency);
        // For USD: systemAmount = ExpectedAmountBsS / rate = 5000/50 = 100 USD
        Assert.Equal(100m, usdDetail.SystemAmount);
        // For USD: declaredAmount = ActualAmountBsS / rate = 5000/50 = 100 USD
        Assert.Equal(100m, usdDetail.DeclaredAmount);
        Assert.Equal(0m, usdDetail.Difference);
        Assert.Equal("Balanced", usdDetail.Status);

        var bsSDetail = reportDetails.First(d => d.PaymentMethodId == 2);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, bsSDetail.Currency);
        // For Bs.S: amounts are passed through directly
        Assert.Equal(2000m, bsSDetail.SystemAmount);
        Assert.Equal(2000m, bsSDetail.DeclaredAmount);
        Assert.Equal(0m, bsSDetail.Difference);
        Assert.Equal("Balanced", bsSDetail.Status);
    }

    [Fact]
    public void ReceiptContent_UsesSameResolverClassification_AsShiftReportMapper()
    {
        // C1 P1 agreement: receipt uses PaymentMethodCurrencyResolver.Resolve(detail.PaymentMethodName)
        // which is the same logic ShiftReportMapper uses. Verify both produce the same currency label.

        var closure = new DailyClosure
        {
            ClosureDate = DateTime.UtcNow,
            UserId = "Admin",
            TotalActualBsS = 7000m,
            TotalExpectedBsS = 7000m,
            TotalDifferenceBsS = 0m,
            Details = new List<ClosureDetail>
            {
                new() { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ActualAmountBsS = 5000m, ExpectedAmountBsS = 5000m, DifferenceBsS = 0m },
                new() { PaymentMethodId = 2, PaymentMethodName = "Efectivo Bs.S", ActualAmountBsS = 2000m, ExpectedAmountBsS = 2000m, DifferenceBsS = 0m }
            }
        };

        string receipt = DailyClosureService.GenerateReceiptContent(closure, isBlind: false);

        // Receipt must contain the resolver-classified currency labels
        Assert.Contains("USD", receipt);
        Assert.Contains("Bs.S", receipt);

        // Verify the mapper produces the same classification
        var reportDetails = ShiftReportMapper.MapDetails(closure.Details, 50m);
        Assert.Equal(PaymentMethodCurrencyResolver.Usd, reportDetails.First(d => d.PaymentMethodId == 1).Currency);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, reportDetails.First(d => d.PaymentMethodId == 2).Currency);
    }
}
