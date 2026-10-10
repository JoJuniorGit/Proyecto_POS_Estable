using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Hubs;
using Backend.API.Services;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.150 (T3, design D3/D4): orquestacion del coordinador (create/resolve/local/consume/expire).
/// Los tests de orquestacion usan mocks del servicio y del notifier; el flujo end-to-end corre
/// con el AuthorizationService real sobre SQLite, token service real y notifier fake.
/// </summary>
public class AuthorizationCoordinatorTests
{
    private const string TestKey = "POS_Test_Super_Secret_Key_At_Least_32_Chars_Long!";
    private static readonly string HashA = new('a', 64);
    private static readonly DateTime FixedUtc = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private static AuthorizationTokenService CreateTokenService(int ttlSeconds = 60) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "JWT_SETTINGS_KEY", TestKey },
            { "JwtSettings:Issuer", "SolucionesPos" },
            { "JwtSettings:Audience", "PosClient" },
            { "JwtSettings:ExpiryMinutes", "60" }
        }).Build(), TimeSpan.FromSeconds(ttlSeconds));

    private static DateTime UtcNowSeconds()
    {
        var now = DateTime.UtcNow;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
    }

    private static ManualPriceOverrideContext Operation(
        int productId = 10,
        decimal quantity = 2m,
        decimal? usd = 9.99m,
        decimal? local = 1234.5m) => new()
        {
            ProductId = productId,
            Quantity = quantity,
            CustomUnitPriceUsd = usd,
            CustomUnitPriceLocal = local
        };

    private static RequestAuthorizationContract Contract(
        int saleId = 445,
        int productId = 10,
        string? productName = "Cafe molido",
        decimal quantity = 2m,
        decimal? usd = 9.99m,
        decimal? local = 1234.5m,
        string? terminal = "Caja-01") => new()
        {
            SaleId = saleId,
            ProductId = productId,
            ProductName = productName,
            Quantity = quantity,
            CustomUnitPriceUsd = usd,
            CustomUnitPriceLocal = local,
            Terminal = terminal
        };

    private static AuthorizationRequestDto SampleRequest(
        int id = 7,
        AuthorizationStatus status = AuthorizationStatus.Pending,
        AuthorizationResolutionMode? mode = null,
        DateTime? resolvedAt = null,
        string? resolvedByName = null) => new()
        {
            Id = id,
            ActionType = AuthorizationActionType.ManualPriceOverride,
            SaleId = 445,
            RequestedByUserId = 70,
            RequestedByName = "Cajero 70",
            Terminal = "Caja-01",
            Status = status,
            ResolutionMode = mode,
            ResolvedByUserId = resolvedByName is null ? null : 2,
            ResolvedByName = resolvedByName,
            ContextJson = """{"productId":10,"productName":"Cafe molido","quantity":2,"customUnitPriceUsd":9.99,"customUnitPriceLocal":1234.5}""",
            ContextHash = AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(Operation()),
            CreatedAt = FixedUtc,
            ExpiresAt = FixedUtc.AddSeconds(60),
            ResolvedAt = resolvedAt,
            RemainingLifetimeSeconds = status == AuthorizationStatus.Pending ? 60 : 0
        };

    private static CreateAuthorizationRequestDto NewCreateDto(int saleId) => new()
    {
        RequestedByUserId = 70,
        RequestedByName = "Cajero 70",
        RequesterRole = UserRole.Cashier,
        ActionType = AuthorizationActionType.ManualPriceOverride,
        SaleId = saleId,
        ContextJson = """{"productId":10,"quantity":2,"customUnitPriceUsd":9.99}""",
        ContextHash = HashA,
        Terminal = "Caja-01"
    };

    private sealed class RecordingAuthorizationNotifier : IAuthorizationNotifier
    {
        public List<AuthorizationRequestDto> CreatedRequests { get; } = new();
        public List<(AuthorizationRequestDto Request, string? Token)> ResolvedNotifications { get; } = new();
        public List<AuthorizationRequestDto> ExpiredRequests { get; } = new();

        public Task NotifyRequestCreatedAsync(AuthorizationRequestDto request, CancellationToken cancellationToken = default)
        {
            CreatedRequests.Add(request);
            return Task.CompletedTask;
        }

        public Task NotifyResolvedAsync(AuthorizationRequestDto request, string? token, CancellationToken cancellationToken = default)
        {
            ResolvedNotifications.Add((request, token));
            return Task.CompletedTask;
        }

        public Task NotifyExpiredAsync(AuthorizationRequestDto request, CancellationToken cancellationToken = default)
        {
            ExpiredRequests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class SqliteStack : IDisposable
    {
        private readonly SqliteConnection _connection;

        public SalesDbContext Db { get; }
        public AuthorizationService Service { get; }
        public AuthorizationTokenService TokenService { get; }
        public RecordingAuthorizationNotifier Notifier { get; } = new();
        public AuthorizationCoordinator Coordinator { get; }

        public SqliteStack()
        {
            (Db, _connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
            Service = new AuthorizationService(Db, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            TokenService = CreateTokenService();
            Coordinator = new AuthorizationCoordinator(Service, TokenService, Notifier, Db);
        }

        public async Task SeedSaleAsync(int saleId, int? cashierId)
        {
            if (cashierId.HasValue)
            {
                // Sale.CashierId tiene FK a Users: sin la fila, SQLite rechaza la venta.
                Db.Users.Add(new User
                {
                    Id = cashierId.Value,
                    Cedula = $"V-{cashierId.Value:D8}",
                    Name = $"Cajero {cashierId.Value}",
                    Username = $"usuario{cashierId.Value}",
                    FullName = $"Cajero {cashierId.Value}",
                    Role = UserRole.Cashier
                });
            }

            Db.Sales.Add(new Sale { Id = saleId, CashierId = cashierId, Status = SaleStatus.Pending, Date = FixedUtc });
            await Db.SaveChangesAsync();
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    private static async Task BackdateExpiryAsync(SalesDbContext db, int requestId)
    {
        var request = await db.AuthorizationRequests.SingleAsync(candidate => candidate.Id == requestId);
        request.ExpiresAt = DateTime.UtcNow.AddSeconds(-5);
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- canonicalizador

    [Fact]
    public void Canonicalizer_IsDeterministicAndOrderStable()
    {
        var first = AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(Operation());
        var second = AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(Operation());
        var reordered = AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(new ManualPriceOverrideContext
        {
            CustomUnitPriceLocal = 1234.5m,
            CustomUnitPriceUsd = 9.99m,
            Quantity = 2m,
            ProductId = 10
        });

        Assert.Equal(first, second);
        Assert.Equal(first, reordered);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void Canonicalizer_IsCultureInvariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            var invariant = AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(Operation());
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            var commaCulture = AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(Operation());

            Assert.Equal(invariant, commaCulture);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Canonicalizer_NormalizesDecimalScale()
    {
        var plain = AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(Operation(quantity: 2m, usd: 9.99m, local: 1234.5m));
        var padded = AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(Operation(quantity: 2.0m, usd: 9.990m, local: 1234.500m));

        Assert.Equal(plain, padded);
    }

    [Fact]
    public void Canonicalizer_PayloadChanges_AlterHash()
    {
        var baseline = AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(Operation());

        Assert.NotEqual(baseline, AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(Operation(productId: 11)));
        Assert.NotEqual(baseline, AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(Operation(quantity: 3m)));
        Assert.NotEqual(baseline, AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(Operation(usd: 9.98m)));
        Assert.NotEqual(baseline, AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(Operation(local: 1234.6m)));
    }

    [Fact]
    public void BuildPayload_DisplayJsonIncludesTypedFieldsAndBoundedProductName()
    {
        var payload = AuthorizationContextCanonicalizer.BuildManualPriceOverridePayload(Operation(), "Cafe molido");

        Assert.Contains("\"productId\":10", payload.ContextJson);
        Assert.Contains("\"productName\":\"Cafe molido\"", payload.ContextJson);
        Assert.Contains("\"quantity\":2", payload.ContextJson);
        Assert.Contains("\"customUnitPriceUsd\":9.99", payload.ContextJson);
        Assert.Contains("\"customUnitPriceLocal\":1234.5", payload.ContextJson);

        var longName = new string('x', 500);
        var bounded = AuthorizationContextCanonicalizer.BuildManualPriceOverridePayload(Operation(), longName);

        Assert.DoesNotContain(longName, bounded.ContextJson);
        Assert.Contains(new string('x', AuthorizationContextCanonicalizer.MaxProductNameLength), bounded.ContextJson);
    }

    [Fact]
    public void BuildPayload_ProductNameDoesNotAffectHash()
    {
        var first = AuthorizationContextCanonicalizer.BuildManualPriceOverridePayload(Operation(), "Producto A");
        var second = AuthorizationContextCanonicalizer.BuildManualPriceOverridePayload(Operation(), "Producto B");

        Assert.Equal(first.ContextHash, second.ContextHash);
    }

    // ---------------------------------------------------------------- creacion

    [Fact]
    public async Task CreateAsync_NewRequest_BuildsCanonicalContextAndNotifies()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, CreateTokenService(), notifier.Object, db);

        CreateAuthorizationRequestDto? captured = null;
        var created = SampleRequest();
        service.Setup(s => s.CreateAsync(It.IsAny<CreateAuthorizationRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback<CreateAuthorizationRequestDto, CancellationToken>((dto, _) => captured = dto)
            .ReturnsAsync(new CreateAuthorizationResult(CreateAuthorizationOutcome.Created, created));

        var result = await coordinator.CreateAsync(Contract(), 70, "Cajero 70", UserRole.Cashier);

        Assert.Equal(CreateAuthorizationOutcome.Created, result.Outcome);
        Assert.NotNull(captured);
        Assert.Equal(AuthorizationActionType.ManualPriceOverride, captured!.ActionType);
        Assert.Equal(445, captured.SaleId);
        Assert.Equal(70, captured.RequestedByUserId);
        Assert.Equal("Cajero 70", captured.RequestedByName);
        Assert.Equal(UserRole.Cashier, captured.RequesterRole);
        Assert.Equal("Caja-01", captured.Terminal);

        var expectedHash = AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(Operation());
        Assert.Equal(expectedHash, captured.ContextHash);
        Assert.Matches("^[0-9a-f]{64}$", captured.ContextHash);
        Assert.Contains("\"productName\":\"Cafe molido\"", captured.ContextJson);
        Assert.Contains("\"productId\":10", captured.ContextJson);
        Assert.Contains("\"quantity\":2", captured.ContextJson);

        notifier.Verify(n => n.NotifyRequestCreatedAsync(created, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_Deduplicated_ReturnsExistingWithoutNotifying()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, CreateTokenService(), notifier.Object, db);

        service.Setup(s => s.CreateAsync(It.IsAny<CreateAuthorizationRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateAuthorizationResult(CreateAuthorizationOutcome.Deduplicated, SampleRequest()));

        var result = await coordinator.CreateAsync(Contract(), 70, "Cajero 70", UserRole.Cashier);

        Assert.Equal(CreateAuthorizationOutcome.Deduplicated, result.Outcome);
        notifier.Verify(n => n.NotifyRequestCreatedAsync(It.IsAny<AuthorizationRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_ElevatedRejected_PassesThroughWithoutNotifying()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, CreateTokenService(), notifier.Object, db);

        service.Setup(s => s.CreateAsync(It.IsAny<CreateAuthorizationRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateAuthorizationResult(CreateAuthorizationOutcome.ElevationNotRequired, Message: AuthorizationMessages.ElevationNotRequired));

        var result = await coordinator.CreateAsync(Contract(), 2, "Admin Uno", UserRole.Admin);

        Assert.Equal("Los usuarios con rol de Administrador o Supervisor no requieren autorización.", result.Message);
        notifier.Verify(n => n.NotifyRequestCreatedAsync(It.IsAny<AuthorizationRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------- resolucion remota

    [Fact]
    public async Task ResolveAsync_Approved_IssuesTokenAndNotifiesWithToken()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        var tokenService = CreateTokenService();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, tokenService, notifier.Object, db);

        var resolvedAt = UtcNowSeconds();
        var resolved = SampleRequest(status: AuthorizationStatus.Approved, mode: AuthorizationResolutionMode.Remote, resolvedAt: resolvedAt, resolvedByName: "Admin Uno");
        service.Setup(s => s.ResolveAsync(7, 2, "Admin Uno", true, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolveAuthorizationResult(ResolveAuthorizationOutcome.Resolved, resolved));

        AuthorizationRequestDto? notifiedRequest = null;
        string? notifiedToken = null;
        notifier.Setup(n => n.NotifyResolvedAsync(It.IsAny<AuthorizationRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<AuthorizationRequestDto, string?, CancellationToken>((request, token, _) =>
            {
                notifiedRequest = request;
                notifiedToken = token;
            })
            .Returns(Task.CompletedTask);

        var result = await coordinator.ResolveAsync(7, 2, "Admin Uno", approved: true);

        Assert.Equal(ResolveAuthorizationOutcome.Resolved, result.Outcome);
        Assert.Same(resolved, notifiedRequest);
        Assert.NotNull(notifiedToken);

        var claims = tokenService.Validate(notifiedToken);
        Assert.NotNull(claims);
        Assert.Equal(7, claims!.RequestId);
        Assert.Equal(70, claims.CashierUserId);
        Assert.Equal(445, claims.SaleId);
        Assert.Equal(AuthorizationActionType.ManualPriceOverride, claims.ActionType);
        Assert.Equal(resolved.ContextHash, claims.ContextHash);
        Assert.Equal(resolvedAt.AddSeconds(60), claims.ExpiresAt);
    }

    [Fact]
    public async Task ResolveAsync_Rejected_NotifiesWithoutToken()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, CreateTokenService(), notifier.Object, db);

        var resolved = SampleRequest(status: AuthorizationStatus.Rejected, mode: AuthorizationResolutionMode.Remote, resolvedAt: UtcNowSeconds(), resolvedByName: "Admin Uno");
        service.Setup(s => s.ResolveAsync(7, 2, "Admin Uno", false, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolveAuthorizationResult(ResolveAuthorizationOutcome.Resolved, resolved));

        string? notifiedToken = "unset";
        notifier.Setup(n => n.NotifyResolvedAsync(It.IsAny<AuthorizationRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<AuthorizationRequestDto, string?, CancellationToken>((_, token, _) => notifiedToken = token)
            .Returns(Task.CompletedTask);

        var result = await coordinator.ResolveAsync(7, 2, "Admin Uno", approved: false, reason: "Precio fuera de politica");

        Assert.Equal(ResolveAuthorizationOutcome.Resolved, result.Outcome);
        Assert.Null(notifiedToken);
        notifier.Verify(n => n.NotifyResolvedAsync(resolved, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_AlreadyResolved_PassesExactRaceMessageWithoutNotifying()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, CreateTokenService(), notifier.Object, db);

        service.Setup(s => s.ResolveAsync(7, 3, "Admin Dos", true, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolveAuthorizationResult(
                ResolveAuthorizationOutcome.AlreadyResolved,
                SampleRequest(status: AuthorizationStatus.Approved, resolvedAt: UtcNowSeconds(), resolvedByName: "Admin Uno"),
                AuthorizationMessages.AlreadyResolvedBy("Admin Uno")));

        var result = await coordinator.ResolveAsync(7, 3, "Admin Dos", approved: true);

        Assert.Equal(ResolveAuthorizationOutcome.AlreadyResolved, result.Outcome);
        Assert.Equal("Esta solicitud ya fue resuelta por Admin Uno.", result.Message);
        notifier.Verify(n => n.NotifyResolvedAsync(It.IsAny<AuthorizationRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_Expired_PassesExactMessageWithoutNotifying()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, CreateTokenService(), notifier.Object, db);

        service.Setup(s => s.ResolveAsync(7, 2, "Admin Uno", true, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolveAuthorizationResult(ResolveAuthorizationOutcome.Expired, SampleRequest(), AuthorizationMessages.RequestExpired));

        var result = await coordinator.ResolveAsync(7, 2, "Admin Uno", approved: true);

        Assert.Equal(ResolveAuthorizationOutcome.Expired, result.Outcome);
        Assert.Equal("La solicitud expiró; debe generarse una nueva.", result.Message);
        notifier.Verify(n => n.NotifyResolvedAsync(It.IsAny<AuthorizationRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------- resolucion local

    [Fact]
    public async Task ResolveLocalAsync_Resolved_ReturnsTokenAndSupervisorIdentity()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        var tokenService = CreateTokenService();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, tokenService, notifier.Object, db);

        var resolvedAt = UtcNowSeconds();
        var resolved = SampleRequest(status: AuthorizationStatus.Approved, mode: AuthorizationResolutionMode.Local, resolvedAt: resolvedAt, resolvedByName: "Supervisora Ana");
        service.Setup(s => s.ResolveLocalAsync(7, "usuario2", "Clave123!", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalResolveResult(LocalResolveOutcome.Resolved, resolved, 2, "Supervisora Ana"));

        var result = await coordinator.ResolveLocalAsync(7, "usuario2", "Clave123!");

        Assert.Equal(LocalResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(2, result.SupervisorUserId);
        Assert.Equal("Supervisora Ana", result.SupervisorName);
        Assert.NotNull(result.Token);
        notifier.Verify(n => n.NotifyResolvedAsync(resolved, result.Token, It.IsAny<CancellationToken>()), Times.Once);

        var claims = tokenService.Validate(result.Token);
        Assert.NotNull(claims);
        Assert.Equal(7, claims!.RequestId);
        Assert.Equal(70, claims.CashierUserId);
    }

    [Fact]
    public async Task ResolveLocalAsync_InvalidCredentials_ReturnsExactMessageWithoutToken()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, CreateTokenService(), notifier.Object, db);

        service.Setup(s => s.ResolveLocalAsync(7, "usuario2", "mala", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalResolveResult(LocalResolveOutcome.InvalidCredentials, Message: AuthorizationMessages.InvalidCredentials));

        var result = await coordinator.ResolveLocalAsync(7, "usuario2", "mala");

        Assert.Equal(LocalResolveOutcome.InvalidCredentials, result.Outcome);
        Assert.Equal("Credenciales inválidas o sin privilegios para autorizar.", result.Message);
        Assert.Null(result.Token);
        notifier.Verify(n => n.NotifyResolvedAsync(It.IsAny<AuthorizationRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveLocalAsync_EndToEnd_NotifiesRequesterWithIssuedToken()
    {
        using var stack = new SqliteStack();
        stack.Db.Users.Add(new User
        {
            Id = 80,
            Cedula = "V-00000080",
            Name = "Supervisora Ana",
            Username = "usuario80",
            FullName = "Supervisora Ana",
            Role = UserRole.Admin,
            PasswordHash = Core.Security.PasswordHasher.HashPassword("SuperClave123!")
        });
        await stack.Db.SaveChangesAsync();
        var created = await stack.Coordinator.CreateAsync(Contract(), 70, "Cajero 70", UserRole.Cashier);

        var result = await stack.Coordinator.ResolveLocalAsync(created.Request!.Id, "usuario80", "SuperClave123!", reason: "En sitio");

        Assert.Equal(LocalResolveOutcome.Resolved, result.Outcome);
        Assert.NotNull(result.Token);
        var notification = Assert.Single(stack.Notifier.ResolvedNotifications);
        Assert.Equal(created.Request.Id, notification.Request.Id);
        Assert.Equal(result.Token, notification.Token);

        var claims = stack.TokenService.Validate(notification.Token);
        Assert.NotNull(claims);
        Assert.Equal(created.Request.Id, claims!.RequestId);
        Assert.Equal(70, claims.CashierUserId);
        Assert.Equal(AuthorizationStatus.Approved, (await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == created.Request.Id)).Status);
    }

    // ---------------------------------------------------------------- recuperacion de token

    [Fact]
    public async Task GetStatusAsync_ApprovedWithinWindow_ReturnsTokenOnlyForRequester()
    {
        using var stack = new SqliteStack();
        await stack.SeedSaleAsync(445, cashierId: 70);
        var created = await stack.Coordinator.CreateAsync(Contract(), 70, "Cajero 70", UserRole.Cashier);
        await stack.Coordinator.ResolveAsync(created.Request!.Id, 2, "Admin Uno", approved: true);

        var asRequester = await stack.Coordinator.GetStatusAsync(created.Request.Id, viewerUserId: 70, viewerIsElevated: false);
        var asElevated = await stack.Coordinator.GetStatusAsync(created.Request.Id, viewerUserId: 2, viewerIsElevated: true);
        var asOtherCashier = await stack.Coordinator.GetStatusAsync(created.Request.Id, viewerUserId: 71, viewerIsElevated: false);

        Assert.NotNull(asRequester);
        Assert.Equal(AuthorizationStatus.Approved, asRequester!.Request.Status);
        Assert.NotNull(asRequester.Token);
        var claims = stack.TokenService.Validate(asRequester.Token);
        Assert.NotNull(claims);
        Assert.Equal(created.Request.Id, claims!.RequestId);
        Assert.Equal(70, claims.CashierUserId);

        Assert.NotNull(asElevated);
        Assert.Null(asElevated!.Token);
        Assert.Null(asOtherCashier);
    }

    [Fact]
    public async Task GetStatusAsync_RecoveredToken_ReissuesSameWindowWithoutExtension()
    {
        using var stack = new SqliteStack();
        var created = await stack.Coordinator.CreateAsync(Contract(), 70, "Cajero 70", UserRole.Cashier);
        await stack.Coordinator.ResolveAsync(created.Request!.Id, 2, "Admin Uno", approved: true);
        var row = await stack.Db.AuthorizationRequests.SingleAsync(candidate => candidate.Id == created.Request.Id);
        var windowStart = row.ResolvedAt!.Value.AddSeconds(-55);
        row.ResolvedAt = windowStart;
        await stack.Db.SaveChangesAsync();

        var status = await stack.Coordinator.GetStatusAsync(created.Request.Id, 70, false);

        Assert.NotNull(status);
        Assert.NotNull(status!.Token);
        var claims = stack.TokenService.Validate(status.Token);
        Assert.NotNull(claims);
        Assert.Equal(windowStart.AddSeconds(60), claims!.ExpiresAt);
    }

    [Fact]
    public async Task GetStatusAsync_AfterTokenWindow_DoesNotReturnToken()
    {
        using var stack = new SqliteStack();
        var created = await stack.Coordinator.CreateAsync(Contract(), 70, "Cajero 70", UserRole.Cashier);
        await stack.Coordinator.ResolveAsync(created.Request!.Id, 2, "Admin Uno", approved: true);
        var row = await stack.Db.AuthorizationRequests.SingleAsync(candidate => candidate.Id == created.Request.Id);
        row.ResolvedAt = row.ResolvedAt!.Value.AddSeconds(-61);
        await stack.Db.SaveChangesAsync();

        var status = await stack.Coordinator.GetStatusAsync(created.Request.Id, 70, false);

        Assert.NotNull(status);
        Assert.Equal(AuthorizationStatus.Approved, status!.Request.Status);
        Assert.Null(status.Token);
    }

    [Fact]
    public async Task GetStatusAsync_ConsumedApproval_DoesNotReturnToken()
    {
        using var stack = new SqliteStack();
        var created = await stack.Coordinator.CreateAsync(Contract(), 70, "Cajero 70", UserRole.Cashier);
        await stack.Coordinator.ResolveAsync(created.Request!.Id, 2, "Admin Uno", approved: true);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;
        Assert.Equal(AuthorizationConsumeStatus.Consumed, (await stack.Coordinator.ConsumeAsync(token, 445, 70, Operation())).Status);

        var status = await stack.Coordinator.GetStatusAsync(created.Request.Id, 70, false);

        Assert.NotNull(status);
        Assert.NotNull(status!.Request.ConsumedAt);
        Assert.Null(status.Token);
    }

    [Fact]
    public async Task GetStatusAsync_RejectedRequest_DoesNotReturnToken()
    {
        using var stack = new SqliteStack();
        var created = await stack.Coordinator.CreateAsync(Contract(), 70, "Cajero 70", UserRole.Cashier);
        await stack.Coordinator.ResolveAsync(created.Request!.Id, 2, "Admin Uno", approved: false, reason: "No aprobado");

        var status = await stack.Coordinator.GetStatusAsync(created.Request.Id, 70, false);

        Assert.NotNull(status);
        Assert.Equal(AuthorizationStatus.Rejected, status!.Request.Status);
        Assert.Null(status.Token);
    }

    [Fact]
    public async Task GetStatusAsync_NotFound_ReturnsNull()
    {
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(
            new Mock<IAuthorizationService>().Object,
            CreateTokenService(),
            new Mock<IAuthorizationNotifier>().Object,
            db);

        var status = await coordinator.GetStatusAsync(99999, 70, false);

        Assert.Null(status);
    }

    // ---------------------------------------------------------------- consumo

    [Fact]
    public async Task ConsumeAsync_ValidToken_CallsServiceWithClaimsAndComputedPayloadHash()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        var tokenService = CreateTokenService();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, tokenService, notifier.Object, db);

        var operation = Operation();
        var expectedHash = AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(operation);
        var token = tokenService.Issue(70, 7, AuthorizationActionType.ManualPriceOverride, 445, expectedHash, UtcNowSeconds());

        service.Setup(s => s.TryConsumeAsync(7, 70, AuthorizationActionType.ManualPriceOverride, 445, expectedHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsumeAuthorizationResult(ConsumeAuthorizationOutcome.Consumed));

        var result = await coordinator.ConsumeAsync(token, 445, 70, operation);

        Assert.Equal(AuthorizationConsumeStatus.Consumed, result.Status);
        service.Verify(s => s.TryConsumeAsync(7, 70, AuthorizationActionType.ManualPriceOverride, 445, expectedHash, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsumeAsync_DifferentPayload_ComputesRetryHashAndReturnsMismatch()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        var tokenService = CreateTokenService();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, tokenService, notifier.Object, db);

        var approvedHash = AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(Operation());
        var retry = Operation(productId: 11);
        var retryHash = AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(retry);
        Assert.NotEqual(approvedHash, retryHash);

        var token = tokenService.Issue(70, 7, AuthorizationActionType.ManualPriceOverride, 445, approvedHash, UtcNowSeconds());
        service.Setup(s => s.TryConsumeAsync(7, 70, AuthorizationActionType.ManualPriceOverride, 445, retryHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsumeAuthorizationResult(ConsumeAuthorizationOutcome.ContextMismatch));

        var result = await coordinator.ConsumeAsync(token, 445, 70, retry);

        Assert.Equal(AuthorizationConsumeStatus.ContextMismatch, result.Status);
        service.Verify(s => s.TryConsumeAsync(7, 70, AuthorizationActionType.ManualPriceOverride, 445, retryHash, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsumeAsync_InvalidToken_ReturnsInvalidTokenWithoutServiceCall()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, CreateTokenService(), notifier.Object, db);

        var result = await coordinator.ConsumeAsync("not-a-token", 445, 70, Operation());

        Assert.Equal(AuthorizationConsumeStatus.InvalidToken, result.Status);
        service.Verify(s => s.TryConsumeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<AuthorizationActionType>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConsumeAsync_ActualSaleId_IsBoundToTheRequestNotTheClaims()
    {
        // El hash de contexto no cubre la venta: el binding debe usar la venta REAL de la
        // peticion para que un token aprobado para otra venta no pueda consumirse aqui.
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        var tokenService = CreateTokenService();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, tokenService, notifier.Object, db);

        var operation = Operation();
        var expectedHash = AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(operation);
        var token = tokenService.Issue(70, 7, AuthorizationActionType.ManualPriceOverride, 445, expectedHash, UtcNowSeconds());

        service.Setup(s => s.TryConsumeAsync(7, 70, AuthorizationActionType.ManualPriceOverride, 999, expectedHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsumeAuthorizationResult(ConsumeAuthorizationOutcome.WrongSale));

        var result = await coordinator.ConsumeAsync(token, saleId: 999, actingUserId: 70, operation);

        Assert.Equal(AuthorizationConsumeStatus.WrongSale, result.Status);
        service.Verify(s => s.TryConsumeAsync(7, 70, AuthorizationActionType.ManualPriceOverride, 999, expectedHash, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsumeAsync_MissingActingUser_ReturnsWrongUserWithoutServiceCall()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        var tokenService = CreateTokenService();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, tokenService, notifier.Object, db);

        var operation = Operation();
        var hash = AuthorizationContextCanonicalizer.ComputeManualPriceOverrideHash(operation);
        var token = tokenService.Issue(70, 7, AuthorizationActionType.ManualPriceOverride, 445, hash, UtcNowSeconds());

        var result = await coordinator.ConsumeAsync(token, 445, actingUserId: null, operation);

        Assert.Equal(AuthorizationConsumeStatus.WrongUser, result.Status);
        service.Verify(s => s.TryConsumeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<AuthorizationActionType>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------- W1 (8.151): cancelacion

    [Fact]
    public async Task CancelAsync_Cancelled_NotifiesClosureWithoutToken()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, CreateTokenService(), notifier.Object, db);

        var cancelled = SampleRequest(status: AuthorizationStatus.Cancelled, resolvedAt: UtcNowSeconds());
        service.Setup(s => s.CancelAsync(7, 70, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CancelAuthorizationResult(CancelAuthorizationOutcome.Cancelled, cancelled));

        string? notifiedToken = "unset";
        notifier.Setup(n => n.NotifyResolvedAsync(It.IsAny<AuthorizationRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<AuthorizationRequestDto, string?, CancellationToken>((_, token, _) => notifiedToken = token)
            .Returns(Task.CompletedTask);

        var result = await coordinator.CancelAsync(7, 70);

        Assert.Equal(CancelAuthorizationOutcome.Cancelled, result.Outcome);
        Assert.Null(notifiedToken);
        notifier.Verify(n => n.NotifyResolvedAsync(cancelled, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelAsync_Forbidden_PassesExactMessageWithoutNotifying()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, CreateTokenService(), notifier.Object, db);

        service.Setup(s => s.CancelAsync(7, 71, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CancelAuthorizationResult(CancelAuthorizationOutcome.Forbidden, Message: AuthorizationMessages.CancelRequesterOnly));

        var result = await coordinator.CancelAsync(7, 71);

        Assert.Equal(CancelAuthorizationOutcome.Forbidden, result.Outcome);
        Assert.Equal("Solo el solicitante puede cancelar la solicitud.", result.Message);
        notifier.Verify(n => n.NotifyResolvedAsync(It.IsAny<AuthorizationRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelAsync_EndToEnd_PushesCancelledClosureAndPersistsSingleAudit()
    {
        using var stack = new SqliteStack();
        var created = await stack.Coordinator.CreateAsync(Contract(), 70, "Cajero 70", UserRole.Cashier);

        var result = await stack.Coordinator.CancelAsync(created.Request!.Id, 70);

        Assert.Equal(CancelAuthorizationOutcome.Cancelled, result.Outcome);
        var notification = Assert.Single(stack.Notifier.ResolvedNotifications);
        Assert.Equal(created.Request.Id, notification.Request.Id);
        Assert.Equal(AuthorizationStatus.Cancelled, notification.Request.Status);
        Assert.Null(notification.Token);

        var persisted = await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == created.Request.Id);
        Assert.Equal(AuthorizationStatus.Cancelled, persisted.Status);
        Assert.Equal(1, await stack.Db.AuthorizationAudits.CountAsync(row => row.RequestId == created.Request.Id));
    }

    // ---------------------------------------------------------------- expiracion

    [Fact]
    public async Task ExpireStaleAsync_ExpiresAndNotifiesPerExpiredRequest()
    {
        using var stack = new SqliteStack();
        var first = await stack.Service.CreateAsync(NewCreateDto(saleId: 445));
        var second = await stack.Service.CreateAsync(NewCreateDto(saleId: 446));
        var future = await stack.Service.CreateAsync(NewCreateDto(saleId: 447));
        await BackdateExpiryAsync(stack.Db, first.Request!.Id);
        await BackdateExpiryAsync(stack.Db, second.Request!.Id);

        var affected = await stack.Coordinator.ExpireStaleAsync();

        Assert.Equal(2, affected);
        Assert.Equal(2, stack.Notifier.ExpiredRequests.Count);
        Assert.Contains(stack.Notifier.ExpiredRequests, request => request.Id == first.Request.Id);
        Assert.Contains(stack.Notifier.ExpiredRequests, request => request.Id == second.Request.Id);
        Assert.DoesNotContain(stack.Notifier.ExpiredRequests, request => request.Id == future.Request!.Id);

        var statuses = await stack.Db.AuthorizationRequests.AsNoTracking()
            .Where(candidate => candidate.Id == first.Request.Id || candidate.Id == second.Request.Id || candidate.Id == future.Request!.Id)
            .Select(candidate => candidate.Status)
            .ToListAsync();
        Assert.Equal(2, statuses.Count(status => status == AuthorizationStatus.Expired));
        Assert.Single(statuses, status => status == AuthorizationStatus.Pending);
    }

    [Fact]
    public async Task ExpireStaleAsync_NoStaleRequests_ReturnsZeroWithoutNotifications()
    {
        using var stack = new SqliteStack();
        await stack.Service.CreateAsync(NewCreateDto(saleId: 445));

        var affected = await stack.Coordinator.ExpireStaleAsync();

        Assert.Equal(0, affected);
        Assert.Empty(stack.Notifier.ExpiredRequests);
    }

    // ---------------------------------------------------------------- end-to-end

    [Fact]
    public async Task EndToEnd_CreateResolveConsume_ConsumesExactlyOnce()
    {
        using var stack = new SqliteStack();
        await stack.SeedSaleAsync(445, cashierId: 70);

        var created = await stack.Coordinator.CreateAsync(Contract(), 70, "Cajero 70", UserRole.Cashier);
        Assert.Equal(CreateAuthorizationOutcome.Created, created.Outcome);
        Assert.Single(stack.Notifier.CreatedRequests);

        var resolved = await stack.Coordinator.ResolveAsync(created.Request!.Id, 2, "Admin Uno", approved: true);
        Assert.Equal(ResolveAuthorizationOutcome.Resolved, resolved.Outcome);
        var notification = Assert.Single(stack.Notifier.ResolvedNotifications);
        Assert.NotNull(notification.Token);

        var claims = stack.TokenService.Validate(notification.Token);
        Assert.NotNull(claims);
        Assert.Equal(created.Request.Id, claims!.RequestId);
        Assert.Equal(70, claims.CashierUserId);

        var consumed = await stack.Coordinator.ConsumeAsync(notification.Token!, 445, 70, Operation());
        Assert.Equal(AuthorizationConsumeStatus.Consumed, consumed.Status);

        var second = await stack.Coordinator.ConsumeAsync(notification.Token!, 445, 70, Operation());
        Assert.Equal(AuthorizationConsumeStatus.AlreadyConsumed, second.Status);
    }

    [Fact]
    public async Task EndToEnd_ConsumeWithDifferentPayload_IsRejectedAndOriginalPayloadStillWorks()
    {
        using var stack = new SqliteStack();
        await stack.SeedSaleAsync(445, cashierId: 70);

        var created = await stack.Coordinator.CreateAsync(Contract(), 70, "Cajero 70", UserRole.Cashier);
        await stack.Coordinator.ResolveAsync(created.Request!.Id, 2, "Admin Uno", approved: true);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;

        var mismatch = await stack.Coordinator.ConsumeAsync(token, 445, 70, Operation(productId: 11));

        Assert.Equal(AuthorizationConsumeStatus.ContextMismatch, mismatch.Status);

        var consumed = await stack.Coordinator.ConsumeAsync(token, 445, 70, Operation());

        Assert.Equal(AuthorizationConsumeStatus.Consumed, consumed.Status);
    }
}
