using System;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Middleware;
using Backend.API.Services;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class Phase3AuthenticationAndPolicyTests
{
    private readonly PasswordPolicyService _policyService = new();

    #region 1. Unit Tests for PasswordPolicyService

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ab1!")]
    [InlineData("Short1!")]
    public void ValidatePassword_RejectsShortOrEmptyPasswords(string password)
    {
        var (isValid, errorMsg) = _policyService.ValidatePassword(password);
        Assert.False(isValid);
        Assert.NotNull(errorMsg);
    }

    [Fact]
    public void ValidatePassword_RejectsExcessivelyLongPasswords_Over128Chars()
    {
        var longPassword = new string('A', 100) + new string('a', 25) + "123!@#" + "extra_chars"; // > 128 chars
        var (isValid, errorMsg) = _policyService.ValidatePassword(longPassword);
        Assert.False(isValid);
        Assert.Contains("128", errorMsg);
    }

    [Fact]
    public void ValidatePassword_RejectsMissingCharacterClasses()
    {
        // Falta mayúscula
        var (v1, e1) = _policyService.ValidatePassword("nouppercase123!");
        Assert.False(v1);
        Assert.Contains("mayúscula", e1);

        // Falta minúscula
        var (v2, e2) = _policyService.ValidatePassword("NOLOWERCASE123!");
        Assert.False(v2);
        Assert.Contains("minúscula", e2);

        // Falta dígito
        var (v3, e3) = _policyService.ValidatePassword("NoDigitsSpecial!@");
        Assert.False(v3);
        Assert.Contains("número", e3);

        // Falta especial
        var (v4, e4) = _policyService.ValidatePassword("NoSpecialChars123");
        Assert.False(v4);
        Assert.Contains("especial", e4);
    }

    [Fact]
    public void ValidatePassword_RejectsContainingUsernameOrCedula()
    {
        var (v1, e1) = _policyService.ValidatePassword("SuperAdmin2026!", username: "admin");
        Assert.False(v1);
        Assert.Contains("nombre de usuario o cédula", e1);

        var (v2, e2) = _policyService.ValidatePassword("MiClave28192831!", username: "V-28192831");
        Assert.False(v2);
        Assert.Contains("cédula", e2);
    }

    [Fact]
    public void ValidatePassword_RejectsCommonBlacklistPasswords()
    {
        var (v1, e1) = _policyService.ValidatePassword("posadmin");
        Assert.False(v1);
        Assert.Contains("común o predecible", e1);
    }

    [Fact]
    public void ValidatePassword_AcceptsCompliantPassword()
    {
        var (isValid, errorMsg) = _policyService.ValidatePassword("K#9mP$2vXw7!");
        Assert.True(isValid);
        Assert.Null(errorMsg);
    }

    [Fact]
    public void GenerateSecureTemporaryPassword_ExcludesAmbiguousChars_AndCompliesWithPolicy()
    {
        for (int i = 0; i < 20; i++)
        {
            var tempPass = _policyService.GenerateSecureTemporaryPassword(12);

            Assert.Equal(12, tempPass.Length);

            // Caracteres ambiguos prohibidos: 0, O, o, 1, l, I, 8, B
            var forbiddenAmbiguous = new[] { '0', 'O', 'o', '1', 'l', 'I', '8', 'B' };
            foreach (var ch in forbiddenAmbiguous)
            {
                Assert.DoesNotContain(ch, tempPass);
            }

            // Debe aprobar la política de contraseñas
            var (isValid, errorMsg) = _policyService.ValidatePassword(tempPass);
            Assert.True(isValid, $"Contraseña generada falló política: {errorMsg}");
        }
    }

    #endregion

    #region 2. Controller & Integration Tests

    [Fact]
    public async Task ChangePassword_RejectsWeakNewPassword_WithBadRequest()
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        var user = new User
        {
            Id = 10,
            Username = "cajero1",
            Cedula = "V-20000001",
            PasswordHash = PasswordHasher.HashPassword("OldStrongPass123!"),
            IsActive = true
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var tokenServiceMock = new Mock<ITokenService>();
        var authController = new AuthController(context, tokenServiceMock.Object, _policyService);

        var request = new ChangePasswordRequest
        {
            Cedula = "V-20000001",
            CurrentPassword = "OldStrongPass123!",
            NewPassword = "weak" // No cumple política
        };

        var result = await authController.ChangePassword(request);
        var badReq = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badReq.Value);
    }

    [Fact]
    public async Task ChangePassword_Succeeds_RegeneratesSecurityStamp_AndClearsMustChangePassword()
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        var initialStamp = "initial-security-stamp";
        var user = new User
        {
            Id = 11,
            Username = "cajero2",
            Cedula = "V-20000002",
            PasswordHash = PasswordHasher.HashPassword("OldStrongPass123!"),
            IsActive = true,
            MustChangePassword = true,
            SecurityStamp = initialStamp
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var tokenServiceMock = new Mock<ITokenService>();
        var stampValidatorMock = new Mock<ISecurityStampValidator>();
        var authController = new AuthController(context, tokenServiceMock.Object, _policyService, stampValidatorMock.Object);

        var request = new ChangePasswordRequest
        {
            Cedula = "V-20000002",
            CurrentPassword = "OldStrongPass123!",
            NewPassword = "NewStrong#Password2026"
        };

        var result = await authController.ChangePassword(request);
        Assert.IsType<OkObjectResult>(result);

        var updatedUser = await context.Users.FindAsync(11);
        Assert.NotNull(updatedUser);
        Assert.False(updatedUser.MustChangePassword);
        Assert.NotEqual(initialStamp, updatedUser.SecurityStamp);
        Assert.True(PasswordHasher.VerifyPassword("NewStrong#Password2026", updatedUser.PasswordHash));
        stampValidatorMock.Verify(s => s.InvalidateUserStamp(11), Times.Once);
    }

    [Fact]
    public async Task CreateUser_WithoutPassword_GeneratesSecureTemporaryPassword_AndSetsMustChange()
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        var stampValidatorMock = new Mock<ISecurityStampValidator>();
        var usersController = new UsersController(context, _policyService, stampValidatorMock.Object);

        var createDto = new CreateUserDto
        {
            Cedula = "V-30000001",
            Name = "Nuevo Operador",
            Password = null, // Sin clave explícita -> debe generar temporal aleatoria
            Role = UserRole.Cashier
        };

        var actionResult = await usersController.CreateUser(createDto);
        var createdResult = Assert.IsType<CreatedAtActionResult>(actionResult.Result);
        var createdUserDto = Assert.IsType<UserCreatedDto>(createdResult.Value);

        Assert.True(createdUserDto.MustChangePassword);
        Assert.NotNull(createdUserDto.TemporaryPassword);
        Assert.NotEqual("V-30000001", createdUserDto.TemporaryPassword); // NUNCA debe ser la cédula

        // Verificar que la clave temporal cumple la política
        var (isValid, _) = _policyService.ValidatePassword(createdUserDto.TemporaryPassword);
        Assert.True(isValid);

        // Verificar que el hash almacenado coincide con la contraseña temporal
        var dbUser = await context.Users.FindAsync(createdUserDto.Id);
        Assert.NotNull(dbUser);
        Assert.True(dbUser.MustChangePassword);
        Assert.True(PasswordHasher.VerifyPassword(createdUserDto.TemporaryPassword, dbUser.PasswordHash));
    }

    [Fact]
    public async Task ResetTemporaryPassword_GeneratesNewSecureKey_AndRegeneratesStamp()
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        var initialStamp = "stamp-before-reset";
        var user = new User
        {
            Id = 25,
            Username = "cajero_reset",
            Cedula = "V-40000001",
            PasswordHash = PasswordHasher.HashPassword("OldPass#1234"),
            IsActive = true,
            MustChangePassword = false,
            SecurityStamp = initialStamp
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var stampValidatorMock = new Mock<ISecurityStampValidator>();
        var usersController = new UsersController(context, _policyService, stampValidatorMock.Object);

        var actionResult = await usersController.ResetTemporaryPassword(25);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<ResetTemporaryPasswordResponseDto>(okResult.Value);

        Assert.Equal(25, response.UserId);
        Assert.NotNull(response.TemporaryPassword);

        var (isValid, _) = _policyService.ValidatePassword(response.TemporaryPassword);
        Assert.True(isValid);

        var updatedUser = await context.Users.FindAsync(25);
        Assert.NotNull(updatedUser);
        Assert.True(updatedUser.MustChangePassword);
        Assert.NotEqual(initialStamp, updatedUser.SecurityStamp);
        Assert.True(PasswordHasher.VerifyPassword(response.TemporaryPassword, updatedUser.PasswordHash));
        stampValidatorMock.Verify(s => s.InvalidateUserStamp(25), Times.Once);
    }

    [Fact]
    public async Task MustChangePasswordMiddleware_BlocksProtectedEndpoints_WhenClaimIsTrue()
    {
        var middleware = new MustChangePasswordMiddleware(innerContext => Task.CompletedTask);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/sales/1/items";
        context.Response.Body = new MemoryStream();

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim("must_change_password", "true")
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(context.Response.Body);
        var jsonText = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(jsonText);

        Assert.True(doc.RootElement.GetProperty("requiresPasswordChange").GetBoolean());
    }

    [Fact]
    public async Task MustChangePasswordMiddleware_AllowsChangePasswordEndpoint_EvenWhenClaimIsTrue()
    {
        bool nextCalled = false;
        var middleware = new MustChangePasswordMiddleware(innerContext =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/auth/change-password";

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim("must_change_password", "true")
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    #endregion
}
