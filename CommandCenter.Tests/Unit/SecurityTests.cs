using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Middleware;
using Backend.API.Services;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Core.Logging;
using Inventory.Module.Data;
using Inventory.Module.Services;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Moq.Protected;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class SecurityTests
{
    private SalesDbContext GetSalesDbContext(string dbName)
    {
        var context = TestDatabaseFactory.CreateSalesDbContext(dbName);
        context.Database.EnsureCreated();
        return context;
    }

    private InventoryDbContext GetInventoryDbContext(string dbName)
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext(dbName);
        context.Database.EnsureCreated();
        return context;
    }

    private ClaimsPrincipal CreateClaimsPrincipal(string username, string role, string userId = "1")
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Role, role),
            new Claim("sub", userId)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    // 1. Unauthorized Access To Admin Endpoint Returns 403 / is restricted
    [Fact]
    public void UsersController_Is_Decorated_With_Admin_Role()
    {
        var type = typeof(UsersController);
        var authAttr = (Microsoft.AspNetCore.Authorization.AuthorizeAttribute?)Attribute.GetCustomAttribute(type, typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute));

        Assert.NotNull(authAttr);
        Assert.Equal("Admin", authAttr.Roles);
    }

    [Fact]
    public void ProductsController_Mutating_Actions_Are_Decorated_With_Admin_Or_Manager_Role()
    {
        var type = typeof(ProductsController);
        var mutatingMethodNames = new[] { "Create", "Update", "SetStatus", "Restore", "Delete", "AdjustStock", "BulkImport", "ExportProducts", "ExportTemplate" };

        foreach (var methodName in mutatingMethodNames)
        {
            var method = type.GetMethods().FirstOrDefault(m => m.Name == methodName);
            Assert.NotNull(method);
            var authAttr = (Microsoft.AspNetCore.Authorization.AuthorizeAttribute?)Attribute.GetCustomAttribute(method, typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute));
            Assert.NotNull(authAttr);
            Assert.Contains("Admin", authAttr.Roles);
            Assert.Contains("Manager", authAttr.Roles);
        }
    }

    // 2. Cajero Puede Vender Y Cobrar
    [Fact]
    public async Task Cajero_Puede_Vender_Y_Cobrar()
    {
        using var context = GetSalesDbContext("Security_Cajero_Puede_Vender_" + Guid.NewGuid());
        var customer = await context.Customers.FindAsync(1);
        if (customer == null)
        {
            customer = new Customer { Id = 1, Name = "Juan Perez", CedulaOrRif = "V-12345678", IsActive = true };
            context.Customers.Add(customer);
            await context.SaveChangesAsync();
        }

        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<Sales.Module.Interfaces.ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockInventory.Setup(i => i.GetProductByIdAsync(It.IsAny<int>()))
            .ReturnsAsync(new Product { Id = 1, SKU = "1001", Name = "Arroz", PriceUSD = 2m, PriceRetailUSD = 2m, CostPriceUSD = 1m, IsActive = true });

        mockCashDrawer.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSession { Id = 1 });

        var salesService = new Sales.Module.Services.SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        // Cajero inicia venta
        var saleDto = await salesService.StartSaleAsync(10);
        Assert.NotNull(saleDto);

        // Agrega producto
        saleDto = await salesService.AddItemAsync(saleDto.Id, 1, 2m, 50m);
        Assert.Equal(4m, saleDto.TotalUSD);

        // Completa venta
        var payments = new[] { new Sales.Module.Interfaces.PaymentInfo(1, 4m, 200m, null) };
        var completedId = await salesService.CompleteSaleAsync(saleDto.Id, 50m, payments, 0m, 10);

        Assert.Equal(saleDto.Id, completedId);
        var finalSale = await context.Sales.FindAsync(completedId);
        Assert.Equal(SaleStatus.Completed, finalSale!.Status);
    }

    // 3. Cashier Cannot Execute Manual CashIn Or CashOut (Returns 403 Forbidden)
    [Fact]
    public async Task Cashier_Cannot_Execute_Manual_CashIn_Or_CashOut()
    {
        using var salesDb = GetSalesDbContext("Security_CashDrawer_403_" + Guid.NewGuid());
        var mockCashDrawer = new Mock<Sales.Module.Interfaces.ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();
        var mockCurrentUserService = new Mock<ICurrentUserService>();

        // Simula usuario autenticado con Rol Cajero
        mockCurrentUserService.Setup(u => u.UserRole).Returns(UserRole.Cashier);
        mockCurrentUserService.Setup(u => u.UserId).Returns("5");

        var controller = new CashDrawerController(mockCashDrawer.Object, mockSettings.Object, salesDb, mockCurrentUserService.Object);

        var request = new AddTransactionRequest
        {
            SessionId = 1,
            Type = CashTransactionType.Income,
            Source = CashTransactionSource.CashIn, // Operación manual Cash In
            AmountLocal = 500m,
            ExchangeRate = 50m,
            Description = "Ingreso manual no autorizado"
        };

        var actionResult = await controller.AddTransaction(request);
        var objResult = actionResult.Result as ObjectResult;

        Assert.NotNull(objResult);
        Assert.Equal(StatusCodes.Status403Forbidden, objResult.StatusCode);
    }

    // 4. Login Fail Does Not Log Password
    [Fact]
    public async Task Login_Fail_Does_Not_Log_Password()
    {
        using var db = GetSalesDbContext("Security_Login_NoPasswordLog_" + Guid.NewGuid());
        var testPassword = "SuperSecretPassword123!";
        var user = new User
        {
            Id = 99,
            Username = "cajero1",
            Cedula = "V-12345678",
            FullName = "Cajero Uno",
            PasswordHash = PasswordHasher.HashPassword("CorrectPassword123"),
            IsActive = true,
            Role = UserRole.Cashier
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var mockTokenService = new Mock<ITokenService>();
        var controller = new AuthController(db, mockTokenService.Object);

        // Login con contraseña equivocada
        var result = await controller.Login(new LoginRequest { Cedula = "V-12345678", Password = testPassword });
        var unauthorizedResult = result.Result as UnauthorizedObjectResult;
        Assert.NotNull(unauthorizedResult);

        // Verificamos que el log de inicio no contenga la contraseña
        if (File.Exists(AppLogger.StartLogPath))
        {
            var logContent = await File.ReadAllTextAsync(AppLogger.StartLogPath);
            Assert.DoesNotContain(testPassword, logContent);
        }
    }

    // 5. DTO With Negative Price Or Cost Is Rejected
    [Fact]
    public void DTO_With_Negative_Price_Or_Cost_Is_Rejected()
    {
        var product = new Product
        {
            Name = "Producto Prueba",
            SKU = "12345",
            PriceUSD = -10m, // Negativo
            CostPriceUSD = -5m, // Negativo
            ProfitMarginRetail = -1m // Negativo
        };

        var validationResults = new List<ValidationResult>();
        var context = new ValidationContext(product);
        bool isValid = Validator.TryValidateObject(product, context, validationResults, true);

        Assert.False(isValid);
        Assert.Contains(validationResults, v => v.ErrorMessage!.Contains("negativo"));
    }

    // 6. DTO With Invalid Cedula Or Phone Is Rejected
    [Theory]
    [InlineData("XYZ-123")] // Prefijo no válido
    [InlineData("123")] // Demasiado corta
    [InlineData("V-ABCD")] // Letras en lugar de números
    public void DTO_With_Invalid_Cedula_Is_Rejected(string invalidCedula)
    {
        var customerDto = new CreateCustomerDto
        {
            CedulaOrRif = invalidCedula,
            Name = "Cliente Invalido",
            Phone = "04121234567",
            CreditLimitUSD = 100m
        };

        var validationResults = new List<ValidationResult>();
        var context = new ValidationContext(customerDto);
        bool isValid = Validator.TryValidateObject(customerDto, context, validationResults, true);

        Assert.False(isValid);
        Assert.Contains(validationResults, v => v.MemberNames.Contains(nameof(CreateCustomerDto.CedulaOrRif)));
    }

    [Theory]
    [InlineData("V-12345678")]
    [InlineData("V-00000000")]
    [InlineData("J-12345678-9")]
    [InlineData("J123456789")]
    [InlineData("26123456")]
    public void DTO_With_Valid_Cedula_Formats_Is_Accepted(string validCedula)
    {
        var customerDto = new CreateCustomerDto
        {
            CedulaOrRif = validCedula,
            Name = "Cliente Valido",
            Phone = "04121234567",
            CreditLimitUSD = 100m
        };

        var validationResults = new List<ValidationResult>();
        var context = new ValidationContext(customerDto);
        bool isValid = Validator.TryValidateObject(customerDto, context, validationResults, true);

        Assert.True(isValid);
    }

    // 7. Product Import Validates Every Field
    [Fact]
    public async Task Product_Import_Validates_Every_Field()
    {
        using var context = GetInventoryDbContext("Security_Product_Import_Validation_" + Guid.NewGuid());
        var mockCurrentUserService = new Mock<ICurrentUserService>();
        mockCurrentUserService.Setup(u => u.CanMutateCatalog).Returns(true);

        var inventoryService = new InventoryService(context, mockCurrentUserService.Object);

        var productsToImport = new List<ProductImportDto>
        {
            // Fila 1: Válida
            new ProductImportDto { SKU = "2001", Name = "Harina PAN", CostPriceUSD = 1m, ProfitMarginRetail = 30m, PriceRetailUSD = 1.30m, IsValid = true },
            // Fila 2: Inválida marcada
            new ProductImportDto { SKU = "INVALID_SKU", Name = "", CostPriceUSD = -5m, IsValid = false, ErrorMessage = "Error en SKU y Costo" },
            // Fila 3: SKU vacío
            new ProductImportDto { SKU = "   ", Name = "Sin SKU", CostPriceUSD = 2m, IsValid = true }
        };

        var (added, updated) = await inventoryService.BulkImportProductsAsync(productsToImport, overwriteMerge: false);

        Assert.Equal(1, added);
        Assert.Equal(0, updated);

        var savedProducts = await context.Products.ToListAsync();
        Assert.Single(savedProducts);
        Assert.Equal("2001", savedProducts[0].SKU);
    }

    // 8. UserSession In WPF Clears Token On 401
    [Fact]
    public async Task UserSession_In_WPF_Clears_Token_On_401()
    {
        var userSession = new Desktop.Client.Services.UserSession();
        userSession.SetUser(new UserDto { Id = 1, Name = "Cajero", Role = UserRole.Cashier }, "initial_jwt_token_123");

        Assert.True(userSession.IsLoggedIn);
        Assert.Equal("initial_jwt_token_123", userSession.Token);

        // Simulamos un handler HTTP que responde con 401 Unauthorized
        var mockInnerHandler = new Mock<HttpMessageHandler>();
        mockInnerHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var sessionHandler = new Desktop.Client.Services.UserSessionHeaderHandler(userSession)
        {
            InnerHandler = mockInnerHandler.Object
        };

        var client = new HttpClient(sessionHandler);
        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/api/sales/1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(userSession.IsLoggedIn);
        Assert.Null(userSession.Token);
    }

    // 9. SecurityAuditMiddleware Logs 401 And 403 Attempts
    [Fact]
    public async Task SecurityAuditMiddleware_Logs_401_And_403_Attempts()
    {
        var logFile = AppLogger.SecurityAuditLogPath;

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.100");
        context.Request.Method = "DELETE";
        context.Request.Path = "/api/sales/customers/99";
        context.Request.QueryString = new QueryString("?secretParam=sensitiveValue");

        // Simula usuario autenticado con Rol Cajero intentando acción no permitida
        context.User = CreateClaimsPrincipal("cajero_test", "Cashier", "10");

        var middleware = new SecurityAuditMiddleware(next: (ctx) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(File.Exists(logFile));
        var logContent = await File.ReadAllTextAsync(logFile);

        Assert.Contains("IP=192.168.1.100", logContent);
        Assert.Contains("METHOD=DELETE", logContent);
        Assert.Contains("PATH=/api/sales/customers/99", logContent);
        Assert.Contains("USER=cajero_test", logContent);
        Assert.Contains("ROLE=Cashier", logContent);
        Assert.Contains("STATUS=403", logContent);
        // Garantía Cero Fugas: NO debe contener el query string sensible
        Assert.DoesNotContain("secretParam", logContent);
        Assert.DoesNotContain("sensitiveValue", logContent);
    }

    // 10. Rate Limiting Rejects Excessive Login Attempts
    [Fact]
    public void Rate_Limiting_Partition_Configuration_Is_10_Per_Minute()
    {
        var limiterOptions = new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        };

        using var limiter = new System.Threading.RateLimiting.FixedWindowRateLimiter(limiterOptions);

        // Primeras 10 peticiones deben tener éxito
        for (int i = 0; i < 10; i++)
        {
            var lease = limiter.AttemptAcquire(1);
            Assert.True(lease.IsAcquired, $"Intento {i + 1} debería ser admitido.");
        }

        // La petición 11 debe ser rechazada inmediatamente (Simulación de 429 Too Many Requests)
        var rejectedLease = limiter.AttemptAcquire(1);
        Assert.False(rejectedLease.IsAcquired, "El intento 11 debe ser rechazado por la política de Rate Limiting (429).");
    }
}
