using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Core.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T2 (8.150): maquina de estados de autorizaciones remotas (design D2). Cubre creacion y
/// dedupe (incluido SaleId NULL, residual T1), resolucion remota race-safe, expiracion lazy y
/// por barrido, fallback local con lockout y consumo single-use del token efimero.
/// </summary>
public class AuthorizationServiceTests
{
    private const int FourKilobytes = 4096;
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);
    private static readonly DateTime FixedUtc = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private sealed class DatabaseScope : IDisposable
    {
        private readonly SqliteConnection _connection;

        public SalesDbContext Db { get; }

        public DatabaseScope()
        {
            (Db, _connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    private static AuthorizationService CreateService(
        SalesDbContext db,
        int requestTimeoutSeconds = 60,
        int tokenTtlSeconds = 60)
        => new(db, TimeSpan.FromSeconds(requestTimeoutSeconds), TimeSpan.FromSeconds(tokenTtlSeconds));

    private static CreateAuthorizationRequestDto Command(
        int userId = 70,
        UserRole role = UserRole.Cashier,
        AuthorizationActionType action = AuthorizationActionType.ManualPriceOverride,
        int? saleId = 445,
        string? contextJson = null,
        string? contextHash = null,
        string? terminal = "Caja-01")
        => new()
        {
            RequestedByUserId = userId,
            RequestedByName = $"Cajero {userId}",
            RequesterRole = role,
            ActionType = action,
            SaleId = saleId,
            ContextJson = contextJson ?? """{"productId":10,"quantity":2,"customUnitPriceUsd":9.99}""",
            ContextHash = contextHash ?? HashA,
            Terminal = terminal
        };

    private static async Task<User> SeedUserAsync(
        SalesDbContext db,
        int id,
        UserRole role,
        string? password = null,
        bool isActive = true,
        int accessFailedCount = 0,
        DateTime? lockoutEndUtc = null)
    {
        var user = new User
        {
            Id = id,
            Cedula = $"V-{id:D8}",
            Name = $"Usuario {id}",
            Username = $"usuario{id}",
            FullName = $"Usuario {id}",
            Role = role,
            IsActive = isActive,
            PasswordHash = password is null ? string.Empty : PasswordHasher.HashPassword(password),
            AccessFailedCount = accessFailedCount,
            LockoutEndUtc = lockoutEndUtc
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task SeedSaleAsync(
        SalesDbContext db,
        int id,
        int? cashierId,
        SaleStatus status = SaleStatus.Pending)
    {
        db.Sales.Add(new Sale { Id = id, CashierId = cashierId, Status = status, Date = FixedUtc });
        await db.SaveChangesAsync();
    }

    private static async Task<AuthorizationRequest> LoadRequestAsync(SalesDbContext db, int id)
        => await db.AuthorizationRequests.AsNoTracking().SingleAsync(candidate => candidate.Id == id);

    private static async Task UpdateRequestAsync(SalesDbContext db, int id, Action<AuthorizationRequest> mutate)
    {
        var request = await db.AuthorizationRequests.SingleAsync(candidate => candidate.Id == id);
        mutate(request);
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- creacion

    [Fact]
    public async Task CreateAsync_CashierOwnSale_PersistsPendingWithSecondsPrecisionAndSixtySecondLifetime()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        await SeedUserAsync(db, 70, UserRole.Cashier);
        await SeedSaleAsync(db, 445, cashierId: 70);
        var service = CreateService(db);

        var result = await service.CreateAsync(Command(userId: 70));

        Assert.Equal(CreateAuthorizationOutcome.Created, result.Outcome);
        Assert.NotNull(result.Request);
        Assert.Equal(AuthorizationStatus.Pending, result.Request!.Status);
        Assert.Equal(60, result.Request.RemainingLifetimeSeconds);
        Assert.Equal(0, result.Request.CreatedAt.Ticks % TimeSpan.TicksPerSecond);
        Assert.Equal(result.Request.CreatedAt.AddSeconds(60), result.Request.ExpiresAt);
        Assert.Null(result.Request.ResolvedAt);
        Assert.Null(result.Request.ConsumedAt);

        var persisted = await LoadRequestAsync(db, result.Request.Id);
        Assert.Equal(AuthorizationStatus.Pending, persisted.Status);
        Assert.Equal(70, persisted.RequestedByUserId);
        Assert.Equal("Cajero 70", persisted.RequestedByName);
        Assert.Equal("Caja-01", persisted.Terminal);
        Assert.Equal(HashA, persisted.ContextHash);
        Assert.Equal(0, await db.AuthorizationAudits.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_UsesInjectedRequestTimeout()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db, requestTimeoutSeconds: 30);

        var result = await service.CreateAsync(Command(userId: 70));

        Assert.Equal(CreateAuthorizationOutcome.Created, result.Outcome);
        Assert.Equal(30, result.Request!.RemainingLifetimeSeconds);
        Assert.Equal(result.Request.CreatedAt.AddSeconds(30), result.Request.ExpiresAt);
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Manager)]
    public async Task CreateAsync_ElevatedRoles_AreRejectedWithExactMessageAndNoPersistence(UserRole role)
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);

        var result = await service.CreateAsync(Command(role: role));

        Assert.Equal(CreateAuthorizationOutcome.ElevationNotRequired, result.Outcome);
        Assert.Equal("Los usuarios con rol de Administrador o Supervisor no requieren autorización.", result.Message);
        Assert.Equal(0, await db.AuthorizationRequests.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_Driver_IsBlockedAndDoesNotPersist()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);

        var result = await service.CreateAsync(Command(role: UserRole.Driver));

        Assert.Equal(CreateAuthorizationOutcome.DriverBlocked, result.Outcome);
        Assert.Equal(0, await db.AuthorizationRequests.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_SaleOwnedByAnotherCashier_IsDenied()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        await SeedUserAsync(db, 70, UserRole.Cashier);
        await SeedUserAsync(db, 71, UserRole.Cashier);
        await SeedSaleAsync(db, 445, cashierId: 71);
        var service = CreateService(db);

        var result = await service.CreateAsync(Command(userId: 70));

        Assert.Equal(CreateAuthorizationOutcome.SaleAccessDenied, result.Outcome);
        Assert.Equal(0, await db.AuthorizationRequests.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_OnHoldSale_IsAccessibleForAnyCashier()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        await SeedUserAsync(db, 70, UserRole.Cashier);
        await SeedUserAsync(db, 71, UserRole.Cashier);
        await SeedSaleAsync(db, 445, cashierId: 71, status: SaleStatus.OnHold);
        var service = CreateService(db);

        var result = await service.CreateAsync(Command(userId: 70));

        Assert.Equal(CreateAuthorizationOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_UnknownSale_IsTolerated_MirroringSaleAccessRule()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);

        var result = await service.CreateAsync(Command(userId: 70, saleId: 9999));

        Assert.Equal(CreateAuthorizationOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_ContextJsonAboveFourKilobytes_IsRejected()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);

        var result = await service.CreateAsync(Command(userId: 70, contextJson: new string('x', FourKilobytes + 1)));

        Assert.Equal(CreateAuthorizationOutcome.ContextTooLarge, result.Outcome);
        Assert.Equal(0, await db.AuthorizationRequests.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_ContextJsonAtFourKilobyteLimit_IsAccepted()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);

        var result = await service.CreateAsync(Command(userId: 70, contextJson: new string('x', FourKilobytes)));

        Assert.Equal(CreateAuthorizationOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_UnregisteredAction_IsRejected()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);

        var result = await service.CreateAsync(Command(userId: 70, action: (AuthorizationActionType)99));

        Assert.Equal(CreateAuthorizationOutcome.UnknownAction, result.Outcome);
        Assert.Equal(0, await db.AuthorizationRequests.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_DuplicatePending_ReturnsExistingWithRemainingLifetime()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var first = await service.CreateAsync(Command(userId: 70));
        await UpdateRequestAsync(db, first.Request!.Id, request => request.ExpiresAt = request.CreatedAt.AddSeconds(30));

        var second = await service.CreateAsync(Command(userId: 70));

        Assert.Equal(CreateAuthorizationOutcome.Deduplicated, second.Outcome);
        Assert.Equal(first.Request.Id, second.Request!.Id);
        Assert.InRange(second.Request.RemainingLifetimeSeconds, 29, 31);
        Assert.Equal(1, await db.AuthorizationRequests.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_DuplicatePendingWithNullSaleId_IsDeduplicatedByService()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var first = await service.CreateAsync(Command(userId: 70, saleId: null));

        var second = await service.CreateAsync(Command(userId: 70, saleId: null));
        var otherAction = await service.CreateAsync(Command(userId: 70, saleId: null, action: AuthorizationActionType.SaleCancellation));

        Assert.Equal(CreateAuthorizationOutcome.Created, first.Outcome);
        Assert.Equal(CreateAuthorizationOutcome.Deduplicated, second.Outcome);
        Assert.Equal(first.Request!.Id, second.Request!.Id);
        Assert.Equal(CreateAuthorizationOutcome.Created, otherAction.Outcome);
        Assert.Equal(2, await db.AuthorizationRequests.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_ExpiredPending_IsReplacedByFreshRequestAndOldIsAuditedAsExpired()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var first = await service.CreateAsync(Command(userId: 70));
        await UpdateRequestAsync(db, first.Request!.Id, request => request.ExpiresAt = DateTime.UtcNow.AddSeconds(-1));

        var second = await service.CreateAsync(Command(userId: 70));

        Assert.Equal(CreateAuthorizationOutcome.Created, second.Outcome);
        Assert.NotEqual(first.Request.Id, second.Request!.Id);
        Assert.Equal(2, await db.AuthorizationRequests.CountAsync());

        var old = await LoadRequestAsync(db, first.Request.Id);
        Assert.Equal(AuthorizationStatus.Expired, old.Status);
        Assert.NotNull(old.ResolvedAt);

        var audit = await db.AuthorizationAudits.AsNoTracking().SingleAsync();
        Assert.Equal(first.Request.Id, audit.RequestId);
        Assert.Equal(AuthorizationStatus.Expired, audit.Status);
        Assert.Null(audit.ResolvedByUserId);
        Assert.Null(audit.ResolvedByName);
        Assert.Null(audit.ResolutionMode);
        Assert.Equal(old.ResolvedAt, audit.ResolvedAt);
    }

    // ---------------------------------------------------------------- resolucion remota

    [Fact]
    public async Task ResolveAsync_Approve_ResolvesRemotelyAndWritesSingleAudit()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));

        var result = await service.ResolveAsync(created.Request!.Id, resolverUserId: 2, resolverName: "Admin Uno", approved: true, reason: "Precio acordado");

        Assert.Equal(ResolveAuthorizationOutcome.Resolved, result.Outcome);
        Assert.Equal(AuthorizationStatus.Approved, result.Request!.Status);
        Assert.Equal(AuthorizationResolutionMode.Remote, result.Request.ResolutionMode);
        Assert.Equal(2, result.Request.ResolvedByUserId);
        Assert.Equal("Admin Uno", result.Request.ResolvedByName);
        Assert.Equal("Precio acordado", result.Request.ResolutionReason);
        Assert.NotNull(result.Request.ResolvedAt);

        var persisted = await LoadRequestAsync(db, created.Request.Id);
        Assert.Equal(AuthorizationStatus.Approved, persisted.Status);
        Assert.Equal(AuthorizationResolutionMode.Remote, persisted.ResolutionMode);
        Assert.Equal(2, persisted.ResolvedByUserId);
        Assert.Equal("Admin Uno", persisted.ResolvedByName);
        Assert.Equal("Precio acordado", persisted.ResolutionReason);

        var audit = await db.AuthorizationAudits.AsNoTracking().SingleAsync();
        Assert.Equal(created.Request.Id, audit.RequestId);
        Assert.Equal(AuthorizationActionType.ManualPriceOverride, audit.ActionType);
        Assert.Equal(445, audit.SaleId);
        Assert.Equal("Caja-01", audit.Terminal);
        Assert.Equal(70, audit.RequestedByUserId);
        Assert.Equal("Cajero 70", audit.RequestedByName);
        Assert.Equal(AuthorizationStatus.Approved, audit.Status);
        Assert.Equal(AuthorizationResolutionMode.Remote, audit.ResolutionMode);
        Assert.Equal(2, audit.ResolvedByUserId);
        Assert.Equal("Admin Uno", audit.ResolvedByName);
        Assert.Equal("Precio acordado", audit.Reason);
        Assert.Equal(created.Request.CreatedAt, audit.RequestedAt);
        Assert.Equal(persisted.ResolvedAt, audit.ResolvedAt);
    }

    [Fact]
    public async Task ResolveAsync_Reject_WithReason_WritesRejectedAudit()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));

        var result = await service.ResolveAsync(created.Request!.Id, resolverUserId: 3, resolverName: "Manager Dos", approved: false, reason: "Monto excesivo");

        Assert.Equal(ResolveAuthorizationOutcome.Resolved, result.Outcome);
        Assert.Equal(AuthorizationStatus.Rejected, result.Request!.Status);
        Assert.Equal(AuthorizationResolutionMode.Remote, result.Request.ResolutionMode);
        Assert.Equal("Monto excesivo", result.Request.ResolutionReason);

        var persisted = await LoadRequestAsync(db, created.Request.Id);
        Assert.Equal(AuthorizationStatus.Rejected, persisted.Status);
        Assert.Equal("Manager Dos", persisted.ResolvedByName);

        var audit = await db.AuthorizationAudits.AsNoTracking().SingleAsync();
        Assert.Equal(AuthorizationStatus.Rejected, audit.Status);
        Assert.Equal("Monto excesivo", audit.Reason);
        Assert.Equal(3, audit.ResolvedByUserId);
    }

    [Fact]
    public async Task ResolveAsync_SecondResolve_LosesRaceWithExactMessageAndUnchangedState()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));

        var first = await service.ResolveAsync(created.Request!.Id, resolverUserId: 2, resolverName: "Admin Uno", approved: true);
        var second = await service.ResolveAsync(created.Request.Id, resolverUserId: 3, resolverName: "Admin Dos", approved: false, reason: "tarde");

        Assert.Equal(ResolveAuthorizationOutcome.Resolved, first.Outcome);
        Assert.Equal(ResolveAuthorizationOutcome.AlreadyResolved, second.Outcome);
        Assert.Equal("Esta solicitud ya fue resuelta por Admin Uno.", second.Message);

        var persisted = await LoadRequestAsync(db, created.Request.Id);
        Assert.Equal(AuthorizationStatus.Approved, persisted.Status);
        Assert.Equal(2, persisted.ResolvedByUserId);
        Assert.Null(persisted.ResolutionReason);
        Assert.Equal(1, await db.AuthorizationAudits.CountAsync());
    }

    [Fact]
    public async Task ResolveAsync_ExpiredPending_LazilyExpiresWithAuditAndExactMessage()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));
        await UpdateRequestAsync(db, created.Request!.Id, request => request.ExpiresAt = DateTime.UtcNow.AddSeconds(-5));

        var result = await service.ResolveAsync(created.Request.Id, resolverUserId: 2, resolverName: "Admin Uno", approved: true);

        Assert.Equal(ResolveAuthorizationOutcome.Expired, result.Outcome);
        Assert.Equal("La solicitud expiró; debe generarse una nueva.", result.Message);

        var persisted = await LoadRequestAsync(db, created.Request.Id);
        Assert.Equal(AuthorizationStatus.Expired, persisted.Status);
        Assert.Null(persisted.ResolutionMode);
        Assert.Null(persisted.ResolvedByUserId);
        Assert.Null(persisted.ResolvedByName);
        Assert.NotNull(persisted.ResolvedAt);

        var audit = await db.AuthorizationAudits.AsNoTracking().SingleAsync();
        Assert.Equal(AuthorizationStatus.Expired, audit.Status);
        Assert.Null(audit.ResolutionMode);
        Assert.Null(audit.ResolvedByUserId);
        Assert.Null(audit.ResolvedByName);
        Assert.Equal(persisted.ResolvedAt, audit.ResolvedAt);
    }

    [Fact]
    public async Task ResolveAsync_AlreadyExpiredRequest_ReturnsExpiredWithoutNewAudit()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));
        await UpdateRequestAsync(db, created.Request!.Id, request =>
        {
            request.Status = AuthorizationStatus.Expired;
            request.ResolvedAt = FixedUtc;
        });

        var result = await service.ResolveAsync(created.Request.Id, resolverUserId: 2, resolverName: "Admin Uno", approved: true);

        Assert.Equal(ResolveAuthorizationOutcome.Expired, result.Outcome);
        Assert.Equal("La solicitud expiró; debe generarse una nueva.", result.Message);
        Assert.Equal(0, await db.AuthorizationAudits.CountAsync());
    }

    [Fact]
    public async Task ResolveAsync_NotFound_ReturnsNotFound()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);

        var result = await service.ResolveAsync(99999, resolverUserId: 2, resolverName: "Admin Uno", approved: true);

        Assert.Equal(ResolveAuthorizationOutcome.NotFound, result.Outcome);
        Assert.Equal(0, await db.AuthorizationAudits.CountAsync());
    }

    // ---------------------------------------------------------------- resolucion local

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Manager)]
    public async Task ResolveLocalAsync_ValidSupervisorCredentials_ResolvesLocalWithAudit(UserRole role)
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        await SeedUserAsync(db, 80, role, password: "SuperClave123!");
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));

        var result = await service.ResolveLocalAsync(created.Request!.Id, "usuario80", "SuperClave123!", reason: "Autorizado en sitio");

        Assert.Equal(LocalResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(80, result.SupervisorUserId);
        Assert.Equal("Usuario 80", result.SupervisorName);
        Assert.Equal(AuthorizationStatus.Approved, result.Request!.Status);
        Assert.Equal(AuthorizationResolutionMode.Local, result.Request.ResolutionMode);
        Assert.Equal(80, result.Request.ResolvedByUserId);

        var persisted = await LoadRequestAsync(db, created.Request.Id);
        Assert.Equal(AuthorizationStatus.Approved, persisted.Status);
        Assert.Equal(AuthorizationResolutionMode.Local, persisted.ResolutionMode);
        Assert.Equal("Autorizado en sitio", persisted.ResolutionReason);

        var audit = await db.AuthorizationAudits.AsNoTracking().SingleAsync();
        Assert.Equal(AuthorizationStatus.Approved, audit.Status);
        Assert.Equal(AuthorizationResolutionMode.Local, audit.ResolutionMode);
        Assert.Equal(80, audit.ResolvedByUserId);
        Assert.Equal("Usuario 80", audit.ResolvedByName);
        Assert.Equal("Autorizado en sitio", audit.Reason);
    }

    [Fact]
    public async Task ResolveLocalAsync_LookupByCedulaWithVPrefix_IsAccepted()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        await SeedUserAsync(db, 89, UserRole.Admin, password: "SuperClave123!");
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));

        var result = await service.ResolveLocalAsync(created.Request!.Id, "V-00000089", "SuperClave123!");

        Assert.Equal(LocalResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(89, result.SupervisorUserId);
    }

    [Fact]
    public async Task ResolveLocalAsync_WrongPassword_FailsClosedWithGenericMessageAndCountsAttempt()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        await SeedUserAsync(db, 80, UserRole.Admin, password: "SuperClave123!");
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));

        var result = await service.ResolveLocalAsync(created.Request!.Id, "usuario80", "ClaveIncorrecta", reason: null);

        Assert.Equal(LocalResolveOutcome.InvalidCredentials, result.Outcome);
        Assert.Equal("Credenciales inválidas o sin privilegios para autorizar.", result.Message);

        var user = await db.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == 80);
        Assert.Equal(1, user.AccessFailedCount);
        Assert.Null(user.LockoutEndUtc);

        var persisted = await LoadRequestAsync(db, created.Request.Id);
        Assert.Equal(AuthorizationStatus.Pending, persisted.Status);
        Assert.Equal(0, await db.AuthorizationAudits.CountAsync());
    }

    [Fact]
    public async Task ResolveLocalAsync_NonElevatedUser_FailsClosedWithSameGenericMessage()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        await SeedUserAsync(db, 81, UserRole.Cashier, password: "ClaveCajero1!");
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));

        var result = await service.ResolveLocalAsync(created.Request!.Id, "usuario81", "ClaveCajero1!");

        Assert.Equal(LocalResolveOutcome.InvalidCredentials, result.Outcome);
        Assert.Equal("Credenciales inválidas o sin privilegios para autorizar.", result.Message);

        var user = await db.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == 81);
        Assert.Equal(0, user.AccessFailedCount);

        var persisted = await LoadRequestAsync(db, created.Request.Id);
        Assert.Equal(AuthorizationStatus.Pending, persisted.Status);
        Assert.Equal(0, await db.AuthorizationAudits.CountAsync());
    }

    [Fact]
    public async Task ResolveLocalAsync_UnknownUser_FailsClosedWithSameGenericMessage()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));

        var result = await service.ResolveLocalAsync(created.Request!.Id, "fantasma", "cualquiera");

        Assert.Equal(LocalResolveOutcome.InvalidCredentials, result.Outcome);
        Assert.Equal("Credenciales inválidas o sin privilegios para autorizar.", result.Message);
        Assert.Equal(0, await db.AuthorizationAudits.CountAsync());
    }

    [Fact]
    public async Task ResolveLocalAsync_LockedUser_ReturnsDistinctLockoutOutcomeWithoutResolving()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        await SeedUserAsync(db, 82, UserRole.Admin, password: "SuperClave123!", accessFailedCount: 5, lockoutEndUtc: DateTime.UtcNow.AddMinutes(5));
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));

        var result = await service.ResolveLocalAsync(created.Request!.Id, "usuario82", "SuperClave123!");

        Assert.Equal(LocalResolveOutcome.LockedOut, result.Outcome);
        Assert.Equal("Credenciales inválidas o sin privilegios para autorizar.", result.Message);

        var user = await db.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == 82);
        Assert.Equal(5, user.AccessFailedCount);
        Assert.NotNull(user.LockoutEndUtc);
        Assert.Equal(AuthorizationStatus.Pending, (await LoadRequestAsync(db, created.Request.Id)).Status);
        Assert.Equal(0, await db.AuthorizationAudits.CountAsync());
    }

    [Fact]
    public async Task ResolveLocalAsync_FiveWrongAttempts_LocksAccountAndThenRejectsEvenCorrectPassword()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        await SeedUserAsync(db, 83, UserRole.Admin, password: "SuperClave123!");
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));

        for (int attempt = 0; attempt < 5; attempt++)
        {
            var failed = await service.ResolveLocalAsync(created.Request!.Id, "usuario83", "ClaveIncorrecta");
            Assert.Equal(LocalResolveOutcome.InvalidCredentials, failed.Outcome);
        }

        var lockedUser = await db.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == 83);
        Assert.Equal(5, lockedUser.AccessFailedCount);
        Assert.NotNull(lockedUser.LockoutEndUtc);
        Assert.True(lockedUser.LockoutEndUtc.Value > DateTime.UtcNow);

        var lockedResult = await service.ResolveLocalAsync(created.Request!.Id, "usuario83", "SuperClave123!");
        Assert.Equal(LocalResolveOutcome.LockedOut, lockedResult.Outcome);
        Assert.Equal("Credenciales inválidas o sin privilegios para autorizar.", lockedResult.Message);
    }

    [Fact]
    public async Task ResolveLocalAsync_ExpiredLockout_IsClearedOnSuccessfulResolution()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        await SeedUserAsync(db, 84, UserRole.Admin, password: "SuperClave123!", accessFailedCount: 5, lockoutEndUtc: DateTime.UtcNow.AddMinutes(-1));
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));

        var result = await service.ResolveLocalAsync(created.Request!.Id, "usuario84", "SuperClave123!");

        Assert.Equal(LocalResolveOutcome.Resolved, result.Outcome);
        var user = await db.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == 84);
        Assert.Equal(0, user.AccessFailedCount);
        Assert.Null(user.LockoutEndUtc);
    }

    [Fact]
    public async Task ResolveLocalAsync_SuccessResetsFailedAttemptCounter()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        await SeedUserAsync(db, 85, UserRole.Admin, password: "SuperClave123!", accessFailedCount: 3);
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));

        var result = await service.ResolveLocalAsync(created.Request!.Id, "usuario85", "SuperClave123!");

        Assert.Equal(LocalResolveOutcome.Resolved, result.Outcome);
        var user = await db.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == 85);
        Assert.Equal(0, user.AccessFailedCount);
        Assert.Null(user.LockoutEndUtc);
    }

    [Fact]
    public async Task ResolveLocalAsync_ExpiredRequest_ReturnsExpiredWithoutResolving()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        await SeedUserAsync(db, 86, UserRole.Admin, password: "SuperClave123!");
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));
        await UpdateRequestAsync(db, created.Request!.Id, request => request.ExpiresAt = DateTime.UtcNow.AddSeconds(-5));

        var result = await service.ResolveLocalAsync(created.Request.Id, "usuario86", "SuperClave123!");

        Assert.Equal(LocalResolveOutcome.Expired, result.Outcome);
        Assert.Equal("La solicitud expiró; debe generarse una nueva.", result.Message);

        var persisted = await LoadRequestAsync(db, created.Request.Id);
        Assert.Equal(AuthorizationStatus.Expired, persisted.Status);
        Assert.Null(persisted.ResolvedByUserId);

        var audit = await db.AuthorizationAudits.AsNoTracking().SingleAsync();
        Assert.Equal(AuthorizationStatus.Expired, audit.Status);
        Assert.Null(audit.ResolvedByUserId);

        var user = await db.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == 86);
        Assert.Equal(0, user.AccessFailedCount);
    }

    [Fact]
    public async Task ResolveLocalAsync_AlreadyResolvedRemotely_ReturnsAlreadyResolvedWithRemoteResolver()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        await SeedUserAsync(db, 87, UserRole.Admin, password: "SuperClave123!");
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));
        await service.ResolveAsync(created.Request!.Id, resolverUserId: 2, resolverName: "Admin Uno", approved: true);

        var result = await service.ResolveLocalAsync(created.Request.Id, "usuario87", "SuperClave123!");

        Assert.Equal(LocalResolveOutcome.AlreadyResolved, result.Outcome);
        Assert.Equal("Esta solicitud ya fue resuelta por Admin Uno.", result.Message);
        Assert.Equal(1, await db.AuthorizationAudits.CountAsync());
    }

    // ---------------------------------------------------------------- expiracion por barrido

    [Fact]
    public async Task ExpireStaleAsync_ExpiresOnlyPastPendingAndWritesOneAuditPerTransition()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var expiringA = await service.CreateAsync(Command(userId: 70, saleId: 445));
        var expiringB = await service.CreateAsync(Command(userId: 70, saleId: 446, action: AuthorizationActionType.SaleCancellation));
        var future = await service.CreateAsync(Command(userId: 70, saleId: 447));
        var approved = await service.CreateAsync(Command(userId: 70, saleId: 448));
        await service.ResolveAsync(approved.Request!.Id, resolverUserId: 2, resolverName: "Admin Uno", approved: true);
        await UpdateRequestAsync(db, expiringA.Request!.Id, request => request.ExpiresAt = DateTime.UtcNow.AddSeconds(-10));
        await UpdateRequestAsync(db, expiringB.Request!.Id, request => request.ExpiresAt = DateTime.UtcNow.AddSeconds(-10));

        var affected = await service.ExpireStaleAsync();

        Assert.Equal(2, affected);
        Assert.Equal(AuthorizationStatus.Expired, (await LoadRequestAsync(db, expiringA.Request.Id)).Status);
        Assert.Equal(AuthorizationStatus.Expired, (await LoadRequestAsync(db, expiringB.Request.Id)).Status);
        Assert.Equal(AuthorizationStatus.Pending, (await LoadRequestAsync(db, future.Request!.Id)).Status);
        Assert.Equal(AuthorizationStatus.Approved, (await LoadRequestAsync(db, approved.Request.Id)).Status);

        Assert.Equal(1, await db.AuthorizationAudits.CountAsync(audit => audit.Status == AuthorizationStatus.Approved));
        var expiryAudits = await db.AuthorizationAudits.AsNoTracking()
            .Where(audit => audit.Status == AuthorizationStatus.Expired)
            .OrderBy(audit => audit.RequestId)
            .ToListAsync();
        Assert.Equal(2, expiryAudits.Count);
        Assert.Equal(expiringA.Request.Id, expiryAudits[0].RequestId);
        Assert.Equal(expiringB.Request.Id, expiryAudits[1].RequestId);
        Assert.All(expiryAudits, audit =>
        {
            Assert.Null(audit.ResolutionMode);
            Assert.Null(audit.ResolvedByUserId);
            Assert.Null(audit.ResolvedByName);
            Assert.NotEqual(default, audit.ResolvedAt);
            Assert.Equal("Cajero 70", audit.RequestedByName);
        });
    }

    [Fact]
    public async Task ExpireStaleAsync_NoStaleRequests_ReturnsZeroWithoutAudits()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        await service.CreateAsync(Command(userId: 70, saleId: 445));

        var affected = await service.ExpireStaleAsync();

        Assert.Equal(0, affected);
        Assert.Equal(0, await db.AuthorizationAudits.CountAsync());
        Assert.Equal(AuthorizationStatus.Pending, (await db.AuthorizationRequests.AsNoTracking().SingleAsync()).Status);
    }

    // ---------------------------------------------------------------- consumo single-use

    private static async Task<(SqliteConnection Connection, SalesDbContext Db, AuthorizationService Service, AuthorizationRequestDto Request)> CreateApprovedAsync(
        int tokenTtlSeconds = 60)
    {
        var (db, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        var service = CreateService(db, tokenTtlSeconds: tokenTtlSeconds);
        var created = await service.CreateAsync(Command(userId: 70));
        await service.ResolveAsync(created.Request!.Id, resolverUserId: 2, resolverName: "Admin Uno", approved: true);
        return (connection, db, service, created.Request);
    }

    [Fact]
    public async Task TryConsumeAsync_ApprovedWithinWindow_ClaimsAtomicallyAndDoesNotAudit()
    {
        var (connection, db, service, request) = await CreateApprovedAsync();
        using (connection)
        using (db)
        {
            var result = await service.TryConsumeAsync(request.Id, 70, AuthorizationActionType.ManualPriceOverride, 445, HashA);

            Assert.Equal(ConsumeAuthorizationOutcome.Consumed, result.Outcome);
            var persisted = await LoadRequestAsync(db, request.Id);
            Assert.NotNull(persisted.ConsumedAt);
            Assert.Equal(0, persisted.ConsumedAt!.Value.Ticks % TimeSpan.TicksPerSecond);
            Assert.Equal(1, await db.AuthorizationAudits.CountAsync());
        }
    }

    [Fact]
    public async Task TryConsumeAsync_SecondUse_IsRejectedAsAlreadyConsumedWithoutMutation()
    {
        var (connection, db, service, request) = await CreateApprovedAsync();
        using (connection)
        using (db)
        {
            var first = await service.TryConsumeAsync(request.Id, 70, AuthorizationActionType.ManualPriceOverride, 445, HashA);
            var consumedAt = (await LoadRequestAsync(db, request.Id)).ConsumedAt;

            var second = await service.TryConsumeAsync(request.Id, 70, AuthorizationActionType.ManualPriceOverride, 445, HashA);

            Assert.Equal(ConsumeAuthorizationOutcome.Consumed, first.Outcome);
            Assert.Equal(ConsumeAuthorizationOutcome.AlreadyConsumed, second.Outcome);
            Assert.Equal(consumedAt, (await LoadRequestAsync(db, request.Id)).ConsumedAt);
        }
    }

    [Fact]
    public async Task TryConsumeAsync_WrongUser_IsRejectedWithoutClaim()
    {
        var (connection, db, service, request) = await CreateApprovedAsync();
        using (connection)
        using (db)
        {
            var result = await service.TryConsumeAsync(request.Id, 71, AuthorizationActionType.ManualPriceOverride, 445, HashA);

            Assert.Equal(ConsumeAuthorizationOutcome.WrongUser, result.Outcome);
            Assert.Null((await LoadRequestAsync(db, request.Id)).ConsumedAt);
        }
    }

    [Fact]
    public async Task TryConsumeAsync_WrongAction_IsRejectedWithoutClaim()
    {
        var (connection, db, service, request) = await CreateApprovedAsync();
        using (connection)
        using (db)
        {
            var result = await service.TryConsumeAsync(request.Id, 70, AuthorizationActionType.SaleCancellation, 445, HashA);

            Assert.Equal(ConsumeAuthorizationOutcome.WrongAction, result.Outcome);
            Assert.Null((await LoadRequestAsync(db, request.Id)).ConsumedAt);
        }
    }

    [Fact]
    public async Task TryConsumeAsync_WrongSale_IsRejectedWithoutClaim()
    {
        var (connection, db, service, request) = await CreateApprovedAsync();
        using (connection)
        using (db)
        {
            var result = await service.TryConsumeAsync(request.Id, 70, AuthorizationActionType.ManualPriceOverride, 999, HashA);

            Assert.Equal(ConsumeAuthorizationOutcome.WrongSale, result.Outcome);
            Assert.Null((await LoadRequestAsync(db, request.Id)).ConsumedAt);
        }
    }

    [Fact]
    public async Task TryConsumeAsync_ContextMismatch_IsRejectedWithoutClaim()
    {
        var (connection, db, service, request) = await CreateApprovedAsync();
        using (connection)
        using (db)
        {
            var result = await service.TryConsumeAsync(request.Id, 70, AuthorizationActionType.ManualPriceOverride, 445, HashB);

            Assert.Equal(ConsumeAuthorizationOutcome.ContextMismatch, result.Outcome);
            Assert.Null((await LoadRequestAsync(db, request.Id)).ConsumedAt);
        }
    }

    [Fact]
    public async Task TryConsumeAsync_AfterTokenWindow_IsRejectedAsTokenExpired()
    {
        var (connection, db, service, request) = await CreateApprovedAsync(tokenTtlSeconds: 5);
        using (connection)
        using (db)
        {
            await UpdateRequestAsync(db, request.Id, row => row.ResolvedAt = DateTime.UtcNow.AddSeconds(-10));

            var result = await service.TryConsumeAsync(request.Id, 70, AuthorizationActionType.ManualPriceOverride, 445, HashA);

            Assert.Equal(ConsumeAuthorizationOutcome.TokenExpired, result.Outcome);
            Assert.Null((await LoadRequestAsync(db, request.Id)).ConsumedAt);
        }
    }

    [Fact]
    public async Task TryConsumeAsync_PendingRequest_IsRejectedAsNotApproved()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));

        var result = await service.TryConsumeAsync(created.Request!.Id, 70, AuthorizationActionType.ManualPriceOverride, 445, HashA);

        Assert.Equal(ConsumeAuthorizationOutcome.NotApproved, result.Outcome);
        Assert.Null((await LoadRequestAsync(db, created.Request.Id)).ConsumedAt);
    }

    [Fact]
    public async Task TryConsumeAsync_NotFound_ReturnsNotFound()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);

        var result = await service.TryConsumeAsync(99999, 70, AuthorizationActionType.ManualPriceOverride, 445, HashA);

        Assert.Equal(ConsumeAuthorizationOutcome.NotFound, result.Outcome);
    }

    // ---------------------------------------------------------------- W1 (8.151): claim bindeado + expiracion perezosa

    /// <summary>
    /// W1 (8.151): ejecuta una mutacion cruda justo antes del primer UPDATE interceptado (claim
    /// de consumo o de expiracion), en la misma conexion y transaccion del UPDATE, para forzar
    /// un interleave determinista entre la validacion y el claim.
    /// </summary>
    private sealed class PreClaimMutationInterceptor : DbCommandInterceptor
    {
        private readonly string _marker;
        private readonly Func<DbCommand, Task> _mutate;
        private bool _enabled;
        private bool _fired;

        public PreClaimMutationInterceptor(string marker, Func<DbCommand, Task> mutate)
        {
            _marker = marker;
            _mutate = mutate;
        }

        public void Enable() => _enabled = true;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_enabled && !_fired && command.CommandText.Contains(_marker, StringComparison.Ordinal))
            {
                _fired = true;
                await _mutate(command);
            }

            return result;
        }
    }

    private static async Task ExecuteOnCommandConnectionAsync(DbCommand command, string sql, int requestId, DateTime? now = null)
    {
        await using var concurrent = command.Connection!.CreateCommand();
        concurrent.Transaction = command.Transaction;
        concurrent.CommandText = sql;
        var idParameter = concurrent.CreateParameter();
        idParameter.ParameterName = "@id";
        idParameter.Value = requestId;
        concurrent.Parameters.Add(idParameter);
        if (now.HasValue)
        {
            var nowParameter = concurrent.CreateParameter();
            nowParameter.ParameterName = "@now";
            nowParameter.Value = now.Value;
            concurrent.Parameters.Add(nowParameter);
        }

        await concurrent.ExecuteNonQueryAsync();
    }

    private static DbContextOptions<SalesDbContext> InterceptedSqliteOptions(
        SqliteConnection connection,
        PreClaimMutationInterceptor interceptor)
        => new DbContextOptionsBuilder<SalesDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

    public enum ConsumeBindingField
    {
        ActionType,
        SaleId,
        ContextHash
    }

    [Theory]
    [InlineData(ConsumeBindingField.ActionType, ConsumeAuthorizationOutcome.WrongAction)]
    [InlineData(ConsumeBindingField.SaleId, ConsumeAuthorizationOutcome.WrongSale)]
    [InlineData(ConsumeBindingField.ContextHash, ConsumeAuthorizationOutcome.ContextMismatch)]
    public async Task TryConsumeAsync_BindingMutatedBetweenValidationAndClaim_IsRejectedWithPreciseOutcome(
        ConsumeBindingField field,
        ConsumeAuthorizationOutcome expected)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var requestId = 0;
        var mutationSql = field switch
        {
            ConsumeBindingField.ActionType => "UPDATE \"AuthorizationRequests\" SET \"ActionType\" = 2 WHERE \"Id\" = @id",
            ConsumeBindingField.SaleId => "UPDATE \"AuthorizationRequests\" SET \"SaleId\" = 999 WHERE \"Id\" = @id",
            _ => $"UPDATE \"AuthorizationRequests\" SET \"ContextHash\" = '{HashB}' WHERE \"Id\" = @id"
        };
        var interceptor = new PreClaimMutationInterceptor(
            "\"ConsumedAt\"",
            command => ExecuteOnCommandConnectionAsync(command, mutationSql, requestId));
        await using var db = new SalesDbContext(InterceptedSqliteOptions(connection, interceptor));
        await db.Database.EnsureCreatedAsync();
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));
        await service.ResolveAsync(created.Request!.Id, resolverUserId: 2, resolverName: "Admin Uno", approved: true);
        requestId = created.Request.Id;

        interceptor.Enable();
        var result = await service.TryConsumeAsync(requestId, 70, AuthorizationActionType.ManualPriceOverride, 445, HashA);

        Assert.Equal(expected, result.Outcome);
        Assert.Null((await LoadRequestAsync(db, requestId)).ConsumedAt);
    }

    [Fact]
    public async Task ResolveAsync_ExpiryClaimLosesToConcurrentApproval_ReReadsAndReportsAlreadyResolved()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var requestId = 0;
        var interceptor = new PreClaimMutationInterceptor(
            "UPDATE \"AuthorizationRequests\"",
            command => ExecuteOnCommandConnectionAsync(
                command,
                "UPDATE \"AuthorizationRequests\" SET \"Status\" = 1, \"ResolutionMode\" = 1, \"ResolvedByUserId\" = 2, \"ResolvedByName\" = 'Admin Uno', \"ResolvedAt\" = @now WHERE \"Id\" = @id",
                requestId,
                DateTime.UtcNow));
        await using var db = new SalesDbContext(InterceptedSqliteOptions(connection, interceptor));
        await db.Database.EnsureCreatedAsync();
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));
        await UpdateRequestAsync(db, created.Request!.Id, request => request.ExpiresAt = DateTime.UtcNow.AddSeconds(-5));
        requestId = created.Request.Id;

        interceptor.Enable();
        var result = await service.ResolveAsync(requestId, resolverUserId: 2, resolverName: "Admin Uno", approved: true);

        Assert.Equal(ResolveAuthorizationOutcome.AlreadyResolved, result.Outcome);
        Assert.Equal("Esta solicitud ya fue resuelta por Admin Uno.", result.Message);
        Assert.Equal(0, await db.AuthorizationAudits.CountAsync());
    }

    [Fact]
    public async Task ResolveLocalAsync_ExpiryClaimLosesToConcurrentApproval_ReReadsAndReportsAlreadyResolved()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var requestId = 0;
        var interceptor = new PreClaimMutationInterceptor(
            "UPDATE \"AuthorizationRequests\"",
            command => ExecuteOnCommandConnectionAsync(
                command,
                "UPDATE \"AuthorizationRequests\" SET \"Status\" = 1, \"ResolutionMode\" = 2, \"ResolvedByUserId\" = 2, \"ResolvedByName\" = 'Admin Uno', \"ResolvedAt\" = @now WHERE \"Id\" = @id",
                requestId,
                DateTime.UtcNow));
        await using var db = new SalesDbContext(InterceptedSqliteOptions(connection, interceptor));
        await db.Database.EnsureCreatedAsync();
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));
        await UpdateRequestAsync(db, created.Request!.Id, request => request.ExpiresAt = DateTime.UtcNow.AddSeconds(-5));
        requestId = created.Request.Id;

        interceptor.Enable();
        var result = await service.ResolveLocalAsync(requestId, "usuario2", "clave-cualquiera");

        Assert.Equal(LocalResolveOutcome.AlreadyResolved, result.Outcome);
        Assert.Equal("Esta solicitud ya fue resuelta por Admin Uno.", result.Message);
        Assert.Equal(0, await db.AuthorizationAudits.CountAsync());
    }

    [Fact]
    public async Task ResolveAsync_ExpiryClaimLosesToConcurrentCancellation_ReReadsAndReportsAlreadyResolved()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var requestId = 0;
        var interceptor = new PreClaimMutationInterceptor(
            "UPDATE \"AuthorizationRequests\"",
            command => ExecuteOnCommandConnectionAsync(
                command,
                "UPDATE \"AuthorizationRequests\" SET \"Status\" = 4, \"ResolvedAt\" = @now WHERE \"Id\" = @id",
                requestId,
                DateTime.UtcNow));
        await using var db = new SalesDbContext(InterceptedSqliteOptions(connection, interceptor));
        await db.Database.EnsureCreatedAsync();
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));
        await UpdateRequestAsync(db, created.Request!.Id, request => request.ExpiresAt = DateTime.UtcNow.AddSeconds(-5));
        requestId = created.Request.Id;

        interceptor.Enable();
        var result = await service.ResolveAsync(requestId, resolverUserId: 2, resolverName: "Admin Uno", approved: true);

        Assert.Equal(ResolveAuthorizationOutcome.AlreadyResolved, result.Outcome);
        Assert.Equal("Esta solicitud ya fue resuelta por otro usuario.", result.Message);
        Assert.Equal(0, await db.AuthorizationAudits.CountAsync());
    }

    [Fact]
    public async Task ResolveLocalAsync_ExpiryClaimLosesToConcurrentCancellation_ReReadsAndReportsAlreadyResolved()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var requestId = 0;
        var interceptor = new PreClaimMutationInterceptor(
            "UPDATE \"AuthorizationRequests\"",
            command => ExecuteOnCommandConnectionAsync(
                command,
                "UPDATE \"AuthorizationRequests\" SET \"Status\" = 4, \"ResolvedAt\" = @now WHERE \"Id\" = @id",
                requestId,
                DateTime.UtcNow));
        await using var db = new SalesDbContext(InterceptedSqliteOptions(connection, interceptor));
        await db.Database.EnsureCreatedAsync();
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));
        await UpdateRequestAsync(db, created.Request!.Id, request => request.ExpiresAt = DateTime.UtcNow.AddSeconds(-5));
        requestId = created.Request.Id;

        interceptor.Enable();
        var result = await service.ResolveLocalAsync(requestId, "usuario2", "clave-cualquiera");

        Assert.Equal(LocalResolveOutcome.AlreadyResolved, result.Outcome);
        Assert.Equal("Esta solicitud ya fue resuelta por otro usuario.", result.Message);
        Assert.Equal(0, await db.AuthorizationAudits.CountAsync());
    }

    // ---------------------------------------------------------------- W1 (8.151): cancelacion del solicitante

    [Fact]
    public async Task CancelAsync_RequesterCancelsPending_TransitionsToCancelledWithSingleAudit()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));

        var result = await service.CancelAsync(created.Request!.Id, requesterUserId: 70);

        Assert.Equal(CancelAuthorizationOutcome.Cancelled, result.Outcome);
        Assert.Equal(AuthorizationStatus.Cancelled, result.Request!.Status);
        Assert.NotNull(result.Request.ResolvedAt);
        Assert.Null(result.Request.ResolutionMode);
        Assert.Null(result.Request.ResolvedByUserId);
        Assert.Null(result.Request.ResolvedByName);

        var persisted = await LoadRequestAsync(db, created.Request.Id);
        Assert.Equal(AuthorizationStatus.Cancelled, persisted.Status);
        Assert.NotNull(persisted.ResolvedAt);
        Assert.Null(persisted.ResolutionMode);
        Assert.Null(persisted.ResolvedByUserId);
        Assert.Null(persisted.ResolvedByName);
        Assert.Null(persisted.ResolutionReason);

        var audit = await db.AuthorizationAudits.AsNoTracking().SingleAsync();
        Assert.Equal(created.Request.Id, audit.RequestId);
        Assert.Equal(AuthorizationStatus.Cancelled, audit.Status);
        Assert.Null(audit.ResolutionMode);
        Assert.Null(audit.ResolvedByUserId);
        Assert.Null(audit.ResolvedByName);
        Assert.Equal(persisted.ResolvedAt, audit.ResolvedAt);
    }

    [Fact]
    public async Task CancelAsync_NonRequester_IsForbiddenWithExactMessageAndNoMutation()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));

        var result = await service.CancelAsync(created.Request!.Id, requesterUserId: 71);

        Assert.Equal(CancelAuthorizationOutcome.Forbidden, result.Outcome);
        Assert.Equal("Solo el solicitante puede cancelar la solicitud.", result.Message);
        Assert.Equal(AuthorizationStatus.Pending, (await LoadRequestAsync(db, created.Request.Id)).Status);
        Assert.Equal(0, await db.AuthorizationAudits.CountAsync());
    }

    [Fact]
    public async Task CancelAsync_AlreadyApproved_ReturnsAlreadyResolvedWithResolverName()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));
        await service.ResolveAsync(created.Request!.Id, resolverUserId: 2, resolverName: "Admin Uno", approved: true);

        var result = await service.CancelAsync(created.Request.Id, requesterUserId: 70);

        Assert.Equal(CancelAuthorizationOutcome.AlreadyResolved, result.Outcome);
        Assert.Equal("Esta solicitud ya fue resuelta por Admin Uno.", result.Message);
        Assert.Equal(AuthorizationStatus.Approved, (await LoadRequestAsync(db, created.Request.Id)).Status);
        Assert.Equal(1, await db.AuthorizationAudits.CountAsync());
    }

    [Fact]
    public async Task CancelAsync_AlreadyExpired_ReturnsExpiredWithExactMessage()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));
        await UpdateRequestAsync(db, created.Request!.Id, request =>
        {
            request.Status = AuthorizationStatus.Expired;
            request.ResolvedAt = FixedUtc;
        });

        var result = await service.CancelAsync(created.Request.Id, requesterUserId: 70);

        Assert.Equal(CancelAuthorizationOutcome.Expired, result.Outcome);
        Assert.Equal("La solicitud expiró; debe generarse una nueva.", result.Message);
        Assert.Equal(AuthorizationStatus.Expired, (await LoadRequestAsync(db, created.Request.Id)).Status);
        Assert.Equal(0, await db.AuthorizationAudits.CountAsync());
    }

    [Fact]
    public async Task CancelAsync_PendingPastLifetime_LazilyExpiresWithAuditAndReportsExpired()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));
        await UpdateRequestAsync(db, created.Request!.Id, request => request.ExpiresAt = DateTime.UtcNow.AddSeconds(-5));

        var result = await service.CancelAsync(created.Request.Id, requesterUserId: 70);

        Assert.Equal(CancelAuthorizationOutcome.Expired, result.Outcome);
        Assert.Equal("La solicitud expiró; debe generarse una nueva.", result.Message);

        var persisted = await LoadRequestAsync(db, created.Request.Id);
        Assert.Equal(AuthorizationStatus.Expired, persisted.Status);
        Assert.NotNull(persisted.ResolvedAt);
        Assert.Null(persisted.ResolutionMode);

        var audit = await db.AuthorizationAudits.AsNoTracking().SingleAsync();
        Assert.Equal(AuthorizationStatus.Expired, audit.Status);
        Assert.Null(audit.ResolvedByUserId);
        Assert.Equal(persisted.ResolvedAt, audit.ResolvedAt);
    }

    [Fact]
    public async Task CancelAsync_SecondCancel_ReturnsAlreadyResolvedWithoutSecondAudit()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));
        await service.CancelAsync(created.Request!.Id, requesterUserId: 70);

        var second = await service.CancelAsync(created.Request.Id, requesterUserId: 70);

        Assert.Equal(CancelAuthorizationOutcome.AlreadyResolved, second.Outcome);
        Assert.Equal("Esta solicitud ya fue resuelta por otro usuario.", second.Message);
        Assert.Equal(1, await db.AuthorizationAudits.CountAsync());
    }

    [Fact]
    public async Task CancelAsync_NotFound_ReturnsNotFound()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);

        var result = await service.CancelAsync(99999, requesterUserId: 70);

        Assert.Equal(CancelAuthorizationOutcome.NotFound, result.Outcome);
        Assert.Equal("La solicitud de autorización no existe.", result.Message);
    }

    // ---------------------------------------------------------------- lectura

    [Fact]
    public async Task GetAsync_RequesterAndElevated_CanRead_UnrelatedNonElevatedCannot()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);
        var created = await service.CreateAsync(Command(userId: 70));

        var asRequester = await service.GetAsync(created.Request!.Id, viewerUserId: 70, viewerIsElevated: false);
        var asOtherCashier = await service.GetAsync(created.Request.Id, viewerUserId: 71, viewerIsElevated: false);
        var asElevated = await service.GetAsync(created.Request.Id, viewerUserId: 999, viewerIsElevated: true);

        Assert.NotNull(asRequester);
        Assert.Equal(created.Request.Id, asRequester!.Id);
        Assert.Equal(AuthorizationStatus.Pending, asRequester.Status);
        Assert.InRange(asRequester.RemainingLifetimeSeconds, 58, 60);
        Assert.Null(asOtherCashier);
        Assert.NotNull(asElevated);
    }

    [Fact]
    public async Task GetAsync_NotFound_ReturnsNull()
    {
        using var scope = new DatabaseScope();
        var db = scope.Db;
        var service = CreateService(db);

        var result = await service.GetAsync(99999, viewerUserId: 70, viewerIsElevated: true);

        Assert.Null(result);
    }

    // ---------------------------------------------------------------- W1: carrera de creacion

    /// <summary>
    /// W1 (8.150): ejecuta un callback antes del primer SaveChangesAsync para forzar el
    /// escenario "otra terminal inserto la Pending entre el pre-chequeo y el INSERT".
    /// </summary>
    private sealed class ConflictInjectingSalesDbContext : SalesDbContext
    {
        private readonly Func<SalesDbContext, CancellationToken, Task> _beforeFirstSave;
        private bool _injected;

        public ConflictInjectingSalesDbContext(
            DbContextOptions<SalesDbContext> options,
            Func<SalesDbContext, CancellationToken, Task> beforeFirstSave)
            : base(options)
        {
            _beforeFirstSave = beforeFirstSave;
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!_injected)
            {
                _injected = true;
                await _beforeFirstSave(this, cancellationToken);
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    private static AuthorizationRequest BuildWinnerRequest(int userId, int? saleId)
    {
        var now = DateTime.UtcNow;
        return new AuthorizationRequest
        {
            ActionType = AuthorizationActionType.ManualPriceOverride,
            SaleId = saleId,
            RequestedByUserId = userId,
            RequestedByName = $"Cajero {userId}",
            Terminal = "Caja-02",
            Status = AuthorizationStatus.Pending,
            ContextJson = """{"productId":10,"quantity":2,"customUnitPriceUsd":9.99}""",
            ContextHash = HashA,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(60)
        };
    }

    [Fact]
    public async Task CreateAsync_ConcurrentDuplicateInsert_TranslatesUniqueViolationToDeduplicated()
    {
        const int userId = 970;
        const int saleId = 9970;
        var databaseName = $"file:auth-dedupe-{Guid.NewGuid():N}?mode=memory&cache=shared";
        using var connection = new SqliteConnection($"Data Source={databaseName}");
        await connection.OpenAsync();

        ConflictInjectingSalesDbContext? raceContext = null;
        raceContext = new ConflictInjectingSalesDbContext(
            new DbContextOptionsBuilder<SalesDbContext>().UseSqlite(connection).Options,
            async (context, cancellationToken) =>
            {
                // La transaccion del perdedor aun no escribio: se revierte para liberar los locks
                // de SQLite y el ganador se comitea desde otra conexion a la misma base compartida.
                var ambient = context.Database.CurrentTransaction;
                if (ambient is not null)
                {
                    await ambient.RollbackAsync(cancellationToken);
                    await ambient.DisposeAsync();
                }

                await using var winnerConnection = new SqliteConnection($"Data Source={databaseName}");
                await winnerConnection.OpenAsync(cancellationToken);
                await using var winnerContext = new SalesDbContext(
                    new DbContextOptionsBuilder<SalesDbContext>().UseSqlite(winnerConnection).Options);
                winnerContext.AuthorizationRequests.Add(BuildWinnerRequest(userId, saleId));
                await winnerContext.SaveChangesAsync(cancellationToken);
            });
        await using var scope = raceContext;
        await raceContext.Database.EnsureCreatedAsync();

        var service = CreateService(raceContext);
        var result = await service.CreateAsync(Command(userId: userId, saleId: saleId));

        Assert.Equal(CreateAuthorizationOutcome.Deduplicated, result.Outcome);
        Assert.NotNull(result.Request);
        Assert.Equal(AuthorizationStatus.Pending, result.Request!.Status);
        Assert.Equal("Caja-02", result.Request.Terminal);
        Assert.InRange(result.Request.RemainingLifetimeSeconds, 58, 61);

        var persisted = await raceContext.AuthorizationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(AuthorizationStatus.Pending, persisted.Status);
        Assert.Equal("Caja-02", persisted.Terminal);
    }

    [Fact]
    public async Task CreateAsync_OtherUniqueViolation_IsNotTranslatedAndPropagates()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var raceContext = new ConflictInjectingSalesDbContext(
            new DbContextOptionsBuilder<SalesDbContext>().UseSqlite(connection).Options,
            (_, _) => throw new DbUpdateException(
                "SQLite Error 19: 'UNIQUE constraint failed: index 'IX_Customers_CedulaOrRif''.",
                new Microsoft.Data.Sqlite.SqliteException(
                    "SQLite Error 19: 'UNIQUE constraint failed: index 'IX_Customers_CedulaOrRif''.",
                    19)));
        raceContext.Database.EnsureCreated();

        var service = CreateService(raceContext);

        await Assert.ThrowsAsync<DbUpdateException>(() => service.CreateAsync(Command(userId: 971)));
        Assert.Equal(0, await raceContext.AuthorizationRequests.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_ConcurrentPostgresCreates_OneCreatedAndOneDeduplicated()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var userId = Random.Shared.Next(800_000, 899_999);
        var saleId = Random.Shared.Next(800_000, 899_999);

        using var firstContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
        using var secondContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
        if (firstContext is null || secondContext is null) return;

        var firstService = CreateService(firstContext);
        var secondService = CreateService(secondContext);

        var results = await Task.WhenAll(
            firstService.CreateAsync(Command(userId: userId, saleId: saleId)),
            secondService.CreateAsync(Command(userId: userId, saleId: saleId)));

        Assert.Equal(1, results.Count(result => result.Outcome == CreateAuthorizationOutcome.Created));
        Assert.Equal(1, results.Count(result => result.Outcome == CreateAuthorizationOutcome.Deduplicated));
        Assert.NotNull(results[0].Request);
        Assert.Equal(results[0].Request!.Id, results[1].Request!.Id);

        using var verificationContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
        if (verificationContext is null) return;
        var pending = await verificationContext.AuthorizationRequests.AsNoTracking()
            .Where(row => row.RequestedByUserId == userId && row.SaleId == saleId && row.Status == AuthorizationStatus.Pending)
            .ToListAsync();
        Assert.Single(pending);
        await verificationContext.AuthorizationRequests
            .Where(row => row.RequestedByUserId == userId)
            .ExecuteDeleteAsync();
    }

    [Fact]
    public async Task CreateAsync_ConcurrentNullSaleCollisionOnPostgres_TranslatesUniqueViolationToDeduplicated()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        const int userId = 86001;
        var databaseName = $"pos_auth_null_sale_{Guid.NewGuid():N}";
        var derivedConnection = await CreateIsolatedPostgresDatabaseAsync(connectionString, databaseName);
        try
        {
            var options = new DbContextOptionsBuilder<SalesDbContext>()
                .UseNpgsql(derivedConnection.ConnectionString)
                .Options;
            await using (var bootstrap = new SalesDbContext(options))
            {
                await bootstrap.Database.MigrateAsync();
            }

            // La terminal perdedora inserta despues de que la ganadora commiteo una Pending con
            // SaleId NULL: el indice NULLS NOT DISTINCT produce el 23505 que el servicio traduce.
            ConflictInjectingSalesDbContext? raceContext = null;
            raceContext = new ConflictInjectingSalesDbContext(options, async (_, cancellationToken) =>
            {
                await using var winnerContext = new SalesDbContext(options);
                winnerContext.AuthorizationRequests.Add(BuildWinnerRequest(userId, saleId: null));
                await winnerContext.SaveChangesAsync(cancellationToken);
            });
            await using var scope = raceContext;
            var service = CreateService(raceContext);

            var result = await service.CreateAsync(Command(userId: userId, saleId: null));

            Assert.Equal(CreateAuthorizationOutcome.Deduplicated, result.Outcome);
            Assert.NotNull(result.Request);
            Assert.Equal("Caja-02", result.Request!.Terminal);

            var persisted = await raceContext.AuthorizationRequests.AsNoTracking().SingleAsync();
            Assert.Equal(AuthorizationStatus.Pending, persisted.Status);
            Assert.Null(persisted.SaleId);
        }
        finally
        {
            await DropIsolatedPostgresDatabaseAsync(connectionString, databaseName);
        }
    }

    private static async Task<NpgsqlConnectionStringBuilder> CreateIsolatedPostgresDatabaseAsync(
        string baseConnectionString,
        string databaseName)
    {
        var maintenance = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "postgres" }.ConnectionString;
        await using (var connection = new NpgsqlConnection(maintenance))
        {
            await connection.OpenAsync();
            await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection))
            {
                await drop.ExecuteNonQueryAsync();
            }

            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = databaseName };
    }

    private static async Task DropIsolatedPostgresDatabaseAsync(string baseConnectionString, string databaseName)
    {
        var maintenance = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "postgres" }.ConnectionString;
        await using var connection = new NpgsqlConnection(maintenance);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }
}
