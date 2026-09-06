using System;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Services;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Sales.Module.Data;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class Phase4SecurityHardeningTests
{
    private SalesDbContext CreateInMemoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new SalesDbContext(options);
    }

    private ITokenService CreateTokenService()
    {
        var inMemorySettings = new System.Collections.Generic.Dictionary<string, string?>
        {
            { "JWT_SETTINGS_KEY", "POS_Test_Super_Secret_Key_At_Least_32_Chars_Long!" },
            { "JwtSettings:Issuer", "SolucionesPos" },
            { "JwtSettings:Audience", "PosClient" },
            { "JwtSettings:ExpiryMinutes", "120" }
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        return new TokenService(config);
    }

    [Fact]
    public async Task AccountLockout_AfterFiveFailedAttempts_Returns423Locked()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var tokenService = CreateTokenService();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var validator = new SecurityStampValidator(db, cache);

        var user = new User
        {
            Id = 1,
            Cedula = "V-12345678",
            Username = "lockout_user",
            Name = "Lockout Test",
            PasswordHash = PasswordHasher.HashPassword("CorrectPassword123!"),
            Role = UserRole.Cashier,
            IsActive = true,
            AccessFailedCount = 0
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = new AuthController(db, tokenService, stampValidator: validator);

        // 4 failed attempts
        for (int i = 0; i < 4; i++)
        {
            var res = await controller.Login(new LoginRequest { Cedula = "V-12345678", Password = "WrongPassword" });
            Assert.IsType<UnauthorizedObjectResult>(res.Result);
        }

        var checkUser = await db.Users.FindAsync(1);
        Assert.Equal(4, checkUser!.AccessFailedCount);
        Assert.Null(checkUser.LockoutEndUtc);

        // 5th failed attempt -> locks account
        var fifthRes = await controller.Login(new LoginRequest { Cedula = "V-12345678", Password = "WrongPassword" });
        Assert.IsType<UnauthorizedObjectResult>(fifthRes.Result);

        checkUser = await db.Users.FindAsync(1);
        Assert.Equal(5, checkUser!.AccessFailedCount);
        Assert.NotNull(checkUser.LockoutEndUtc);
        Assert.True(checkUser.LockoutEndUtc.Value > DateTime.UtcNow);

        // 6th attempt (even with correct password) -> 401 genérico (anti-enumeración 8B-M3,
        // no se revela que la cuenta está bloqueada)
        var lockedRes = await controller.Login(new LoginRequest { Cedula = "V-12345678", Password = "CorrectPassword123!" });
        Assert.IsType<UnauthorizedObjectResult>(lockedRes.Result);
    }

    [Fact]
    public async Task AccountLockout_SuccessfulLogin_ResetsFailureCounter()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var tokenService = CreateTokenService();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var validator = new SecurityStampValidator(db, cache);

        var user = new User
        {
            Id = 2,
            Cedula = "V-87654321",
            Username = "reset_user",
            Name = "Reset Test",
            PasswordHash = PasswordHasher.HashPassword("CorrectPassword123!"),
            Role = UserRole.Cashier,
            IsActive = true,
            AccessFailedCount = 3
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = new AuthController(db, tokenService, stampValidator: validator);

        var okRes = await controller.Login(new LoginRequest { Cedula = "V-87654321", Password = "CorrectPassword123!" });
        Assert.IsType<OkObjectResult>(okRes.Result);

        var checkUser = await db.Users.FindAsync(2);
        Assert.Equal(0, checkUser!.AccessFailedCount);
        Assert.Null(checkUser.LockoutEndUtc);
        Assert.NotNull(checkUser.LastLoginUtc);
    }

    [Fact]
    public async Task SecurityStampValidator_ValidatesStamp_AndInvalidatesOnReset()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var validator = new SecurityStampValidator(db, cache);

        string initialStamp = Guid.NewGuid().ToString("N");
        var user = new User
        {
            Id = 10,
            Cedula = "V-10101010",
            Username = "stamp_user",
            Name = "Stamp Test",
            PasswordHash = PasswordHasher.HashPassword("Pass1234!"),
            Role = UserRole.Admin,
            IsActive = true,
            SecurityStamp = initialStamp
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // 1. Valid stamp -> returns true
        bool isValid = await validator.ValidateStampAsync(10, initialStamp);
        Assert.True(isValid);

        // 2. Wrong stamp -> returns false
        bool isWrongValid = await validator.ValidateStampAsync(10, "wrong_stamp_value");
        Assert.False(isWrongValid);

        // 3. User stamp changed in DB (e.g. password changed or role changed)
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync();
        validator.InvalidateUserStamp(10);

        // Old stamp now rejected
        bool isOldValid = await validator.ValidateStampAsync(10, initialStamp);
        Assert.False(isOldValid);

        // New stamp accepted
        bool isNewValid = await validator.ValidateStampAsync(10, user.SecurityStamp);
        Assert.True(isNewValid);
    }

    [Fact]
    public async Task SecurityStampValidator_WithMemoryCacheSizeLimit_SucceedsWithoutThrowing()
    {
        // Regression test: IMemoryCache with SizeLimit throws InvalidOperationException if Size is omitted on cache.Set
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var cacheOptions = new MemoryCacheOptions { SizeLimit = 1000 };
        var cache = new MemoryCache(cacheOptions);
        var validator = new SecurityStampValidator(db, cache);

        string stamp = Guid.NewGuid().ToString("N");
        var user = new User
        {
            Id = 20,
            Cedula = "V-20202020",
            Username = "sizelimit_user",
            Name = "SizeLimit Test",
            PasswordHash = PasswordHasher.HashPassword("Pass1234!"),
            Role = UserRole.Cashier,
            IsActive = true,
            SecurityStamp = stamp
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Must succeed and populate cache with Size=1 without throwing InvalidOperationException
        bool isValidFirstCall = await validator.ValidateStampAsync(20, stamp);
        Assert.True(isValidFirstCall);

        // Second call should hit the cache successfully
        bool isValidSecondCall = await validator.ValidateStampAsync(20, stamp);
        Assert.True(isValidSecondCall);
    }

    [Fact]
    public void UserRole_Manager_PermissionsAndDisplay_AreProperlyConfigured()
    {
        var session = new UserSession();
        var managerUser = new UserDto
        {
            Id = 5,
            Cedula = "V-55555555",
            Name = "Carlos Gerente",
            Role = UserRole.Manager,
            IsActive = true
        };

        session.SetUser(managerUser);

        Assert.True(session.IsManager);
        Assert.False(session.IsAdmin);
        Assert.False(session.IsCashier);
        Assert.True(session.CanMutateCatalog);
        Assert.False(session.CanMutateSettings); // Settings is Admin only
        Assert.False(session.CanMutateExchangeRate); // BCV rate mutation is Admin only
        Assert.Equal("Gerente", session.UserRoleDisplay);
    }
}
