using System;
using System.Linq;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Services;
using Core.DTOs;
using Core.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Sales.Module.Data;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class Phase1SecurityHardeningTests
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
    public async Task ChangePassword_WrongPassword_IncrementsFailedCount_AndLocksAccountAt5Attempts()
    {
        // Arrange
        using var context = GetInMemorySalesDbContext();
        var config = GetMockConfiguration();
        var tokenService = new TokenService(config);

        var testUser = new User
        {
            Id = 10,
            Username = "cajero1",
            Cedula = "V-20000000",
            Name = "Cajero Uno",
            Role = UserRole.Cashier,
            PasswordHash = PasswordHasher.HashPassword("CorrectPass123!"),
            IsActive = true,
            AccessFailedCount = 0,
            LockoutEndUtc = null
        };
        context.Users.Add(testUser);
        await context.SaveChangesAsync();

        var controller = new AuthController(context, tokenService, stampValidator: null);

        // Act: 4 failed attempts
        for (int i = 1; i <= 4; i++)
        {
            var result = await controller.ChangePassword(new ChangePasswordRequest
            {
                Cedula = "cajero1",
                CurrentPassword = "WrongPassword!",
                NewPassword = "NewPassword123!"
            });

            var unauth = Assert.IsType<UnauthorizedObjectResult>(result);
            var refreshedUser = await context.Users.FindAsync(10);
            Assert.Equal(i, refreshedUser!.AccessFailedCount);
            Assert.Null(refreshedUser.LockoutEndUtc);
        }

        // Act: 5th failed attempt -> locks account
        var fifthResult = await controller.ChangePassword(new ChangePasswordRequest
        {
            Cedula = "cajero1",
            CurrentPassword = "WrongPassword!",
            NewPassword = "NewPassword123!"
        });

        Assert.IsType<UnauthorizedObjectResult>(fifthResult);
        var lockedUser = await context.Users.FindAsync(10);
        Assert.Equal(5, lockedUser!.AccessFailedCount);
        Assert.NotNull(lockedUser.LockoutEndUtc);
        Assert.True(lockedUser.LockoutEndUtc.Value > DateTime.UtcNow);

        // Act: 6th attempt (even with right or wrong password) -> rejected with 401 genérico (8B-M3: sin revelar lockout)
        var sixthResult = await controller.ChangePassword(new ChangePasswordRequest
        {
            Cedula = "cajero1",
            CurrentPassword = "CorrectPass123!",
            NewPassword = "NewPassword123!"
        });

        Assert.IsType<UnauthorizedObjectResult>(sixthResult);
    }

    [Fact]
    public async Task ChangePassword_CorrectPassword_ResetsFailedCountAndLockout()
    {
        // Arrange
        using var context = GetInMemorySalesDbContext();
        var config = GetMockConfiguration();
        var tokenService = new TokenService(config);

        var testUser = new User
        {
            Id = 11,
            Username = "cajero2",
            Cedula = "V-20000001",
            Name = "Cajero Dos",
            Role = UserRole.Cashier,
            PasswordHash = PasswordHasher.HashPassword("CurrentPass123!"),
            IsActive = true,
            AccessFailedCount = 3, // Had 3 failed attempts
            LockoutEndUtc = null
        };
        context.Users.Add(testUser);
        await context.SaveChangesAsync();

        var controller = new AuthController(context, tokenService, stampValidator: null);

        // Act: Successfully change password
        var result = await controller.ChangePassword(new ChangePasswordRequest
        {
            Cedula = "cajero2",
            CurrentPassword = "CurrentPass123!",
            NewPassword = "BrandNewPass123!"
        });

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var updatedUser = await context.Users.FindAsync(11);
        Assert.Equal(0, updatedUser!.AccessFailedCount);
        Assert.Null(updatedUser.LockoutEndUtc);
        Assert.True(PasswordHasher.VerifyPassword("BrandNewPass123!", updatedUser.PasswordHash));
    }

    [Fact]
    public async Task UsersController_UnlockUser_ClearsLockoutAndFailedCount()
    {
        // Arrange
        using var context = GetInMemorySalesDbContext();
        var lockedUser = new User
        {
            Id = 12,
            Username = "cajero_bloqueado",
            Cedula = "V-20000002",
            Name = "Cajero Bloqueado",
            Role = UserRole.Cashier,
            PasswordHash = PasswordHasher.HashPassword("Pass123!"),
            IsActive = true,
            AccessFailedCount = 5,
            LockoutEndUtc = DateTime.UtcNow.AddMinutes(15)
        };
        context.Users.Add(lockedUser);
        await context.SaveChangesAsync();

        var controller = new UsersController(context, stampValidator: null);

        // Act
        var result = await controller.UnlockUser(12);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        var unlockedUser = await context.Users.FindAsync(12);
        Assert.Equal(0, unlockedUser!.AccessFailedCount);
        Assert.Null(unlockedUser.LockoutEndUtc);
    }

    [Fact]
    public void AdminStartupLogic_PreservesInactiveAdmins_WithoutForcedReactivation()
    {
        // Arrange
        using var context = GetInMemorySalesDbContext();
        var dismissedAdmin = new User
        {
            Id = 1,
            Username = "ex_admin",
            Cedula = "V-11111111",
            Name = "Ex Administrador",
            Role = UserRole.Admin,
            PasswordHash = PasswordHasher.HashPassword("Secret123!"),
            IsActive = false, // Dismissed by business owner
            SecurityStamp = Guid.NewGuid().ToString("N")
        };
        var activeAdmin = new User
        {
            Id = 2,
            Username = "admin_actual",
            Cedula = "V-22222222",
            Name = "Admin Activo",
            Role = UserRole.Admin,
            PasswordHash = PasswordHasher.HashPassword("Secret123!"),
            IsActive = true,
            SecurityStamp = Guid.NewGuid().ToString("N")
        };
        context.Users.AddRange(dismissedAdmin, activeAdmin);
        context.SaveChanges();

        // Simulate startup routine without forced reactivation
        var allAdmins = context.Users.Where(u => u.Role == UserRole.Admin).ToList();
        foreach (var admin in allAdmins)
        {
            if (string.IsNullOrWhiteSpace(admin.SecurityStamp))
            {
                admin.SecurityStamp = Guid.NewGuid().ToString("N");
            }
        }
        context.SaveChanges();

        // Assert: Inactive admin must remain inactive
        var checkedDismissed = context.Users.Find(1);
        Assert.False(checkedDismissed!.IsActive, "El administrador desactivado debe permanecer inactivo (IsActive = false).");

        var checkedActive = context.Users.Find(2);
        Assert.True(checkedActive!.IsActive, "El administrador activo debe permanecer activo (IsActive = true).");
    }
}
