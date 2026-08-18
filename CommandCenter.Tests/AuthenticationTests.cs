using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Services;
using Core.DTOs;
using Core.Entities;
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
    public async Task Login_WithLegacyPlainTextPassword_UpgradesHashAndReturnsToken()
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

        var okResult = Assert.IsType<OkObjectResult>(response.Result);
        var resultDto = Assert.IsType<LoginResultDto>(okResult.Value);

        Assert.NotNull(resultDto.Token);

        // Verify that user's password in database was upgraded to PBKDF2
        var updatedUser = await db.Users.FindAsync(3);
        Assert.NotNull(updatedUser);
        Assert.StartsWith("PBKDF2$", updatedUser.PasswordHash);
        Assert.True(PasswordHasher.VerifyPassword(legacyPassword, updatedUser.PasswordHash));
    }
}
