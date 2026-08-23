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
        Assert.Equal("Contraseña (Opcional, por defecto Cédula)", vm.PasswordHint);

        // State when selecting a user (Editing)
        vm.SelectedUser = new UserDto { Id = 1, Cedula = "V-12345678", Name = "Test User" };
        Assert.True(vm.IsEditing);
        Assert.Equal("Contraseña (Dejar en blanco para conservar actual)", vm.PasswordHint);
        Assert.Empty(vm.Password);

        // State when clicking NewUser command
        vm.NewUserCommand.Execute(null);
        Assert.False(vm.IsEditing);
        Assert.Equal("Contraseña (Opcional, por defecto Cédula)", vm.PasswordHint);
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
}
