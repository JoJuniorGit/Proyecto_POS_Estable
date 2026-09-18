using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using CommandCenter.Tests.TestHelpers;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class CloseShiftResolverClassificationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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

    private static Mock<ICashDrawerService> CreateMockCashDrawer()
    {
        var mock = new Mock<ICashDrawerService>();
        mock.Setup(c => c.GetActiveSessionAsync())
            .ReturnsAsync(new CashDrawerSessionResponseDto
            {
                Id = 1,
                Status = CashDrawerStatus.Open,
                OpeningExchangeRate = 50m,
                OpenedAt = DateTime.UtcNow
            });
        return mock;
    }

    private static ShiftsController CreateController(
        Mock<IDailyClosureService>? mockClosure = null,
        Mock<ICashDrawerService>? mockCashDrawer = null)
    {
        mockClosure ??= new Mock<IDailyClosureService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var controller = new ShiftsController(
            mockClosure.Object,
            mockUser.Object);

        AttachUser(controller, CreateUser("1", "Admin"));
        return controller;
    }

    [Fact]
    public async Task CloseShift_DeclaresBothCurrencies_ClassifiesViaResolverAndIgnoresRequestCurrency()
    {
        var mockClosure = new Mock<IDailyClosureService>();
        mockClosure
            .Setup(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateClosureCommand cmd, CancellationToken ct) =>
            {
                var details = new List<ShiftReportDetailResult>
                {
                    new(1, "Efectivo USD", PaymentMethodCurrencyResolver.Usd, 100m, 100m, 0m, "Balanced"),
                    new(2, "Efectivo Bs.S", PaymentMethodCurrencyResolver.LocalCurrency, 500m, 500m, 0m, "Balanced")
                };
                return new CloseShiftResult(1, "Admin", "V-00000000", DateTime.UtcNow, 50m, details);
            });

        var controller = CreateController(mockClosure: mockClosure);

        var request = new CloseShiftRequest
        {
            DeclaredAmounts = new List<DeclaredAmountDto>
            {
                new() { PaymentMethodId = 1, Amount = 100m },
                new() { PaymentMethodId = 2, Amount = 500m }
            }
        };

        var result = await controller.CloseShiftAsync(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var report = Assert.IsType<ShiftReportDto>(okResult.Value);
        Assert.Equal(PaymentMethodCurrencyResolver.Usd, report.Details.First(d => d.PaymentMethodId == 1).Currency);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, report.Details.First(d => d.PaymentMethodId == 2).Currency);

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
        var mockClosure = new Mock<IDailyClosureService>();
        mockClosure
            .Setup(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateClosureCommand cmd, CancellationToken ct) =>
            {
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
                new() { PaymentMethodId = 1, Amount = 100m }
            }
        };

        var result = await controller.CloseShiftAsync(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var report = Assert.IsType<ShiftReportDto>(okResult.Value);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, report.Details[0].Currency);
    }

    [Fact]
    public async Task CloseShift_UnknownPaymentMethodId_ReturnsProblemDetails()
    {
        var mockClosure = new Mock<IDailyClosureService>();
        mockClosure
            .Setup(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("El desglose contiene métodos de pago no reconocidos: 999."));

        var controller = CreateController(mockClosure: mockClosure);

        var request = new CloseShiftRequest
        {
            DeclaredAmounts = new List<DeclaredAmountDto>
            {
                new() { PaymentMethodId = 999, Amount = 50m }
            }
        };

        var result = await controller.CloseShiftAsync(request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(400, problemDetails.Status);
        Assert.Contains("999", problemDetails.Detail);
        mockClosure.Verify(
            c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

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

        var result = await controller.CloseShiftAsync(request, CancellationToken.None);

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
                new() { PaymentMethodId = 1, Amount = 100m },
                new() { PaymentMethodId = 1, Amount = 200m }
            }
        };

        var result = await controller.CloseShiftAsync(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        mockClosure.Verify(
            c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void ShiftReportMapper_ProducesConsistentLabels_WithResolverClassification()
    {
        var details = new List<ClosureDetailResponseDto>
        {
            new(0, 0, 1, "Efectivo USD", 5000m, 5000m, 0m),
            new(0, 0, 2, "Efectivo Bs.S", 2000m, 2000m, 0m)
        };

        decimal exchangeRate = 50m;

        var reportDetails = ShiftReportMapper.MapDetails(details, exchangeRate);

        Assert.Equal(2, reportDetails.Count);

        var usdDetail = reportDetails.First(d => d.PaymentMethodId == 1);
        Assert.Equal(PaymentMethodCurrencyResolver.Usd, usdDetail.Currency);
        Assert.Equal(100m, usdDetail.SystemAmount);
        Assert.Equal(100m, usdDetail.DeclaredAmount);
        Assert.Equal(0m, usdDetail.Difference);
        Assert.Equal("Balanced", usdDetail.Status);

        var bsSDetail = reportDetails.First(d => d.PaymentMethodId == 2);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, bsSDetail.Currency);
        Assert.Equal(2000m, bsSDetail.SystemAmount);
        Assert.Equal(2000m, bsSDetail.DeclaredAmount);
        Assert.Equal(0m, bsSDetail.Difference);
        Assert.Equal("Balanced", bsSDetail.Status);
    }

    [Fact]
    public void ReceiptContent_UsesSameResolverClassification_AsShiftReportMapper()
    {
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

        var closureDto = ShiftReportMapper.MapClosure(closure);
        string receipt = DailyClosureService.GenerateReceiptContent(closureDto, isBlind: false);

        Assert.Contains("USD", receipt);
        Assert.Contains("Bs.S", receipt);

        var reportDetails = ShiftReportMapper.MapDetails(closureDto.Details, 50m);
        Assert.Equal(PaymentMethodCurrencyResolver.Usd, reportDetails.First(d => d.PaymentMethodId == 1).Currency);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, reportDetails.First(d => d.PaymentMethodId == 2).Currency);
    }

    [Fact]
    public async Task CreateClosureFromCommandAsync_RealService_UsdMethodClassifiedAsUsd()
    {
        var salesCtx = CreateInMemorySalesContext();
        salesCtx.PaymentMethods.Add(new PaymentMethod
        {
            Id = 1,
            Name = "Efectivo USD",
            IsActive = true,
            IsDeleted = false,
            IsCash = true,
            DisplayOrder = 1
        });
        await salesCtx.SaveChangesAsync();

        var service = DailyClosureTestHelper.CreateService(salesCtx);

        var command = new CreateClosureCommand(
            ClosureDateUtc: DateTime.UtcNow,
            UserId: "Admin",
            Observation: "V-00000000",
            Declarations: new List<DeclaredPaymentAmount>
            {
                new(1, 100m)
            });

        var result = await service.CreateClosureFromCommandAsync(command, CancellationToken.None);

        Assert.Equal(50m, result.ExchangeRate);
        var usdDetail = result.Details.First(d => d.PaymentMethodId == 1);
        Assert.Equal(PaymentMethodCurrencyResolver.Usd, usdDetail.Currency);
        Assert.Equal(100m, usdDetail.DeclaredAmount);

        var savedClosure = await salesCtx.DailyClosures
            .Include(c => c.Details)
            .FirstAsync(c => c.Id == result.ClosureId);
        Assert.Equal(50m, savedClosure.ExchangeRate);
        var persistedDetail = savedClosure.Details.First(d => d.PaymentMethodId == 1);
        Assert.Equal(5000m, persistedDetail.ActualAmountBsS);
    }

    [Fact]
    public async Task CreateClosureFromCommandAsync_RealService_BsSMethodClassifiedAsBsS()
    {
        var salesCtx = CreateInMemorySalesContext();
        salesCtx.PaymentMethods.Add(new PaymentMethod
        {
            Id = 2,
            Name = "Efectivo Bs.S",
            IsActive = true,
            IsDeleted = false,
            IsCash = true,
            DisplayOrder = 1
        });
        await salesCtx.SaveChangesAsync();

        var service = DailyClosureTestHelper.CreateService(salesCtx);

        var command = new CreateClosureCommand(
            ClosureDateUtc: DateTime.UtcNow,
            UserId: "Admin",
            Observation: "V-00000000",
            Declarations: new List<DeclaredPaymentAmount>
            {
                new(2, 5000m)
            });

        var result = await service.CreateClosureFromCommandAsync(command, CancellationToken.None);

        var bsSDetail = result.Details.First(d => d.PaymentMethodId == 2);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, bsSDetail.Currency);
        Assert.Equal(5000m, bsSDetail.DeclaredAmount);

        var savedClosure = await salesCtx.DailyClosures
            .Include(c => c.Details)
            .FirstAsync(c => c.Id == result.ClosureId);
        var persistedDetail = savedClosure.Details.First(d => d.PaymentMethodId == 2);
        Assert.Equal(5000m, persistedDetail.ActualAmountBsS);
    }

    [Fact]
    public async Task CreateClosureFromCommandAsync_RealService_DivergingName_UsesResolverClassification()
    {
        var salesCtx = CreateInMemorySalesContext();
        salesCtx.PaymentMethods.Add(new PaymentMethod
        {
            Id = 3,
            Name = "Dólares",
            IsActive = true,
            IsDeleted = false,
            IsCash = true,
            DisplayOrder = 1
        });
        await salesCtx.SaveChangesAsync();

        var service = DailyClosureTestHelper.CreateService(salesCtx);

        var command = new CreateClosureCommand(
            ClosureDateUtc: DateTime.UtcNow,
            UserId: "Admin",
            Observation: "V-00000000",
            Declarations: new List<DeclaredPaymentAmount>
            {
                new(3, 100m)
            });

        var result = await service.CreateClosureFromCommandAsync(command, CancellationToken.None);

        var detail = result.Details.First(d => d.PaymentMethodId == 3);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, detail.Currency);
        Assert.Equal("Dólares", detail.PaymentMethodName);

        var savedClosure = await salesCtx.DailyClosures
            .Include(c => c.Details)
            .FirstAsync(c => c.Id == result.ClosureId);
        var persistedDetail = savedClosure.Details.First(d => d.PaymentMethodId == 3);
        Assert.Equal(100m, persistedDetail.ActualAmountBsS);
    }

    [Fact]
    public async Task CreateClosureFromCommandAsync_RealService_UnknownMethodId_ThrowsArgumentException()
    {
        var salesCtx = CreateInMemorySalesContext();
        salesCtx.PaymentMethods.Add(new PaymentMethod
        {
            Id = 1,
            Name = "Efectivo USD",
            IsActive = true,
            IsDeleted = false,
            DisplayOrder = 1
        });
        await salesCtx.SaveChangesAsync();

        var service = DailyClosureTestHelper.CreateService(salesCtx);

        var command = new CreateClosureCommand(
            ClosureDateUtc: DateTime.UtcNow,
            UserId: "Admin",
            Observation: "V-00000000",
            Declarations: new List<DeclaredPaymentAmount>
            {
                new(999, 100m)
            });

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateClosureFromCommandAsync(command, CancellationToken.None));
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public async Task CreateClosureFromCommandAsync_RealService_ReqPmc04_ClientUsdForLocalMethod_UsesResolverClassification()
    {
        var salesCtx = CreateInMemorySalesContext();
        salesCtx.PaymentMethods.Add(new PaymentMethod
        {
            Id = 2,
            Name = "Efectivo Bs.S",
            IsActive = true,
            IsDeleted = false,
            IsCash = true,
            DisplayOrder = 1
        });
        await salesCtx.SaveChangesAsync();

        var service = DailyClosureTestHelper.CreateService(salesCtx);

        var command = new CreateClosureCommand(
            ClosureDateUtc: DateTime.UtcNow,
            UserId: "Admin",
            Observation: "V-00000000",
            Declarations: new List<DeclaredPaymentAmount>
            {
                new(2, 5000m)
            });

        var result = await service.CreateClosureFromCommandAsync(command, CancellationToken.None);

        var detail = result.Details.First(d => d.PaymentMethodId == 2);
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, detail.Currency);
        Assert.Equal(5000m, detail.DeclaredAmount);

        var savedClosure = await salesCtx.DailyClosures
            .Include(c => c.Details)
            .FirstAsync(c => c.Id == result.ClosureId);
        var persistedDetail = savedClosure.Details.First(d => d.PaymentMethodId == 2);
        Assert.Equal(5000m, persistedDetail.ActualAmountBsS);
        Assert.Equal(50m, savedClosure.ExchangeRate);
    }

    [Fact]
    public async Task CreateClosureFromCommandAsync_RealService_WhenEffectiveRateIsNotPositive_ThrowsBeforePersisting()
    {
        var salesCtx = CreateInMemorySalesContext();
        salesCtx.PaymentMethods.Add(new PaymentMethod
        {
            Id = 1,
            Name = "Efectivo USD",
            IsActive = true,
            IsDeleted = false,
            IsCash = true,
            DisplayOrder = 1
        });
        await salesCtx.SaveChangesAsync();

        var rateProvider = new Mock<ITodayExchangeRateProvider>();
        rateProvider.Setup(r => r.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        var cashDrawer = new Mock<ICashDrawerService>();
        var service = DailyClosureTestHelper.CreateService(salesCtx, rateProvider, cashDrawer);

        var command = new CreateClosureCommand(
            ClosureDateUtc: DateTime.UtcNow,
            UserId: "Admin",
            Observation: "V-00000000",
            Declarations: new List<DeclaredPaymentAmount> { new(1, 100m) });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateClosureFromCommandAsync(command, CancellationToken.None));

        Assert.Contains("tasa BCV", ex.Message);
        Assert.Empty(await salesCtx.DailyClosures.AsNoTracking().ToListAsync());
        cashDrawer.Verify(c => c.RolloverSessionAfterClosureAsync(It.IsAny<decimal>()), Times.Never);
    }
}
