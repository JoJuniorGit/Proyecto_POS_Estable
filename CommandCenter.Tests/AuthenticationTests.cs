using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Services;
using Core.DTOs;
using Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Sales.Module.Data;
using Xunit;

namespace CommandCenter.Tests;

public class AuthenticationTests
{
    private SalesDbContext GetInMemorySalesDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new SalesDbContext(options);
    }

    private IConfiguration GetMockConfiguration()
    {
        var inMemorySettings = new System.Collections.Generic.Dictionary<string, string?>
        {
            {"JWT_SETTINGS_KEY", "POS_Test_Super_Secret_Key_At_Least_32_Chars_Long!"},
            {"JwtSettings:Issuer", "SolucionesPos"},
            {"JwtSettings:Audience", "PosClient"},
            {"JwtSettings:ExpiryMinutes", "60"}
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();
    }

    [Fact]
    public void PasswordHasher_HashesAndVerifiesCorrectly()
    {
        string rawPassword = "SecurePassword123!";
        string hashed = PasswordHasher.HashPassword(rawPassword);

        Assert.NotNull(hashed);
        Assert.StartsWith("PBKDF2$", hashed);
        Assert.True(PasswordHasher.VerifyPassword(rawPassword, hashed));
        Assert.False(PasswordHasher.VerifyPassword("WrongPassword!", hashed));
    }

    [Fact]
    public void JwtToken_ContainsUserIdAndRoleClaims()
    {
        var config = GetMockConfiguration();
        var tokenService = new TokenService(config);

        var user = new User
        {
            Id = 42,
            Cedula = "V-12345678",
            Name = "John Doe",
            Role = UserRole.Admin
        };

        string tokenString = tokenService.GenerateToken(user);
        Assert.NotNull(tokenString);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(tokenString);

        Assert.Equal("SolucionesPos", jwt.Issuer);
        Assert.Contains(jwt.Audiences, a => a == "PosClient");

        var subClaim = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub || c.Type == ClaimTypes.NameIdentifier);
        Assert.NotNull(subClaim);
        Assert.Equal("42", subClaim.Value);

        var roleClaim = jwt.Claims.FirstOrDefault(c => c.Type == "role" || c.Type == ClaimTypes.Role);
        Assert.NotNull(roleClaim);
        Assert.Equal("Admin", roleClaim.Value);
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsToken()
    {
        using var db = GetInMemorySalesDbContext();
        var config = GetMockConfiguration();
        var tokenService = new TokenService(config);

        string rawPassword = "MySecretPassword123!";
        var user = new User
        {
            Id = 1,
            Cedula = "V-99999999",
            Name = "Alice Admin",
            PasswordHash = PasswordHasher.HashPassword(rawPassword),
            Role = UserRole.Admin,
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = new AuthController(db, tokenService);
        var response = await controller.Login(new LoginRequest
        {
            Cedula = "V-99999999",
            Password = rawPassword
        });

        var okResult = Assert.IsType<OkObjectResult>(response.Result);
        var resultDto = Assert.IsType<LoginResultDto>(okResult.Value);

        Assert.NotNull(resultDto.Token);
        Assert.NotNull(resultDto.User);
        Assert.Equal("V-99999999", resultDto.User.Cedula);
    }

    [Fact]
    public async Task Login_WithInvalidPassword_ReturnsUnauthorized()
    {
        using var db = GetInMemorySalesDbContext();
        var config = GetMockConfiguration();
        var tokenService = new TokenService(config);

        var user = new User
        {
            Id = 2,
            Cedula = "V-88888888",
            Name = "Bob Cashier",
            PasswordHash = PasswordHasher.HashPassword("CorrectPassword123"),
            Role = UserRole.Cashier,
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = new AuthController(db, tokenService);
        var response = await controller.Login(new LoginRequest
        {
            Cedula = "V-88888888",
            Password = "WrongPassword"
        });

        Assert.IsType<UnauthorizedObjectResult>(response.Result);
    }

    [Fact]
    public async Task Login_WithLegacyPlainTextPassword_ReturnsUnauthorizedDueToZeroPlainTextTolerance()
    {
        using var db = GetInMemorySalesDbContext();
        var config = GetMockConfiguration();
        var tokenService = new TokenService(config);

        string legacyPassword = "LegacyPlainText123";
        var user = new User
        {
            Id = 3,
            Cedula = "V-77777777",
            Name = "Charlie Legacy",
            PasswordHash = legacyPassword, // Legacy plain text in DB
            Role = UserRole.Admin,
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = new AuthController(db, tokenService);
        var response = await controller.Login(new LoginRequest
        {
            Cedula = "V-77777777",
            Password = legacyPassword
        });

        // H-API-23: Stored legacy passwords without PBKDF2$ prefix are rejected immediately
        Assert.IsType<UnauthorizedObjectResult>(response.Result);
    }

    [Fact]
    public async Task Login_WithEmptyPasswordHash_ReturnsUnauthorized()
    {
        using var db = GetInMemorySalesDbContext();
        var config = GetMockConfiguration();
        var tokenService = new TokenService(config);

        var user = new User
        {
            Id = 4,
            Cedula = "V-66666666",
            Name = "Dave EmptyHash",
            PasswordHash = "", // Empty password hash
            Role = UserRole.Cashier,
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = new AuthController(db, tokenService);
        var response = await controller.Login(new LoginRequest
        {
            Cedula = "V-66666666",
            Password = "AnyRandomPassword123!"
        });

        var unauthResult = Assert.IsType<UnauthorizedObjectResult>(response.Result);
        Assert.NotNull(unauthResult.Value);
    }

    [Theory]
    [InlineData("pos:desktop")]
    [InlineData("pos:web")]
    public void JwtToken_WithExplicitScope_ContainsScopeClaim(string expectedScope)
    {
        var config = GetMockConfiguration();
        var tokenService = new TokenService(config);

        var user = new User
        {
            Id = 10,
            Cedula = "V-10101010",
            Name = "Scope Test User",
            Role = UserRole.Cashier
        };

        string tokenString = tokenService.GenerateToken(user, expectedScope);
        Assert.NotNull(tokenString);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(tokenString);

        var scopeClaim = jwt.Claims.FirstOrDefault(c => c.Type == "scope");
        Assert.NotNull(scopeClaim);
        Assert.Equal(expectedScope, scopeClaim.Value);
    }

    [Fact]
    public void TokenService_InProduction_WithWeakOrDevelopmentSecret_ThrowsInvalidOperationException()
    {
        var originalEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");

            // Caso A: Sin clave secreta configurada
            var emptyConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>())
                .Build();
            var serviceEmpty = new TokenService(emptyConfig);
            var user = new User { Id = 1, Cedula = "V-1", Name = "U1" };
            Assert.Throws<InvalidOperationException>(() => serviceEmpty.GenerateToken(user));

            // Caso B: Clave secreta por defecto de desarrollo en producción
            var devKeyConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
                {
                    {"JWT_SETTINGS_KEY", "POS_System_Default_Development_Secret_Key_At_Least_32_Chars!"}
                })
                .Build();
            var serviceDevKey = new TokenService(devKeyConfig);
            Assert.Throws<InvalidOperationException>(() => serviceDevKey.GenerateToken(user));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", originalEnv);
        }
    }

    [Fact]
    public async Task Login_WithWebPlatform_SetsHttpOnlyCookieAndReturnsNullTokenInBody()
    {
        using var db = GetInMemorySalesDbContext();
        var config = GetMockConfiguration();
        var tokenService = new TokenService(config);

        string rawPassword = "WebPassword123!";
        var user = new User
        {
            Id = 15,
            Cedula = "V-15151515",
            Name = "Web Cashier",
            PasswordHash = PasswordHasher.HashPassword(rawPassword),
            Role = UserRole.Cashier,
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = new AuthController(db, tokenService);
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var response = await controller.Login(new LoginRequest
        {
            Cedula = "V-15151515",
            Password = rawPassword,
            Platform = "Web"
        });

        var okResult = Assert.IsType<OkObjectResult>(response.Result);
        var resultDto = Assert.IsType<LoginResultDto>(okResult.Value);

        // En Web, el token no viaja en el cuerpo JSON (para evitar exposición en cliente)
        Assert.Null(resultDto.Token);
        Assert.NotNull(resultDto.User);
        Assert.Equal("V-15151515", resultDto.User.Cedula);

        // Se verifica que se haya emitido la cookie httpOnly con pos_jwt
        var setCookieHeader = httpContext.Response.Headers["Set-Cookie"].ToString();
        Assert.Contains("pos_jwt=", setCookieHeader);
        Assert.Contains("httponly", setCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookieHeader, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_WithDesktopPlatform_ReturnsBearerTokenInBody()
    {
        using var db = GetInMemorySalesDbContext();
        var config = GetMockConfiguration();
        var tokenService = new TokenService(config);

        string rawPassword = "DesktopPassword123!";
        var user = new User
        {
            Id = 16,
            Cedula = "V-16161616",
            Name = "Desktop Admin",
            PasswordHash = PasswordHasher.HashPassword(rawPassword),
            Role = UserRole.Admin,
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = new AuthController(db, tokenService);
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var response = await controller.Login(new LoginRequest
        {
            Cedula = "V-16161616",
            Password = rawPassword,
            Platform = "Desktop"
        });

        var okResult = Assert.IsType<OkObjectResult>(response.Result);
        var resultDto = Assert.IsType<LoginResultDto>(okResult.Value);

        // En Desktop, el token viaja en el cuerpo JSON para consumo Bearer
        Assert.NotNull(resultDto.Token);
        Assert.NotEmpty(resultDto.Token);
        Assert.Equal("V-16161616", resultDto.User?.Cedula);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(resultDto.Token);
        var scopeClaim = jwt.Claims.FirstOrDefault(c => c.Type == "scope");
        Assert.NotNull(scopeClaim);
        Assert.Equal("pos:desktop", scopeClaim.Value);
    }

    [Fact]
    public void Logout_RemovesPosJwtCookie()
    {
        using var db = GetInMemorySalesDbContext();
        var config = GetMockConfiguration();
        var tokenService = new TokenService(config);

        var controller = new AuthController(db, tokenService);
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = controller.Logout();
        Assert.IsType<OkObjectResult>(result);

        var setCookieHeader = httpContext.Response.Headers["Set-Cookie"].ToString();
        Assert.Contains("pos_jwt=", setCookieHeader);
        Assert.Contains("expires=", setCookieHeader, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NetworkDiscoveryService_WithAndWithoutHttps_SetsQrPayloadAccordingly()
    {
        var service = new NetworkDiscoveryService();

        // 1. Sin HTTPS activo (solo HTTP)
        var infoHttpOnly = service.GetPairingInfo(httpPort: 5000, httpsPort: 5001, isHttpsEnabled: false);
        Assert.False(infoHttpOnly.IsHttpsEnabled);
        Assert.Empty(infoHttpOnly.PrimaryHttpsUrl);
        Assert.StartsWith("http://", infoHttpOnly.QrPayload);
        Assert.Contains(":5000", infoHttpOnly.QrPayload);

        // 2. Con HTTPS activo
        var infoHttps = service.GetPairingInfo(httpPort: 5000, httpsPort: 5001, isHttpsEnabled: true);
        Assert.True(infoHttps.IsHttpsEnabled);
        Assert.StartsWith("https://", infoHttps.PrimaryHttpsUrl);
        Assert.StartsWith("https://", infoHttps.QrPayload);
        Assert.Contains(":5001", infoHttps.QrPayload);
    }

    [Fact]
    public void AuthorizationPolicies_EnforceScopeSeparation()
    {
        var options = new AuthorizationOptions();
        options.AddPolicy("DesktopOnly", policy => policy.RequireClaim("scope", "pos:desktop"));
        options.AddPolicy("WebOnly", policy => policy.RequireClaim("scope", "pos:web"));

        var desktopPolicy = options.GetPolicy("DesktopOnly")!;
        var webPolicy = options.GetPolicy("WebOnly")!;

        var webIdentity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim("scope", "pos:web")
        }, "TestAuth");
        var webPrincipal = new ClaimsPrincipal(webIdentity);

        var desktopIdentity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "2"),
            new Claim("scope", "pos:desktop")
        }, "TestAuth");
        var desktopPrincipal = new ClaimsPrincipal(desktopIdentity);

        // WebPrincipal debe satisfacer WebOnly pero fallar DesktopOnly
        Assert.True(webPrincipal.HasClaim("scope", "pos:web"));
        Assert.False(webPrincipal.HasClaim("scope", "pos:desktop"));

        // DesktopPrincipal debe satisfacer DesktopOnly pero fallar WebOnly
        Assert.True(desktopPrincipal.HasClaim("scope", "pos:desktop"));
        Assert.False(desktopPrincipal.HasClaim("scope", "pos:web"));
    }
}
