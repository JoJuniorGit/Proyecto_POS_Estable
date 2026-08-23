using Backend.API.Controllers;
using Backend.API.Services;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using System;
using System.Globalization;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests;

public class UserPasswordAndStockFormattingTests
{
    private SalesDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private UsersController CreateControllerWithAdminUser(SalesDbContext context, int currentUserId = 1)
    {
        var controller = new UsersController(context);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, currentUserId.ToString()),
            new Claim(ClaimTypes.Role, "Admin")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };
        return controller;
    }

    [Fact]
    public async Task CreateUser_WithShortPassword_ReturnsBadRequest()
    {
        using var context = GetInMemoryDbContext();
        var controller = CreateControllerWithAdminUser(context);

        var dto = new CreateUserDto
        {
            Cedula = "V-20111222",
            Name = "Usuario Test",
            Password = "123" // < 4 chars
        };

        var result = await controller.CreateUser(dto);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateUser_WithValidCustomPassword_HashesAndSetsMustChangeFalse()
    {
        using var context = GetInMemoryDbContext();
        var controller = CreateControllerWithAdminUser(context);

        var dto = new CreateUserDto
        {
            Cedula = "V-20111222",
            Name = "Usuario Test",
            Password = "CustomPassword123"
        };

        var result = await controller.CreateUser(dto);
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var userDto = Assert.IsType<UserDto>(createdResult.Value);

        var savedUser = await context.Users.FindAsync(userDto.Id);
        Assert.NotNull(savedUser);
        Assert.False(savedUser.MustChangePassword);
        Assert.True(PasswordHasher.VerifyPassword("CustomPassword123", savedUser.PasswordHash));
    }

    [Fact]
    public async Task CreateUser_WithoutPassword_UsesCedulaAndSetsMustChangeTrue()
    {
        using var context = GetInMemoryDbContext();
        var controller = CreateControllerWithAdminUser(context);

        var dto = new CreateUserDto
        {
            Cedula = "V-20111222",
            Name = "Usuario Test",
            Password = null
        };

        var result = await controller.CreateUser(dto);
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var userDto = Assert.IsType<UserDto>(createdResult.Value);

        var savedUser = await context.Users.FindAsync(userDto.Id);
        Assert.NotNull(savedUser);
        Assert.True(savedUser.MustChangePassword);
        Assert.True(PasswordHasher.VerifyPassword("V-20111222", savedUser.PasswordHash));
    }

    [Fact]
    public async Task UpdateUser_WithValidPassword_UpdatesHashAndClearsMustChangePassword()
    {
        using var context = GetInMemoryDbContext();
        var user = new User
        {
            Id = 10,
            Cedula = "V-12345678",
            Name = "Cajero Uno",
            FullName = "Cajero Uno",
            Username = "V-12345678",
            PasswordHash = PasswordHasher.HashPassword("OldPassword123"),
            MustChangePassword = true,
            IsActive = true
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = CreateControllerWithAdminUser(context);

        var updateDto = new UpdateUserDto
        {
            Cedula = "V-12345678",
            Name = "Cajero Modificado",
            Password = "NewSecretPassword2026",
            IsActive = true
        };

        var result = await controller.UpdateUser(10, updateDto);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);

        var updatedUser = await context.Users.FindAsync(10);
        Assert.NotNull(updatedUser);
        Assert.Equal("Cajero Modificado", updatedUser.Name);
        Assert.False(updatedUser.MustChangePassword);
        Assert.True(PasswordHasher.VerifyPassword("NewSecretPassword2026", updatedUser.PasswordHash));
        Assert.False(PasswordHasher.VerifyPassword("OldPassword123", updatedUser.PasswordHash));
    }

    [Fact]
    public async Task UpdateUser_WithBlankPassword_KeepsExistingHash()
    {
        using var context = GetInMemoryDbContext();
        var oldHash = PasswordHasher.HashPassword("OriginalSecret999");
        var user = new User
        {
            Id = 11,
            Cedula = "V-87654321",
            Name = "Cajero Dos",
            FullName = "Cajero Dos",
            Username = "V-87654321",
            PasswordHash = oldHash,
            MustChangePassword = true,
            IsActive = true
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = CreateControllerWithAdminUser(context);

        var updateDto = new UpdateUserDto
        {
            Cedula = "V-87654321",
            Name = "Cajero Dos Renombrado",
            Password = null, // Blank / unchanged
            IsActive = true
        };

        var result = await controller.UpdateUser(11, updateDto);
        Assert.IsType<OkObjectResult>(result.Result);

        var updatedUser = await context.Users.FindAsync(11);
        Assert.NotNull(updatedUser);
        Assert.Equal(oldHash, updatedUser.PasswordHash);
        Assert.True(updatedUser.MustChangePassword);
    }

    [Fact]
    public async Task UpdateUser_WithShortPassword_ReturnsBadRequest()
    {
        using var context = GetInMemoryDbContext();
        var user = new User
        {
            Id = 12,
            Cedula = "V-99999999",
            Name = "Cajero Tres",
            FullName = "Cajero Tres",
            Username = "V-99999999",
            PasswordHash = PasswordHasher.HashPassword("Pass1234"),
            IsActive = true
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = CreateControllerWithAdminUser(context);

        var updateDto = new UpdateUserDto
        {
            Cedula = "V-99999999",
            Name = "Cajero Tres",
            Password = "abc", // < 4 chars
            IsActive = true
        };

        var result = await controller.UpdateUser(12, updateDto);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void FormattedStockQuantity_WithDifferentDecimals_ReturnsExpectedFormat()
    {
        var originalCulture = Thread.CurrentThread.CurrentCulture;
        try
        {
            // Test with en-US culture (dot decimal, comma thousand)
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");

            var mockExchangeRate = new Mock<IExchangeRateService>();
            mockExchangeRate.Setup(x => x.CurrentRate).Returns(50m);

            var p1 = new ProductItemViewModel(new ProductDto { StockQuantity = 10.0000m }, mockExchangeRate.Object, _ => { });
            Assert.Equal("10", p1.FormattedStockQuantity);

            var p2 = new ProductItemViewModel(new ProductDto { StockQuantity = 1.5000m }, mockExchangeRate.Object, _ => { });
            Assert.Equal("1.5", p2.FormattedStockQuantity);

            var p3 = new ProductItemViewModel(new ProductDto { StockQuantity = 1.7500m }, mockExchangeRate.Object, _ => { });
            Assert.Equal("1.75", p3.FormattedStockQuantity);

            var p4 = new ProductItemViewModel(new ProductDto { StockQuantity = 0.000m }, mockExchangeRate.Object, _ => { });
            Assert.Equal("0", p4.FormattedStockQuantity);

            var p5 = new ProductItemViewModel(new ProductDto { StockQuantity = 1250.750m }, mockExchangeRate.Object, _ => { });
            Assert.Equal("1,250.75", p5.FormattedStockQuantity);

            // Test dynamic property change
            p1.StockQuantity = 25.1250m;
            Assert.Equal("25.125", p1.FormattedStockQuantity);

            p1.StockQuantity = 0m;
            Assert.Equal("0", p1.FormattedStockQuantity);
            Assert.True(p1.IsStockCritical);

            // Test with es-VE culture (comma decimal, dot thousand)
            Thread.CurrentThread.CurrentCulture = new CultureInfo("es-VE");
            Assert.Equal("1.250,75", p5.FormattedStockQuantity);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void UsersManagementViewModel_PasswordHint_ChangesWithIsEditing()
    {
        var mockUserService = new Mock<IUserService>();
        var userSession = new UserSession();
        var mockSalesService = new Mock<ISalesService>();
        var mockDialogService = new Mock<IDialogService>();
        var customerVm = new CustomerManagementViewModel(mockSalesService.Object, userSession, mockDialogService.Object);

        var vm = new UsersManagementViewModel(mockUserService.Object, userSession, customerVm);

        // Initial state (Creating)
        Assert.False(vm.IsEditing);
        Assert.Equal("Contraseña (Opcional, por defecto Usuario)", vm.PasswordHint);

        // State when selecting a user (Editing)
        vm.SelectedUser = new UserDto { Id = 1, Cedula = "V-12345678", Name = "Test User" };
        Assert.True(vm.IsEditing);
        Assert.Equal("Contraseña (Dejar en blanco para conservar actual)", vm.PasswordHint);
        Assert.Empty(vm.Password);

        // State when clicking NewUser command
        vm.NewUserCommand.Execute(null);
        Assert.False(vm.IsEditing);
        Assert.Equal("Contraseña (Opcional, por defecto Usuario)", vm.PasswordHint);
    }

    [Fact]
    public void UsersManagementViewModel_TogglePasswordVisibility_TogglesVisibilityState()
    {
        var mockUserService = new Mock<IUserService>();
        var userSession = new UserSession();
        var mockSalesService = new Mock<ISalesService>();
        var mockDialogService = new Mock<IDialogService>();
        var customerVm = new CustomerManagementViewModel(mockSalesService.Object, userSession, mockDialogService.Object);

        var vm = new UsersManagementViewModel(mockUserService.Object, userSession, customerVm);

        Assert.False(vm.IsPasswordVisible);

        // Toggle on
        vm.TogglePasswordVisibilityCommand.Execute(null);
        Assert.True(vm.IsPasswordVisible);

        // Toggle off
        vm.TogglePasswordVisibilityCommand.Execute(null);
        Assert.False(vm.IsPasswordVisible);

        // Reset on selection change
        vm.TogglePasswordVisibilityCommand.Execute(null);
        Assert.True(vm.IsPasswordVisible);
        vm.SelectedUser = new UserDto { Id = 2, Cedula = "V-22222222", Name = "User Two" };
        Assert.False(vm.IsPasswordVisible);
    }

    [Fact]
    public void LoginViewModel_TogglePasswordVisibility_TogglesVisibilityState()
    {
        var mockUserService = new Mock<IUserService>();
        var mockDialogService = new Mock<IDialogService>();
        var userSession = new UserSession();

        var vm = new LoginViewModel(mockUserService.Object, mockDialogService.Object, userSession);

        Assert.False(vm.IsPasswordVisible);

        // Toggle on
        vm.TogglePasswordVisibilityCommand.Execute(null);
        Assert.True(vm.IsPasswordVisible);

        // Toggle off
        vm.TogglePasswordVisibilityCommand.Execute(null);
        Assert.False(vm.IsPasswordVisible);

        // Reset on Clear
        vm.TogglePasswordVisibilityCommand.Execute(null);
        Assert.True(vm.IsPasswordVisible);
        vm.ClearCommand.Execute(null);
        Assert.False(vm.IsPasswordVisible);
    }

    [Fact]
    public async Task CreateUser_WithCustomAlphanumericUsername_Succeeds()
    {
        using var context = GetInMemoryDbContext();
        var controller = CreateControllerWithAdminUser(context);

        var dto = new CreateUserDto
        {
            Cedula = "cajero_central",
            Name = "Carlos Cajero",
            Password = "SecurePass2026"
        };

        var result = await controller.CreateUser(dto);
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var userDto = Assert.IsType<UserDto>(createdResult.Value);

        var savedUser = await context.Users.FindAsync(userDto.Id);
        Assert.NotNull(savedUser);
        Assert.Equal("cajero_central", savedUser.Username);
        Assert.Equal("cajero_central", savedUser.Cedula);
        Assert.Equal("Carlos Cajero", savedUser.Name);
    }

    [Fact]
    public async Task CreateUser_WithDuplicateUsernameCaseInsensitive_ReturnsBadRequest()
    {
        using var context = GetInMemoryDbContext();
        var existing = new User
        {
            Id = 5,
            Cedula = "Admin_Tarde",
            Username = "Admin_Tarde",
            Name = "Admin Existente",
            PasswordHash = PasswordHasher.HashPassword("Pass1234"),
            IsActive = true
        };
        context.Users.Add(existing);
        await context.SaveChangesAsync();

        var controller = CreateControllerWithAdminUser(context);

        // Attempt to create duplicate with different casing: "admin_tarde"
        var dto = new CreateUserDto
        {
            Cedula = "admin_tarde",
            Name = "Nuevo Admin Duplicado",
            Password = "Password999"
        };

        var result = await controller.CreateUser(dto);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public async Task Login_WithCaseInsensitiveUsername_Succeeds()
    {
        using var context = GetInMemoryDbContext();
        var user = new User
        {
            Id = 20,
            Cedula = "Supervisor_General",
            Username = "Supervisor_General",
            Name = "Supervisor",
            FullName = "Supervisor",
            PasswordHash = PasswordHasher.HashPassword("ExactPassword123!"),
            IsActive = true
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var mockTokenService = new Mock<ITokenService>();
        mockTokenService.Setup(t => t.GenerateToken(It.IsAny<User>())).Returns("fake-jwt-token");

        var authController = new AuthController(context, mockTokenService.Object);

        // Login with lowercase "supervisor_general"
        var request = new LoginRequest
        {
            Cedula = "supervisor_general",
            Password = "ExactPassword123!"
        };

        var result = await authController.Login(request);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var loginResult = Assert.IsType<LoginResultDto>(okResult.Value);
        Assert.NotNull(loginResult.Token);
        Assert.Equal("Supervisor_General", loginResult.User?.Cedula);
    }

    [Fact]
    public async Task Login_WithWrongCasePassword_FailsCaseSensitive()
    {
        using var context = GetInMemoryDbContext();
        var user = new User
        {
            Id = 21,
            Cedula = "cajero_uno",
            Username = "cajero_uno",
            Name = "Cajero Uno",
            FullName = "Cajero Uno",
            PasswordHash = PasswordHasher.HashPassword("CaseSensitivePass1"),
            IsActive = true
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var mockTokenService = new Mock<ITokenService>();
        var authController = new AuthController(context, mockTokenService.Object);

        // Login with all lowercase password (mismatch case)
        var request = new LoginRequest
        {
            Cedula = "cajero_uno",
            Password = "casesensitivepass1" // Lowercase mismatch
        };

        var result = await authController.Login(request);
        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task LoginViewModel_EmptyUsername_SetsUsuarioErrorMessage()
    {
        var mockUserService = new Mock<IUserService>();
        var mockDialogService = new Mock<IDialogService>();
        var userSession = new UserSession();

        var vm = new LoginViewModel(mockUserService.Object, mockDialogService.Object, userSession)
        {
            Cedula = "",
            Password = "password123"
        };

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal("Por favor ingrese su usuario.", vm.ErrorMessage);
    }
}
