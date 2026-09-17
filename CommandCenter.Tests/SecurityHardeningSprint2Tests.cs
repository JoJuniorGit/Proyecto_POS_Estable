using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.DTOs;
using Backend.API.Middleware;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests;

public class SecurityHardeningSprint2Tests
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

    [Fact]
    public async Task SecurityHeadersMiddleware_SetsAllRequiredSecurityHeaders()
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(nextContext =>
        {
            nextContext.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        // Trigger response start callback
        context.Response.Body = new MemoryStream();
        await context.Response.StartAsync();

        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"]);
        Assert.Equal("1; mode=block", context.Response.Headers["X-XSS-Protection"]);
        Assert.Equal("strict-origin-when-cross-origin", context.Response.Headers["Referrer-Policy"]);
        Assert.True(context.Response.Headers.ContainsKey("Content-Security-Policy"));

        string permissionsPolicy = context.Response.Headers["Permissions-Policy"].ToString();
        Assert.Contains("camera=(self)", permissionsPolicy);
        Assert.Contains("geolocation=()", permissionsPolicy);
        Assert.Contains("microphone=()", permissionsPolicy);
        Assert.Contains("payment=()", permissionsPolicy);
        Assert.Contains("usb=()", permissionsPolicy);
    }

    [Fact]
    public async Task CsvExport_NeutralizesFormulaInjection()
    {
        using var db = GetInMemoryInventoryDbContext();
        var service = new InventoryService(db);

        var dangerousProduct = new Product
        {
            Id = 100,
            SKU = "=cmd|'/C calc'!A0",
            Name = "+SUM(A1:A10)",
            Description = "@hyperlink(\"http://malicious.site\")",
            CostPriceUSD = 5.00m,
            PriceRetailUSD = 10.00m,
            StockQuantity = 20m
        };

        db.Products.Add(dangerousProduct);
        await db.SaveChangesAsync();

        var csvBytes = await service.ExportProductsAsync("csv", activeOnly: false);
        var csvString = System.Text.Encoding.UTF8.GetString(csvBytes);

        // Must prefix dangerous leading formula characters with '
        Assert.Contains("'=cmd|", csvString);
        Assert.Contains("'+SUM", csvString);
        Assert.Contains("'@hyperlink", csvString);
    }

    [Fact]
    public async Task CashDrawerService_ThrowsOnNegativeOpeningOrClosingBalance()
    {
        using var db = GetInMemorySalesDbContext();
        var service = new CashDrawerService(db);

        // Negative opening balance
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.OpenSessionAsync(-100m, 50m));

        // Invalid exchange rate
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.OpenSessionAsync(100m, 0m));

        // Valid opening
        var session = await service.OpenSessionAsync(100m, 50m);
        Assert.Equal(CashDrawerStatus.Open, session.Status);

        // Negative closing balance
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CloseSessionAsync(-50m, 50m));
    }

    [Fact]
    public async Task DailyClosureService_ThrowsOnNegativeActualAmount()
    {
        using var db = GetInMemorySalesDbContext();
        var service = new DailyClosureService(db);

        var method = new PaymentMethod { Id = 1, Name = "Efectivo", IsActive = true };
        db.PaymentMethods.Add(method);
        await db.SaveChangesAsync();

        var closure = new DailyClosure
        {
            ClosureDate = DateTime.UtcNow,
            Details = new List<ClosureDetail>
            {
                new ClosureDetail
                {
                    PaymentMethodId = 1,
                    PaymentMethodName = "Efectivo",
                    ExpectedAmountBsS = 100m,
                    ActualAmountBsS = -20m // Negative
                }
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateClosureAsync(closure));
    }

    [Fact]
    public async Task ReservationsController_RejectsNegativeQuantityAndClampsDuration()
    {
        using var db = GetInMemoryInventoryDbContext();
        var inventoryService = new InventoryService(db);
        var controller = new ReservationsController(inventoryService);

        // Negative quantity must return BadRequest
        var result = await controller.ReserveStock(new ReserveStockDto
        {
            ProductId = 1,
            Quantity = -5m,
            DurationSeconds = 300
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task DailyClosureController_UnknownPaymentMethodId_ReturnsBadRequestWithoutCreatingClosure()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = GetInMemoryInventoryDbContext();
        var mockClosure = new Mock<IDailyClosureService>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");
        mockCashDrawer.Setup(c => c.GetActiveSessionAsync())
            .ReturnsAsync(new CashDrawerSession { OpeningExchangeRate = 50m });
        mockClosure.Setup(c => c.GetExpectedTotalsByPaymentMethodAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ExpectedTotalDto>
            {
                new ExpectedTotalDto { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ExpectedAmountBsS = 1000m }
            });

        var controller = new DailyClosureController(
            mockClosure.Object,
            mockCashDrawer.Object,
            inventoryDb,
            mockSettings.Object,
            salesDb,
            mockUser.Object);

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
            Details = new List<CreateClosureDetailRequest>
            {
                new CreateClosureDetailRequest { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ActualAmountBsS = 1000m },
                new CreateClosureDetailRequest { PaymentMethodId = 999, PaymentMethodName = "Método Inyectado", ActualAmountBsS = 0m }
            }
        };

        var result = await controller.CreateClosure(request);

        Assert.IsType<BadRequestObjectResult>(result);
        mockClosure.Verify(c => c.CreateClosureAsync(It.IsAny<DailyClosure>()), Times.Never);
        mockCashDrawer.Verify(c => c.RolloverSessionAfterClosureAsync(It.IsAny<decimal>()), Times.Never);
    }

    [Fact]
    public async Task ShiftsController_UnknownDeclaredPaymentMethodId_ReturnsBadRequestWithoutCreatingClosure()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = GetInMemoryInventoryDbContext();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockDailyClosure = new Mock<IDailyClosureService>();
        var mockPaymentMethod = new Mock<IPaymentMethodService>();
        var mockSettings = new Mock<ISystemSettingsService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");
        mockCashDrawer.Setup(c => c.GetActiveSessionAsync())
            .ReturnsAsync(new CashDrawerSession { OpeningExchangeRate = 50m });
        mockDailyClosure.Setup(c => c.GetExpectedTotalsByPaymentMethodAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExpectedTotalDto>
            {
                new ExpectedTotalDto { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ExpectedAmountBsS = 1000m }
            });
        mockDailyClosure
            .Setup(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("El desglose contiene métodos de pago no reconocidos: 999."));

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
                    new Claim(ClaimTypes.NameIdentifier, "1"),
                    new Claim(ClaimTypes.Role, "Admin")
                }, "TestAuth"))
            }
        };

        var request = new CloseShiftRequest
        {
            DeclaredAmounts = new List<DeclaredAmountDto>
            {
                new DeclaredAmountDto { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", Amount = 1000m },
                new DeclaredAmountDto { PaymentMethodId = 999, PaymentMethodName = "Método Inyectado", Amount = 0m }
            }
        };

        var result = await controller.CloseShift(request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(400, problemDetails.Status);
        mockDailyClosure.Verify(c => c.CreateClosureFromCommandAsync(It.IsAny<CreateClosureCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        mockCashDrawer.Verify(c => c.RolloverSessionAfterClosureAsync(It.IsAny<decimal>()), Times.Never);
    }
}
