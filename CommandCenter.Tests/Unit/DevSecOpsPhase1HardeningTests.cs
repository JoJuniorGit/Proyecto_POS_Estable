using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Attributes;
using Backend.API.Controllers;
using Backend.API.Middleware;
using Backend.API.Services;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using ISalesService = Sales.Module.Interfaces.ISalesService;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Moq.Protected;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class DevSecOpsPhase1HardeningTests
{
    private static SalesController CreateController(ISalesService salesService, string role)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(service => service.UserId).Returns("42");

        var controller = new SalesController(salesService, currentUser.Object);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "42"),
            new(ClaimTypes.Role, role)
        };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "mock"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
        return controller;
    }

    [Fact]
    public async Task GetCustomersAsync_WhenRoleIsCashier_MasksCreditLimitUSDToZero()
    {
        var salesService = new Mock<ISalesService>();
        var customers = new List<CustomerDto>
        {
            new() { Id = 1, Name = "Cliente 1", CreditLimitUSD = 1500m },
            new() { Id = 2, Name = "Cliente 2", CreditLimitUSD = 500m }
        };
        salesService.Setup(s => s.GetCustomersAsync(null, 1, 20, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((customers, 2));

        var controller = CreateController(salesService.Object, "Cashier");

        var result = await controller.GetCustomersAsync(cancellationToken: CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result);
        var pagedResult = Assert.IsType<CustomerPagedResultDto>(okResult.Value);

        Assert.All(pagedResult.Items, c => Assert.Equal(0m, c.CreditLimitUSD));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Manager")]
    public async Task GetCustomersAsync_WhenRoleIsAdminOrManager_PreservesCreditLimitUSD(string role)
    {
        var salesService = new Mock<ISalesService>();
        var customers = new List<CustomerDto>
        {
            new() { Id = 1, Name = "Cliente 1", CreditLimitUSD = 1500m },
            new() { Id = 2, Name = "Cliente 2", CreditLimitUSD = 500m }
        };
        salesService.Setup(s => s.GetCustomersAsync(null, 1, 20, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((customers, 2));

        var controller = CreateController(salesService.Object, role);

        var result = await controller.GetCustomersAsync(cancellationToken: CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result);
        var pagedResult = Assert.IsType<CustomerPagedResultDto>(okResult.Value);

        Assert.Equal(1500m, pagedResult.Items[0].CreditLimitUSD);
        Assert.Equal(500m, pagedResult.Items[1].CreditLimitUSD);
    }

    [Fact]
    public async Task GetDefaultCustomerAsync_WhenRoleIsCashier_MasksCreditLimitUSDToZero()
    {
        var salesService = new Mock<ISalesService>();
        var defaultCustomer = new CustomerDto { Id = 1, Name = "Consumidor Final", CreditLimitUSD = 1000m };
        salesService.Setup(s => s.GetDefaultCustomerAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(defaultCustomer);

        var controller = CreateController(salesService.Object, "Cashier");

        var result = await controller.GetDefaultCustomerAsync(CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var customer = Assert.IsType<CustomerDto>(okResult.Value);

        Assert.Equal(0m, customer.CreditLimitUSD);
    }

    [Fact]
    public void CancelCreditSale_HasAuthorizeAttributeRestrictedToAdminAndManagerOnly()
    {
        var method = typeof(SalesController).GetMethod(nameof(SalesController.CancelCreditSaleAsync));
        Assert.NotNull(method);

        var authAttr = method!.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authAttr);
        Assert.Equal("Admin,Manager", authAttr!.Roles);
        Assert.DoesNotContain("Cashier", authAttr.Roles);
    }

    [Theory]
    [InlineData(typeof(SalesController))]
    [InlineData(typeof(ProductsController))]
    [InlineData(typeof(SettingsController))]
    [InlineData(typeof(PaymentMethodsController))]
    [InlineData(typeof(ExchangeRateController))]
    [InlineData(typeof(UsersController))]
    public void AdministrativeAndCoreControllers_HaveRequireSecurityStampValidationAttribute(Type controllerType)
    {
        var attr = controllerType.GetCustomAttribute<RequireSecurityStampValidationAttribute>();
        Assert.NotNull(attr);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task SecurityStampValidationMiddleware_WhenMutatingMethod_ForcesImmediateValidation(string httpMethod)
    {
        var validator = new Mock<ISecurityStampValidator>();
        validator.Setup(v => v.ValidateStampAsync(42, "stamp-123", true))
            .ReturnsAsync(true);

        var context = new DefaultHttpContext();
        context.Request.Method = httpMethod;
        context.Request.Path = "/api/test";

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "42"),
            new("security_stamp", "stamp-123")
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "mock"));

        bool nextCalled = false;
        var middleware = new SecurityStampValidationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, validator.Object);

        Assert.True(nextCalled);
        validator.Verify(v => v.ValidateStampAsync(42, "stamp-123", true), Times.Once);
    }

    [Fact]
    public async Task SecurityStampValidationMiddleware_WhenGetWithoutAttribute_UsesCachedWindow()
    {
        var validator = new Mock<ISecurityStampValidator>();
        validator.Setup(v => v.ValidateStampAsync(42, "stamp-123", false))
            .ReturnsAsync(true);

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/public-read";

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "42"),
            new("security_stamp", "stamp-123")
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "mock"));

        bool nextCalled = false;
        var middleware = new SecurityStampValidationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, validator.Object);

        Assert.True(nextCalled);
        validator.Verify(v => v.ValidateStampAsync(42, "stamp-123", false), Times.Once);
    }

    [Fact]
    public async Task ValidateStampAsync_WhenForceImmediateCheckIsTrue_BypassesMemoryCacheDirectly()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        await using var db = new SalesDbContext(options);
        const int testUserId = 77;
        const string initialStamp = "initial-valid-stamp";
        db.Users.Add(new User
        {
            Id = testUserId,
            Cedula = "V-77777777",
            Username = "user77",
            Name = "User 77",
            FullName = "User 77",
            Role = UserRole.Cashier,
            IsActive = true,
            SecurityStamp = initialStamp
        });
        await db.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var validator = new SecurityStampValidator(db, cache);

        // 1. Llamada normal puebla la caché
        Assert.True(await validator.ValidateStampAsync(testUserId, initialStamp, forceImmediateCheck: false));

        // 2. Modificación directa en la base de datos sin llamar a InvalidateUserStamp
        var userInDb = await db.Users.SingleAsync(u => u.Id == testUserId);
        userInDb.SecurityStamp = "new-rotated-stamp";
        await db.SaveChangesAsync();

        // 3. Con forceImmediateCheck = false, todavía devuelve true desde la caché
        Assert.True(await validator.ValidateStampAsync(testUserId, initialStamp, forceImmediateCheck: false));

        // 4. Con forceImmediateCheck = true (SEC-01), salta la caché en memoria y detecta la revocación inmediatamente
        Assert.False(await validator.ValidateStampAsync(testUserId, initialStamp, forceImmediateCheck: true));
        Assert.True(await validator.ValidateStampAsync(testUserId, "new-rotated-stamp", forceImmediateCheck: true));
    }

    [Fact]
    public async Task RegisterCreditPaymentAsync_PromotesBodyIdempotencyKeyToHeader()
    {
        var salesService = new Mock<ISalesService>();
        salesService
            .Setup(s => s.RegisterCreditPaymentAsync(It.IsAny<CreditPaymentRequest>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditPaymentResultDto { PaymentId = 101 });

        var controller = CreateController(salesService.Object, "Cashier");
        var request = new CreditPaymentRequest
        {
            CustomerId = 1,
            AmountUSD = 20m,
            PaymentMethodId = 1,
            IdempotencyKey = "body-key-test-01"
        };

        var result = await controller.RegisterCreditPaymentAsync(request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("body-key-test-01", controller.Request.Headers["Idempotency-Key"].ToString());
    }

    [Fact]
    public async Task RegisterCreditReturnAsync_PromotesBodyIdempotencyKeyToHeader()
    {
        var salesService = new Mock<ISalesService>();
        salesService
            .Setup(s => s.GetSaleAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaleDto { Id = 10, Status = "Completed", CashierId = 42 });
        salesService
            .Setup(s => s.RegisterCreditReturnAsync(It.IsAny<int>(), It.IsAny<CreditReturnRequest>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditReturnResultDto { ReturnId = 202 });

        var controller = CreateController(salesService.Object, "Cashier");
        var request = new CreditReturnRequest
        {
            IdempotencyKey = "ret-key-test-02",
            Items = new List<CreditReturnItemRequest> { new() { SaleItemId = 1, Quantity = 1m } }
        };

        var result = await controller.RegisterCreditReturnAsync(10, request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("ret-key-test-02", controller.Request.Headers["Idempotency-Key"].ToString());
    }

    [Fact]
    public async Task SupplierInvoiceService_ConfirmAsync_AttachesIdempotencyKeyHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                    "id": 1,
                    "supplierId": 2,
                    "supplierName": "Proveedor Test",
                    "invoiceNumber": "INV-001",
                    "status": "Confirmed",
                    "totalUSD": 100.0,
                    "totalBsS": 3650.0,
                    "appliedRate": 36.5,
                    "currency": "USD",
                    "lines": []
                }
                """, System.Text.Encoding.UTF8, "application/json")
            });

        var httpClient = new HttpClient(handlerMock.Object)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };

        var service = new Desktop.Client.Services.SupplierInvoiceService(httpClient);
        var request = new ConfirmSupplierInvoiceRequestDto(Array.Empty<ConfirmLineDto>());

        var result = await service.ConfirmAsync(1, request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.Headers.Contains("Idempotency-Key"));
        var key = capturedRequest.Headers.GetValues("Idempotency-Key").FirstOrDefault();
        Assert.StartsWith("inv-conf-1-", key);
    }
}
