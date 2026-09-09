using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Services;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Moq;
using Sales.Module.Data;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class Phase1SecurityRemediationTests
{
    private SalesDbContext CreateInMemorySalesDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new SalesDbContext(options);
    }

    private IConfiguration CreateMockConfiguration()
    {
        var inMemorySettings = new System.Collections.Generic.Dictionary<string, string?>
        {
            {"JWT_SETTINGS_KEY", "POS_Test_Super_Secret_Key_At_Least_32_Chars_Long!"},
            {"JwtSettings:Issuer", "SolucionesPos"},
            {"JwtSettings:Audience", "PosClient"},
            {"JwtSettings:ExpiryMinutes", "120"}
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();
    }

    [Fact]
    public async Task CompleteSale_WithoutIdempotencyKey_Returns400BadRequest()
    {
        // Arrange
        var mockSalesService = new Mock<ISalesService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var mockIdempotency = new Mock<IIdempotencyService>();
        var controller = new SalesController(mockSalesService.Object, mockUser.Object, mockIdempotency.Object);

        var httpContext = new DefaultHttpContext();
        // Do NOT set Idempotency-Key header
        httpContext.Request.Path = "/api/sales/1/complete";
        httpContext.Request.Method = "POST";
        httpContext.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var requestDto = new CompleteSaleRequest
        {
            ExchangeRate = 50.0m,
            Payments = new System.Collections.Generic.List<SalePaymentDto>
            {
                new() { PaymentMethodId = 1, Amount = 10, AmountBsS = 500 }
            }
        };

        // Act
        var result = await controller.CompleteSale(1, requestDto);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequestResult.Value);
        
        var messageProp = badRequestResult.Value.GetType().GetProperty("message", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
        Assert.NotNull(messageProp);
        var message = messageProp.GetValue(badRequestResult.Value)?.ToString();
        Assert.Contains("Idempotency-Key", message);

        // Verify sales service was never invoked
        mockSalesService.Verify(s => s.CompleteSaleAsync(
            It.IsAny<int>(),
            It.IsAny<decimal>(),
            It.IsAny<System.Collections.Generic.IEnumerable<PaymentInfo>>(),
            It.IsAny<decimal>(),
            It.IsAny<int?>(),
            It.IsAny<bool>(),
            It.IsAny<string?>(),
            It.IsAny<byte[]?>(),
            It.IsAny<System.Threading.CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HealthCheck_DoesNotExposeMachineNameOrDbExceptions()
    {
        // Arrange
        using var db = CreateInMemorySalesDbContext();
        var inventoryDb = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>()).Build();
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(AppContext.BaseDirectory);
        var controller = new HealthController(db, inventoryDb, config, env.Object, new Backend.API.Metrics.RequestMetricsRegistry());

        // Act
        var result = await controller.CheckHealth();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);

        // Inspect properties of anonymous object via reflection
        var props = okResult.Value.GetType().GetProperties();
        var propNames = props.Select(p => p.Name).ToList();

        // MachineName must not exist in response (SEC-08)
        Assert.DoesNotContain("machineName", propNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host", propNames, StringComparer.OrdinalIgnoreCase);

        // Required safe properties exist
        Assert.Contains("status", propNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("database", propNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_WithNonExistentUser_Returns401Unauthorized_SameAsWrongPassword()
    {
        // Arrange
        using var db = CreateInMemorySalesDbContext();
        var config = CreateMockConfiguration();
        var tokenService = new TokenService(config);

        var controller = new AuthController(db, tokenService);

        // Act: Non-existent user
        var responseNonExistent = await controller.Login(new LoginRequest
        {
            Cedula = "V-00000000",
            Password = "SomePassword123!"
        });

        // Assert
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(responseNonExistent.Result);
        Assert.NotNull(unauthorizedResult.Value);

        var messageProp = unauthorizedResult.Value.GetType().GetProperty("Message");
        Assert.NotNull(messageProp);
        var messageValue = messageProp.GetValue(unauthorizedResult.Value)?.ToString();
        Assert.Equal("Credenciales inválidas.", messageValue);
    }
}
