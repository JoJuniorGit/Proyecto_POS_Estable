using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Hubs;
using Backend.API.Services;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
/// T2a (Paso 13 / 8.188, S4): autorizacion jerarquica del gasto de caja chica. Cubre el helper
/// puro (umbral por InvariantCulture con fallback 50.00, matriz Cashier/Manager/Admin), el
/// contexto canonico (sesion, moneda, montos bimonetarios, categoria), las ramas create/consume
/// del coordinador con binding exacto del token y el contrato 403 + reintento con token del
/// controlador. El cierre H1 agrega el flujo Manager > umbral: el Manager emite la solicitud,
/// solo un Admin la resuelve (remoto o local) y el consumo registra AuthorizedByUserId del Admin.
/// </summary>
public class PettyCashAuthorizationTests
{
    private const decimal Rate = 36.55m;
    private const string TestKey = "POS_Test_Super_Secret_Key_At_Least_32_Chars_Long!";

    // 8.188-H1: identidades persistidas de la jerarquia Manager > umbral -> Admin.
    private const int AdminUserId = 2;
    private const int ManagerUserId = 3;
    private const string AdminUsername = "usuario2";
    private const string AdminPassword = "clave-admin";
    private const string ManagerUsername = "usuario3";
    private const string ManagerPassword = "clave-manager";

    private static PettyCashExpenseAuthorizationContext Operation(
        int sessionId = 55,
        PettyCashCurrency currency = PettyCashCurrency.BsS,
        decimal amountBsS = 365.50m,
        decimal amountUsd = 10.0000m,
        PettyCashCategory category = PettyCashCategory.InsumosLimpieza) => new()
        {
            SessionId = sessionId,
            Currency = currency,
            AmountBsS = amountBsS,
            AmountUsd = amountUsd,
            Category = category
        };

    private static RequestAuthorizationContract PettyCashContract(
        int sessionId = 55,
        PettyCashCurrency currency = PettyCashCurrency.BsS,
        decimal amountBsS = 365.50m,
        decimal amountUsd = 10.0000m,
        PettyCashCategory category = PettyCashCategory.InsumosLimpieza) => new()
        {
            ActionType = AuthorizationActionType.PettyCashExpense,
            PettyCashSessionId = sessionId,
            PettyCashCurrency = currency,
            PettyCashAmountBsS = amountBsS,
            PettyCashAmountUsd = amountUsd,
            PettyCashCategory = category
        };

    private static AuthorizationTokenService CreateTokenService(int ttlSeconds = 60) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "JWT_SETTINGS_KEY", TestKey },
            { "JwtSettings:Issuer", "SolucionesPos" }
        }).Build(), TimeSpan.FromSeconds(ttlSeconds));

    private sealed class RecordingAuthorizationNotifier : IAuthorizationNotifier
    {
        public List<(AuthorizationRequestDto Request, string? Token)> ResolvedNotifications { get; } = new();

        public Task NotifyRequestCreatedAsync(AuthorizationRequestDto request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyResolvedAsync(AuthorizationRequestDto request, string? token, CancellationToken cancellationToken = default)
        {
            ResolvedNotifications.Add((request, token));
            return Task.CompletedTask;
        }

        public Task NotifyExpiredAsync(AuthorizationRequestDto request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class AuthorizationStack : IDisposable
    {
        private readonly SqliteConnection _connection;

        public SalesDbContext Db { get; }
        public CashDrawerService Drawer { get; }
        public PettyCashService Service { get; }
        public AuthorizationService AuthService { get; }
        public AuthorizationTokenService TokenService { get; }
        public RecordingAuthorizationNotifier Notifier { get; } = new();
        public AuthorizationCoordinator Coordinator { get; }

        public AuthorizationStack(string? threshold = null)
        {
            (Db, _connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
            Drawer = new CashDrawerService(Db);

            // Las FK de auditoria del gasto (creador/autorizador/anulador) exigen las filas de
            // usuario en SQLite: el seed del modelo solo trae al Administrador Id=1. El Manager
            // Id=3 y las credenciales habilitan la jerarquia H1 (Manager > umbral -> Admin) y la
            // resolucion local por credenciales.
            Db.Users.AddRange(
                new User { Id = 2, Cedula = "V-00000002", Name = "Admin Dos", Username = "usuario2", FullName = "Admin Dos", Role = UserRole.Admin, PasswordHash = Core.Security.PasswordHasher.HashPassword(AdminPassword) },
                new User { Id = 3, Cedula = "V-00000003", Name = "Manager Tres", Username = "usuario3", FullName = "Manager Tres", Role = UserRole.Manager, PasswordHash = Core.Security.PasswordHasher.HashPassword(ManagerPassword) },
                new User { Id = 70, Cedula = "V-00000070", Name = "Cajero 70", Username = "usuario70", FullName = "Cajero 70", Role = UserRole.Cashier },
                new User { Id = 71, Cedula = "V-00000071", Name = "Cajero 71", Username = "usuario71", FullName = "Cajero 71", Role = UserRole.Cashier });
            Db.SaveChanges();

            var inventory = new Mock<IInventoryService>();
            inventory.Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Rate);
            var settings = new Mock<ISystemSettingsService>();
            settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync(threshold);

            Service = new PettyCashService(Db, Drawer, inventory.Object, settings.Object);
            AuthService = new AuthorizationService(Db, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            TokenService = CreateTokenService();
            Coordinator = new AuthorizationCoordinator(AuthService, TokenService, Notifier, Db);
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    private static async Task SeedPaymentMethodsAsync(SalesDbContext db, bool withUsdCash = true)
    {
        var localCash = await db.PaymentMethods.FirstAsync(method => method.Id == 1);
        localCash.Name = "Efectivo Bs.S";
        localCash.IsCash = true;
        localCash.IsActive = true;
        localCash.IsDeleted = false;

        if (withUsdCash)
        {
            db.PaymentMethods.Add(new PaymentMethod { Id = 11, Name = "Efectivo USD", IsCash = true, IsActive = true, DisplayOrder = 4 });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<CashDrawerSession> SeedOpenSessionAsync(SalesDbContext db, decimal openingLocal = 1000m, decimal openingUsd = 0m)
    {
        var session = new CashDrawerSession
        {
            OpenedAt = DateTime.UtcNow,
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = openingLocal,
            OpeningExchangeRate = Rate,
            OpeningBalanceUsd = openingUsd
        };
        db.CashDrawerSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private static PettyCashExpenseRequest Request(
        PettyCashCurrency currency = PettyCashCurrency.BsS,
        decimal amount = 365.50m,
        PettyCashCategory category = PettyCashCategory.InsumosLimpieza) => new()
        {
            Currency = currency,
            Amount = amount,
            Category = category,
            Concept = "Compra de jabon y cloro",
            Beneficiary = "Limpieza Total C.A.",
            ReferenceNumber = "FACT-0001"
        };

    private static async Task<AuthorizationRequest> SeedConsumedGrantAsync(
        SalesDbContext db,
        int requesterUserId = 70,
        int resolvedByUserId = 2,
        AuthorizationActionType actionType = AuthorizationActionType.PettyCashExpense,
        int? requestedByUserIdOverride = null)
    {
        var now = DateTime.UtcNow;
        var grant = new AuthorizationRequest
        {
            ActionType = actionType,
            SaleId = null,
            RequestedByUserId = requestedByUserIdOverride ?? requesterUserId,
            RequestedByName = "Solicitante",
            Status = AuthorizationStatus.Approved,
            ResolutionMode = AuthorizationResolutionMode.Remote,
            ResolvedByUserId = resolvedByUserId,
            ResolvedByName = "Admin Uno",
            ContextJson = "{}",
            ContextHash = new string('a', 64),
            CreatedAt = now.AddSeconds(-10),
            ExpiresAt = now.AddSeconds(50),
            ResolvedAt = now.AddSeconds(-5),
            ConsumedAt = now.AddSeconds(-1)
        };
        db.AuthorizationRequests.Add(grant);
        await db.SaveChangesAsync();
        return grant;
    }

    private static PettyCashController CreateController(AuthorizationStack stack, string userId = "70", UserRole role = UserRole.Cashier)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(service => service.UserId).Returns(userId);
        currentUser.SetupGet(service => service.UserRole).Returns(role);

        var controller = new PettyCashController(stack.Service, currentUser.Object, stack.Coordinator)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        // Contrato endurecido: el registro de caja chica exige Idempotency-Key.
        controller.ControllerContext.HttpContext.Request.Headers["Idempotency-Key"] =
            $"test-petty-{Guid.NewGuid():N}";
        return controller;
    }

    // ------------------------------------------------------------------ helper puro (umbral)

    [Theory]
    [InlineData(null, 50.00)]
    [InlineData("", 50.00)]
    [InlineData("no-es-numero", 50.00)]
    [InlineData("-5", 50.00)]
    [InlineData("0", 0.00)]
    [InlineData("50.00", 50.00)]
    [InlineData("75.5", 75.5)]
    public void ResolveThresholdUsd_ParsesInvariantWithSafeFallback(string? rawValue, decimal expected)
    {
        Assert.Equal(expected, PettyCashAuthorizationPolicy.ResolveThresholdUsd(rawValue));
    }

    [Theory]
    [InlineData(UserRole.Cashier, 1.00, true)]
    [InlineData(UserRole.Cashier, 5000.00, true)]
    [InlineData(UserRole.Manager, 50.00, false)]
    [InlineData(UserRole.Manager, 50.01, true)]
    [InlineData(UserRole.Admin, 5000.00, false)]
    [InlineData(null, 1.00, true)]
    public void RequiresAuthorization_Matrix_WithDefaultThreshold(UserRole? actingRole, decimal amountUsd, bool expected)
    {
        Assert.Equal(expected, PettyCashAuthorizationPolicy.RequiresAuthorization(
            amountUsd, actingRole, PettyCashAuthorizationPolicy.DefaultThresholdUsd));
    }

    [Fact]
    public void RequiresAuthorization_WithZeroThreshold_ManagerNeedsAdminForAnyPositiveAmount()
    {
        Assert.True(PettyCashAuthorizationPolicy.RequiresAuthorization(0.01m, UserRole.Manager, 0m));
        Assert.False(PettyCashAuthorizationPolicy.RequiresAuthorization(0m, UserRole.Manager, 0m));
    }

    [Theory]
    [InlineData(UserRole.Admin, false)]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Cashier, true)]
    [InlineData(null, true)]
    public void RequiresCancellationAuthorization_OnlyAdminIsDirect(UserRole? actingRole, bool expected)
    {
        Assert.Equal(expected, PettyCashAuthorizationPolicy.RequiresCancellationAuthorization(actingRole));
    }

    // ------------------------------------------------------------------ contexto canonico

    [Fact]
    public void Canonicalizer_PettyCashHash_IsDeterministicAndPayloadSensitive()
    {
        var baseline = AuthorizationContextCanonicalizer.ComputePettyCashExpenseHash(Operation());

        Assert.Matches("^[0-9a-f]{64}$", baseline);
        Assert.Equal(baseline, AuthorizationContextCanonicalizer.ComputePettyCashExpenseHash(Operation()));
        Assert.Equal(baseline, AuthorizationContextCanonicalizer.ComputePettyCashExpenseHash(new PettyCashExpenseAuthorizationContext
        {
            AmountUsd = 10.0000m,
            AmountBsS = 365.50m,
            Category = PettyCashCategory.InsumosLimpieza,
            Currency = PettyCashCurrency.BsS,
            SessionId = 55
        }));

        Assert.NotEqual(baseline, AuthorizationContextCanonicalizer.ComputePettyCashExpenseHash(Operation(sessionId: 56)));
        Assert.NotEqual(baseline, AuthorizationContextCanonicalizer.ComputePettyCashExpenseHash(Operation(currency: PettyCashCurrency.Usd)));
        Assert.NotEqual(baseline, AuthorizationContextCanonicalizer.ComputePettyCashExpenseHash(Operation(amountBsS: 365.51m)));
        Assert.NotEqual(baseline, AuthorizationContextCanonicalizer.ComputePettyCashExpenseHash(Operation(amountUsd: 10.0001m)));
        Assert.NotEqual(baseline, AuthorizationContextCanonicalizer.ComputePettyCashExpenseHash(Operation(category: PettyCashCategory.Varios)));
    }

    [Fact]
    public void BuildPettyCashExpensePayload_ExposesExactContextFields()
    {
        var payload = AuthorizationContextCanonicalizer.BuildPettyCashExpensePayload(Operation());
        var expectedHash = AuthorizationContextCanonicalizer.ComputePettyCashExpenseHash(Operation());

        Assert.Equal(expectedHash, payload.ContextHash);
        Assert.Contains("\"sessionId\":55", payload.ContextJson);
        Assert.Contains("\"currency\":0", payload.ContextJson);
        Assert.Contains("\"amountBsS\":365.50", payload.ContextJson);
        Assert.Contains("\"amountUsd\":10.0000", payload.ContextJson);
        Assert.Contains("\"category\":0", payload.ContextJson);
    }

    [Fact]
    public void Canonicalizer_PettyCashHash_IsCultureInvariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            var invariant = AuthorizationContextCanonicalizer.ComputePettyCashExpenseHash(Operation());
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(invariant, AuthorizationContextCanonicalizer.ComputePettyCashExpenseHash(Operation()));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ------------------------------------------------------------------ contrato y rama create

    [Fact]
    public void RequestAuthorizationContract_PettyCashExpense_RequiresFullContext()
    {
        var contract = PettyCashContract();
        Assert.Empty(contract.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(contract)));

        var incomplete = new RequestAuthorizationContract { ActionType = AuthorizationActionType.PettyCashExpense };
        var errors = incomplete.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(incomplete)).ToList();

        Assert.Equal(5, errors.Count);
    }

    [Fact]
    public async Task CreateAsync_PettyCashExpense_BuildsCanonicalContextWithoutSaleBinding()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, CreateTokenService(), notifier.Object, db);

        CreateAuthorizationRequestDto? captured = null;
        service.Setup(s => s.CreateAsync(It.IsAny<CreateAuthorizationRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback<CreateAuthorizationRequestDto, CancellationToken>((dto, _) => captured = dto)
            .ReturnsAsync(new CreateAuthorizationResult(
                CreateAuthorizationOutcome.Created,
                new AuthorizationRequestDto
                {
                    Id = 9,
                    ActionType = AuthorizationActionType.PettyCashExpense,
                    RequestedByUserId = 70,
                    RequestedByName = "Cajero 70",
                    Status = AuthorizationStatus.Pending,
                    ContextHash = AuthorizationContextCanonicalizer.ComputePettyCashExpenseHash(Operation()),
                    ContextJson = "{}",
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddSeconds(60),
                    RemainingLifetimeSeconds = 60
                }));

        var result = await coordinator.CreateAsync(PettyCashContract(), 70, "Cajero 70", UserRole.Cashier);

        Assert.Equal(CreateAuthorizationOutcome.Created, result.Outcome);
        Assert.NotNull(captured);
        Assert.Equal(AuthorizationActionType.PettyCashExpense, captured!.ActionType);
        Assert.Null(captured.SaleId);
        Assert.Equal(70, captured.RequestedByUserId);
        Assert.Equal(UserRole.Cashier, captured.RequesterRole);
        Assert.Equal(AuthorizationContextCanonicalizer.ComputePettyCashExpenseHash(Operation()), captured.ContextHash);
        Assert.Contains("\"sessionId\":55", captured.ContextJson);
        notifier.Verify(n => n.NotifyRequestCreatedAsync(It.IsAny<AuthorizationRequestDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_PettyCashExpense_MissingContext_IsRejectedWithoutPersisting()
    {
        var service = new Mock<IAuthorizationService>();
        var notifier = new Mock<IAuthorizationNotifier>();
        using var db = TestDatabaseFactory.CreateSalesDbContext();
        var coordinator = new AuthorizationCoordinator(service.Object, CreateTokenService(), notifier.Object, db);

        var incomplete = new RequestAuthorizationContract { ActionType = AuthorizationActionType.PettyCashExpense };
        var result = await coordinator.CreateAsync(incomplete, 70, "Cajero 70", UserRole.Cashier);

        Assert.Equal(CreateAuthorizationOutcome.UnknownAction, result.Outcome);
        service.Verify(s => s.CreateAsync(It.IsAny<CreateAuthorizationRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ------------------------------------------------------------------ binding exacto del token

    [Fact]
    public async Task ConsumePettyCashExpense_EndToEnd_ConsumesExactlyOnceWithExactContext()
    {
        using var stack = new AuthorizationStack();

        var created = await stack.Coordinator.CreateAsync(PettyCashContract(), 70, "Cajero 70", UserRole.Cashier);
        Assert.Equal(CreateAuthorizationOutcome.Created, created.Outcome);
        Assert.True((await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync()).SaleId is null);

        await stack.Coordinator.ResolveAsync(created.Request!.Id, 2, "Admin Uno", approved: true);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;

        var consumed = await stack.Coordinator.ConsumePettyCashExpenseAsync(token, 70, Operation());

        Assert.Equal(AuthorizationConsumeStatus.Consumed, consumed.Status);
        Assert.Equal(created.Request.Id, consumed.RequestId);
        Assert.Equal(2, consumed.AuthorizedByUserId);

        var second = await stack.Coordinator.ConsumePettyCashExpenseAsync(token, 70, Operation());
        Assert.Equal(AuthorizationConsumeStatus.AlreadyConsumed, second.Status);
    }

    [Fact]
    public async Task ConsumePettyCashExpense_WrongContext_IsRejectedAndOriginalStillWorks()
    {
        using var stack = new AuthorizationStack();

        var created = await stack.Coordinator.CreateAsync(PettyCashContract(), 70, "Cajero 70", UserRole.Cashier);
        await stack.Coordinator.ResolveAsync(created.Request!.Id, 2, "Admin Uno", approved: true);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;

        var mismatch = await stack.Coordinator.ConsumePettyCashExpenseAsync(token, 70, Operation(amountBsS: 999.99m));
        Assert.Equal(AuthorizationConsumeStatus.ContextMismatch, mismatch.Status);

        var wrongUser = await stack.Coordinator.ConsumePettyCashExpenseAsync(token, 71, Operation());
        Assert.Equal(AuthorizationConsumeStatus.WrongUser, wrongUser.Status);

        var consumed = await stack.Coordinator.ConsumePettyCashExpenseAsync(token, 70, Operation());
        Assert.Equal(AuthorizationConsumeStatus.Consumed, consumed.Status);
    }

    [Fact]
    public async Task ConsumePettyCashExpense_TokenOfAnotherAction_IsRejectedAsWrongAction()
    {
        using var stack = new AuthorizationStack();

        var manualPrice = new RequestAuthorizationContract
        {
            SaleId = 445,
            ProductId = 10,
            ProductName = "Cafe molido",
            Quantity = 2m,
            CustomUnitPriceUsd = 9.99m
        };
        var created = await stack.Coordinator.CreateAsync(manualPrice, 70, "Cajero 70", UserRole.Cashier);
        await stack.Coordinator.ResolveAsync(created.Request!.Id, 2, "Admin Uno", approved: true);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;

        var result = await stack.Coordinator.ConsumePettyCashExpenseAsync(token, 70, Operation());

        Assert.Equal(AuthorizationConsumeStatus.WrongAction, result.Status);
    }

    [Fact]
    public async Task ConsumePettyCashExpense_InvalidTokenOrMissingUser_IsRejected()
    {
        using var stack = new AuthorizationStack();

        Assert.Equal(
            AuthorizationConsumeStatus.InvalidToken,
            (await stack.Coordinator.ConsumePettyCashExpenseAsync("not-a-token", 70, Operation())).Status);

        var created = await stack.Coordinator.CreateAsync(PettyCashContract(), 70, "Cajero 70", UserRole.Cashier);
        await stack.Coordinator.ResolveAsync(created.Request!.Id, 2, "Admin Uno", approved: true);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;

        Assert.Equal(
            AuthorizationConsumeStatus.WrongUser,
            (await stack.Coordinator.ConsumePettyCashExpenseAsync(token, null, Operation())).Status);
    }

    // ------------------------------------------------------------------ señales del servicio

    [Fact]
    public async Task RegisterAsync_CashierWithoutAuthorization_ThrowsSignalWithExactContext()
    {
        using var stack = new AuthorizationStack();
        await SeedPaymentMethodsAsync(stack.Db);
        var session = await SeedOpenSessionAsync(stack.Db);

        var signal = await Assert.ThrowsAsync<PettyCashAuthorizationRequiredException>(() => stack.Service.RegisterAsync(
            Request(), actingUserId: 70, actingRole: UserRole.Cashier));

        Assert.Equal(session.Id, signal.SessionId);
        Assert.Equal((int)PettyCashCurrency.BsS, signal.Currency);
        Assert.Equal(365.50m, signal.AmountBsS);
        Assert.Equal(10.0000m, signal.AmountUsd);
        Assert.Equal((int)PettyCashCategory.InsumosLimpieza, signal.Category);
        Assert.Empty(await stack.Db.PettyCashExpenses.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RegisterAsync_ManagerAboveThreshold_ThrowsSignalAndWithinThresholdIsDirect()
    {
        using var stack = new AuthorizationStack();
        await SeedPaymentMethodsAsync(stack.Db);
        await SeedOpenSessionAsync(stack.Db, openingLocal: 1000m, openingUsd: 100m);

        // Umbral implicito 50.00: 60 USD supera y exige autorizacion de Admin.
        await Assert.ThrowsAsync<PettyCashAuthorizationRequiredException>(() => stack.Service.RegisterAsync(
            Request(currency: PettyCashCurrency.Usd, amount: 60m), actingUserId: 2, actingRole: UserRole.Manager));

        var direct = await stack.Service.RegisterAsync(
            Request(currency: PettyCashCurrency.Usd, amount: 50m), actingUserId: 2, actingRole: UserRole.Manager);

        // Quick win 8.188: registro directo del Manager dentro del umbral sin supervisor distinto.
        Assert.Null(direct.AuthorizedByUserId);
        Assert.Null(direct.AuthorizationRequestId);
    }

    [Fact]
    public async Task RegisterAsync_CashierWithConsumedGrant_PersistsAuthorizerAndRequest()
    {
        using var stack = new AuthorizationStack();
        await SeedPaymentMethodsAsync(stack.Db);
        await SeedOpenSessionAsync(stack.Db);
        var grant = await SeedConsumedGrantAsync(stack.Db);

        var result = await stack.Service.RegisterAsync(
            Request(),
            actingUserId: 70,
            actingRole: UserRole.Cashier,
            authorizedByUserId: 2,
            authorizationRequestId: grant.Id);

        Assert.Equal(2, result.AuthorizedByUserId);
        Assert.Equal(grant.Id, result.AuthorizationRequestId);
        Assert.Equal(70, result.CreatedByUserId);
    }

    [Fact]
    public async Task RegisterAsync_CashierWithForeignOrUnconsumedGrant_FailsClosed()
    {
        using var stack = new AuthorizationStack();
        await SeedPaymentMethodsAsync(stack.Db);
        await SeedOpenSessionAsync(stack.Db);

        // La solicitud pertenece a otro usuario: jamas habilita a este cajero.
        var foreign = await SeedConsumedGrantAsync(stack.Db, requestedByUserIdOverride: 71);
        await Assert.ThrowsAsync<PettyCashAuthorizationRequiredException>(() => stack.Service.RegisterAsync(
            Request(), actingUserId: 70, actingRole: UserRole.Cashier, authorizedByUserId: 2, authorizationRequestId: foreign.Id));

        // Solicitud aprobada pero aun no consumida por el controlador: tambien falla cerrado.
        var unconsumed = await SeedConsumedGrantAsync(stack.Db);
        var row = await stack.Db.AuthorizationRequests.SingleAsync(candidate => candidate.Id == unconsumed.Id);
        row.ConsumedAt = null;
        await stack.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<PettyCashAuthorizationRequiredException>(() => stack.Service.RegisterAsync(
            Request(), actingUserId: 70, actingRole: UserRole.Cashier, authorizedByUserId: 2, authorizationRequestId: unconsumed.Id));
    }

    [Fact]
    public async Task CancelAsync_CashierWithoutAuthorization_ThrowsSignalAndWithGrantSucceeds()
    {
        using var stack = new AuthorizationStack();
        await SeedPaymentMethodsAsync(stack.Db);
        await SeedOpenSessionAsync(stack.Db);

        var expense = await stack.Service.RegisterAsync(Request(amount: 100m), actingUserId: 1, actingRole: UserRole.Admin);

        var signal = await Assert.ThrowsAsync<PettyCashAuthorizationRequiredException>(() => stack.Service.CancelAsync(
            expense.Id, "Anulacion solicitada por caja", actingUserId: 70, actingRole: UserRole.Cashier));
        Assert.Equal(100m, signal.AmountBsS);
        Assert.Equal((int)PettyCashCurrency.BsS, signal.Currency);

        var cancelled = await stack.Service.CancelAsync(
            expense.Id, "Anulacion autorizada por supervisor", actingUserId: 70, actingRole: UserRole.Cashier, authorizedByUserId: 2);

        Assert.Equal(PettyCashStatus.Cancelled, cancelled.Status);
        Assert.Equal(70, cancelled.CancelledByUserId);
    }

    [Fact]
    public async Task CancelAsync_ManagerRequiresAuthorization_AdminIsDirect()
    {
        using var stack = new AuthorizationStack();
        await SeedPaymentMethodsAsync(stack.Db);
        await SeedOpenSessionAsync(stack.Db);

        var managerExpense = await stack.Service.RegisterAsync(Request(amount: 10m), actingUserId: 2, actingRole: UserRole.Manager);

        await Assert.ThrowsAsync<PettyCashAuthorizationRequiredException>(() => stack.Service.CancelAsync(
            managerExpense.Id, "Gerente intenta anular", actingUserId: 2, actingRole: UserRole.Manager));

        var adminExpense = await stack.Service.RegisterAsync(Request(amount: 20m), actingUserId: 1, actingRole: UserRole.Admin);
        var cancelled = await stack.Service.CancelAsync(adminExpense.Id, "Anulacion directa de admin", actingUserId: 1, actingRole: UserRole.Admin);
        Assert.Equal(PettyCashStatus.Cancelled, cancelled.Status);
    }

    // ------------------------------------------------------------------ contrato 403 + reintento con token

    [Fact]
    public async Task Register_WhenAuthorizationRequired_ReturnsForbiddenContract()
    {
        using var stack = new AuthorizationStack();
        await SeedPaymentMethodsAsync(stack.Db);
        var session = await SeedOpenSessionAsync(stack.Db);
        var controller = CreateController(stack);

        var response = await controller.RegisterAsync(Request(), CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(forbidden.Value);
        Assert.True((bool)problem.Extensions["authorizationRequired"]!);
        Assert.Equal("PettyCashExpense", problem.Extensions["authorizationAction"]);
        var context = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(problem.Extensions["authorizationContext"]);
        Assert.Equal(session.Id, context["sessionId"]);
        Assert.Equal((int)PettyCashCurrency.BsS, context["currency"]);
        Assert.Equal(365.50m, context["amountBss"]);
        Assert.Equal(10.0000m, context["amountUsd"]);
        Assert.Equal((int)PettyCashCategory.InsumosLimpieza, context["category"]);
        Assert.Empty(await stack.Db.PettyCashExpenses.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Register_WithApprovedToken_ConsumesAndRegistersWithAuthorizerBinding()
    {
        using var stack = new AuthorizationStack();
        await SeedPaymentMethodsAsync(stack.Db);
        var session = await SeedOpenSessionAsync(stack.Db);
        var controller = CreateController(stack);

        var created = await stack.Coordinator.CreateAsync(PettyCashContract(sessionId: session.Id), 70, "Cajero 70", UserRole.Cashier);
        await stack.Coordinator.ResolveAsync(created.Request!.Id, 2, "Admin Uno", approved: true);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;

        controller.ControllerContext.HttpContext.Request.Headers["X-Authorization-Token"] = token;

        var response = await controller.RegisterAsync(Request(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<PettyCashExpenseResultDto>(ok.Value);
        Assert.Equal(2, result.AuthorizedByUserId);
        Assert.Equal(created.Request.Id, result.AuthorizationRequestId);

        Assert.Equal(1, await stack.Db.PettyCashExpenses.AsNoTracking().CountAsync());
        var grant = await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync(candidate => candidate.Id == created.Request.Id);
        Assert.NotNull(grant.ConsumedAt);
    }

    [Fact]
    public async Task Cancel_WhenAuthorizationRequired_ReturnsForbiddenContract()
    {
        using var stack = new AuthorizationStack();
        await SeedPaymentMethodsAsync(stack.Db);
        await SeedOpenSessionAsync(stack.Db);
        var expense = await stack.Service.RegisterAsync(Request(amount: 100m), actingUserId: 1, actingRole: UserRole.Admin);
        var controller = CreateController(stack, userId: "70", role: UserRole.Cashier);

        var response = await controller.CancelAsync(expense.Id, new PettyCashCancelRequest { Reason = "Anulacion de caja" }, CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(forbidden.Value);
        Assert.Equal("PettyCashExpense", problem.Extensions["authorizationAction"]);
        var context = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(problem.Extensions["authorizationContext"]);
        Assert.Equal(expense.SessionId, context["sessionId"]);
        Assert.Equal(100m, context["amountBss"]);
    }

    [Fact]
    public async Task Cancel_WithApprovedToken_ConsumesAndCancels()
    {
        using var stack = new AuthorizationStack();
        await SeedPaymentMethodsAsync(stack.Db);
        var session = await SeedOpenSessionAsync(stack.Db);
        var expense = await stack.Service.RegisterAsync(Request(amount: 100m), actingUserId: 1, actingRole: UserRole.Admin);
        var controller = CreateController(stack, userId: "70", role: UserRole.Cashier);

        var created = await stack.Coordinator.CreateAsync(PettyCashContract(
            sessionId: session.Id,
            amountBsS: 100m,
            amountUsd: expense.AmountUsd), 70, "Cajero 70", UserRole.Cashier);
        await stack.Coordinator.ResolveAsync(created.Request!.Id, 2, "Admin Uno", approved: true);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;
        controller.ControllerContext.HttpContext.Request.Headers["X-Authorization-Token"] = token;

        var response = await controller.CancelAsync(expense.Id, new PettyCashCancelRequest { Reason = "Autorizado por supervisor" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<PettyCashExpenseResultDto>(ok.Value);
        Assert.Equal(PettyCashStatus.Cancelled, result.Status);
        Assert.Equal(1, await stack.Db.CashTransactions.AsNoTracking()
            .CountAsync(candidate => candidate.Source == CashTransactionSource.PettyCashReversal));
    }

    [Fact]
    public async Task GetSessionExpenses_RequiresSessionAndReturnsTurnList()
    {
        using var stack = new AuthorizationStack();
        await SeedPaymentMethodsAsync(stack.Db);
        var session = await SeedOpenSessionAsync(stack.Db);
        await stack.Service.RegisterAsync(Request(amount: 10m), actingUserId: 1, actingRole: UserRole.Admin);
        var controller = CreateController(stack, userId: "1", role: UserRole.Admin);

        var invalid = await controller.GetSessionExpensesAsync(0, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(invalid.Result);

        var response = await controller.GetSessionExpensesAsync(session.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var expenses = Assert.IsAssignableFrom<IReadOnlyList<PettyCashExpenseResultDto>>(ok.Value);
        Assert.Single(expenses);
    }

    // ------------------------------------------------------------------ H1 (8.188): Manager > umbral -> Admin

    [Fact]
    public async Task CreateAsync_ManagerPettyCashExpense_IsPendingInsteadOfElevationNotRequired()
    {
        using var stack = new AuthorizationStack();

        var result = await stack.Coordinator.CreateAsync(PettyCashContract(), ManagerUserId, "Manager Tres", UserRole.Manager);

        // Antes del cierre H1 el Manager cortaba en ElevationNotRequired y la espera degradaba a
        // Failed: la solicitud debe nacer Pending para que un Admin pueda aprobarla.
        Assert.Equal(CreateAuthorizationOutcome.Created, result.Outcome);
        Assert.NotNull(result.Request);
        Assert.Equal(AuthorizationStatus.Pending, result.Request!.Status);

        var persisted = await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(AuthorizationStatus.Pending, persisted.Status);
        Assert.Equal(ManagerUserId, persisted.RequestedByUserId);
        Assert.Equal(AuthorizationActionType.PettyCashExpense, persisted.ActionType);
    }

    [Fact]
    public async Task CreateAsync_AdminPettyCashExpense_StillElevationNotRequired()
    {
        using var stack = new AuthorizationStack();

        var result = await stack.AuthService.CreateAsync(new CreateAuthorizationRequestDto
        {
            RequestedByUserId = AdminUserId,
            RequestedByName = "Admin Dos",
            RequesterRole = UserRole.Admin,
            ActionType = AuthorizationActionType.PettyCashExpense,
            SaleId = null,
            ContextJson = "{}",
            ContextHash = new string('a', 64)
        });

        // El Admin se auto-autoriza en el servicio de caja chica: sigue sin requerir solicitud.
        Assert.Equal(CreateAuthorizationOutcome.ElevationNotRequired, result.Outcome);
        Assert.Empty(await stack.Db.AuthorizationRequests.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_DriverPettyCashExpense_IsBlocked()
    {
        using var stack = new AuthorizationStack();

        var result = await stack.AuthService.CreateAsync(new CreateAuthorizationRequestDto
        {
            RequestedByUserId = 90,
            RequestedByName = "Conductor 90",
            RequesterRole = UserRole.Driver,
            ActionType = AuthorizationActionType.PettyCashExpense,
            SaleId = null,
            ContextJson = "{}",
            ContextHash = new string('a', 64)
        });

        Assert.Equal(CreateAuthorizationOutcome.DriverBlocked, result.Outcome);
        Assert.Empty(await stack.Db.AuthorizationRequests.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(AuthorizationActionType.ManualPriceOverride)]
    [InlineData(AuthorizationActionType.CreditOverLimit)]
    public async Task CreateAsync_HistoricalActions_KeepElevationNotRequiredForManager(AuthorizationActionType actionType)
    {
        using var stack = new AuthorizationStack();

        var result = await stack.AuthService.CreateAsync(new CreateAuthorizationRequestDto
        {
            RequestedByUserId = ManagerUserId,
            RequestedByName = "Manager Tres",
            RequesterRole = UserRole.Manager,
            ActionType = actionType,
            SaleId = 445,
            ContextJson = """{"productId":10,"quantity":2,"customUnitPriceUsd":9.99}""",
            ContextHash = new string('a', 64)
        });

        // La excepcion de la elevacion es SOLO para caja chica: precio manual y sobregiro de
        // credito conservan la semantica historica de los roles elevados.
        Assert.Equal(CreateAuthorizationOutcome.ElevationNotRequired, result.Outcome);
        Assert.Empty(await stack.Db.AuthorizationRequests.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ResolveAsync_PettyCashRequestedByManager_ManagerIsRefusedAndAdminResolves()
    {
        using var stack = new AuthorizationStack();
        var created = await stack.Coordinator.CreateAsync(PettyCashContract(), ManagerUserId, "Manager Tres", UserRole.Manager);
        Assert.Equal(CreateAuthorizationOutcome.Created, created.Outcome);

        var refused = await stack.Coordinator.ResolveAsync(created.Request!.Id, ManagerUserId, "Manager Tres", approved: true);

        Assert.NotEqual(ResolveAuthorizationOutcome.Resolved, refused.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(refused.Message));

        // Falla cerrado: la solicitud sigue Pending y sin resolver, disponible para un Admin.
        var stillPending = await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(AuthorizationStatus.Pending, stillPending.Status);
        Assert.Null(stillPending.ResolvedByUserId);
        Assert.Empty(stack.Notifier.ResolvedNotifications);

        var approved = await stack.Coordinator.ResolveAsync(created.Request.Id, AdminUserId, "Admin Dos", approved: true);

        Assert.Equal(ResolveAuthorizationOutcome.Resolved, approved.Outcome);
        Assert.Equal(AuthorizationStatus.Approved, approved.Request!.Status);
        Assert.Equal(AdminUserId, approved.Request.ResolvedByUserId);
        Assert.Equal(AdminUserId, stack.Notifier.ResolvedNotifications.Single().Request.ResolvedByUserId);
    }

    [Fact]
    public async Task ResolveAsync_PettyCashRequestedByCashier_ManagerAndAdminCanResolve()
    {
        using var stack = new AuthorizationStack();

        // Cajero -> Manager: comportamiento historico intacto.
        var byManager = await stack.Coordinator.CreateAsync(PettyCashContract(), 70, "Cajero 70", UserRole.Cashier);
        var managerResult = await stack.Coordinator.ResolveAsync(byManager.Request!.Id, ManagerUserId, "Manager Tres", approved: true);
        Assert.Equal(ResolveAuthorizationOutcome.Resolved, managerResult.Outcome);
        Assert.Equal(ManagerUserId, managerResult.Request!.ResolvedByUserId);

        // Cajero -> Admin: tambien valido.
        var byAdmin = await stack.Coordinator.CreateAsync(PettyCashContract(sessionId: 56), 71, "Cajero 71", UserRole.Cashier);
        var adminResult = await stack.Coordinator.ResolveAsync(byAdmin.Request!.Id, AdminUserId, "Admin Dos", approved: true);
        Assert.Equal(ResolveAuthorizationOutcome.Resolved, adminResult.Outcome);
        Assert.Equal(AdminUserId, adminResult.Request!.ResolvedByUserId);
    }

    [Fact]
    public async Task ResolveLocalAsync_PettyCashRequestedByManager_RequiresAdminCredentials()
    {
        using var stack = new AuthorizationStack();
        var created = await stack.Coordinator.CreateAsync(PettyCashContract(), ManagerUserId, "Manager Tres", UserRole.Manager);

        // El Manager no puede autorizar su propia solicitud ni por credenciales locales: las
        // credenciales eran validas, por lo que tampoco se registra un intento fallido.
        var refused = await stack.Coordinator.ResolveLocalAsync(created.Request!.Id, ManagerUsername, ManagerPassword, reason: null);

        Assert.Equal(LocalResolveOutcome.InvalidCredentials, refused.Outcome);
        Assert.Null(refused.Token);
        var manager = await stack.Db.Users.AsNoTracking().SingleAsync(user => user.Id == ManagerUserId);
        Assert.Equal(0, manager.AccessFailedCount);
        Assert.Equal(AuthorizationStatus.Pending, (await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync()).Status);

        var approved = await stack.Coordinator.ResolveLocalAsync(created.Request.Id, AdminUsername, AdminPassword, reason: null);

        Assert.Equal(LocalResolveOutcome.Resolved, approved.Outcome);
        Assert.NotNull(approved.Token);
        Assert.Equal(AdminUserId, approved.SupervisorUserId);
    }

    [Fact]
    public async Task ResolveLocalAsync_CashierRequestedPettyCash_ManagerCredentialsStillResolve()
    {
        using var stack = new AuthorizationStack();
        var created = await stack.Coordinator.CreateAsync(PettyCashContract(), 70, "Cajero 70", UserRole.Cashier);

        var approved = await stack.Coordinator.ResolveLocalAsync(created.Request!.Id, ManagerUsername, ManagerPassword, reason: null);

        Assert.Equal(LocalResolveOutcome.Resolved, approved.Outcome);
        Assert.NotNull(approved.Token);
        Assert.Equal(ManagerUserId, approved.SupervisorUserId);
    }

    [Fact]
    public async Task ResolveAsync_NonElevatedResolverOnPettyCash_FailsClosedWithoutClaim()
    {
        using var stack = new AuthorizationStack();
        var created = await stack.Coordinator.CreateAsync(PettyCashContract(), 70, "Cajero 70", UserRole.Cashier);

        // La puerta elevada vive en el hub/controlador; el servicio tambien falla cerrado si un
        // resolutor no elevado llega directo (rol persistido, no el claim del token).
        var refused = await stack.AuthService.ResolveAsync(created.Request!.Id, 71, "Cajero 71", approved: true);

        Assert.NotEqual(ResolveAuthorizationOutcome.Resolved, refused.Outcome);
        var stillPending = await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(AuthorizationStatus.Pending, stillPending.Status);
        Assert.Null(stillPending.ResolvedByUserId);

        // El Manager si puede resolver la solicitud del cajero.
        var approved = await stack.Coordinator.ResolveAsync(created.Request.Id, ManagerUserId, "Manager Tres", approved: true);
        Assert.Equal(ResolveAuthorizationOutcome.Resolved, approved.Outcome);
    }

    [Fact]
    public async Task ResolveAsync_PettyCashWithUnknownRequester_RequiresAdmin()
    {
        using var stack = new AuthorizationStack();

        // Solicitante sin fila en Users: el rol no puede derivarse, la regla falla cerrado y solo
        // un Admin resuelve.
        var created = await stack.AuthService.CreateAsync(new CreateAuthorizationRequestDto
        {
            RequestedByUserId = 99,
            RequestedByName = "Usuario 99",
            RequesterRole = UserRole.Cashier,
            ActionType = AuthorizationActionType.PettyCashExpense,
            SaleId = null,
            ContextJson = "{}",
            ContextHash = new string('b', 64)
        });
        Assert.Equal(CreateAuthorizationOutcome.Created, created.Outcome);

        var refused = await stack.Coordinator.ResolveAsync(created.Request!.Id, ManagerUserId, "Manager Tres", approved: true);
        Assert.NotEqual(ResolveAuthorizationOutcome.Resolved, refused.Outcome);

        var approved = await stack.Coordinator.ResolveAsync(created.Request.Id, AdminUserId, "Admin Dos", approved: true);
        Assert.Equal(ResolveAuthorizationOutcome.Resolved, approved.Outcome);
    }

    [Fact]
    public async Task Register_ManagerAboveThreshold_AdminResolvesAndRegistersWithAdminAuthorizer()
    {
        using var stack = new AuthorizationStack();
        await SeedPaymentMethodsAsync(stack.Db);
        var session = await SeedOpenSessionAsync(stack.Db, openingLocal: 1000m, openingUsd: 100m);
        var controller = CreateController(stack, userId: ManagerUserId.ToString(CultureInfo.InvariantCulture), role: UserRole.Manager);

        // 1) Manager > umbral (60 USD > 50.00): el registro directo exige autorizacion, con el
        // contexto exacto de la solicitud.
        var forbidden = await controller.RegisterAsync(Request(currency: PettyCashCurrency.Usd, amount: 60m), CancellationToken.None);
        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(forbidden.Result).Value);
        Assert.Equal("PettyCashExpense", problem.Extensions["authorizationAction"]);
        var context = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(problem.Extensions["authorizationContext"]);

        // 2) El Manager emite la solicitud: antes del cierre H1 cortaba en ElevationNotRequired.
        var created = await stack.Coordinator.CreateAsync(
            PettyCashContract(
                sessionId: session.Id,
                currency: PettyCashCurrency.Usd,
                amountBsS: (decimal)context["amountBss"]!,
                amountUsd: (decimal)context["amountUsd"]!),
            ManagerUserId,
            "Manager Tres",
            UserRole.Manager);
        Assert.Equal(CreateAuthorizationOutcome.Created, created.Outcome);

        // 3) El Manager no puede aprobar la suya; el Admin si.
        var refused = await stack.Coordinator.ResolveAsync(created.Request!.Id, ManagerUserId, "Manager Tres", approved: true);
        Assert.NotEqual(ResolveAuthorizationOutcome.Resolved, refused.Outcome);
        await stack.Coordinator.ResolveAsync(created.Request.Id, AdminUserId, "Admin Dos", approved: true);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;

        // 4) El Manager reintenta con el token: consume, registra y el gasto queda autorizado por
        // el Admin (AuthorizedByUserId = Admin), no por si mismo.
        controller.ControllerContext.HttpContext.Request.Headers["X-Authorization-Token"] = token;
        var response = await controller.RegisterAsync(Request(currency: PettyCashCurrency.Usd, amount: 60m), CancellationToken.None);

        var result = Assert.IsType<PettyCashExpenseResultDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(AdminUserId, result.AuthorizedByUserId);
        Assert.Equal(created.Request.Id, result.AuthorizationRequestId);
        Assert.Equal(ManagerUserId, result.CreatedByUserId);
        Assert.Equal(1, await stack.Db.PettyCashExpenses.AsNoTracking().CountAsync());
        Assert.NotNull((await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync()).ConsumedAt);
    }

    [Fact]
    public async Task Cancel_ManagerAbovePolicy_AdminResolvesAndCancelsWithReversal()
    {
        using var stack = new AuthorizationStack();
        await SeedPaymentMethodsAsync(stack.Db);
        var session = await SeedOpenSessionAsync(stack.Db);
        var expense = await stack.Service.RegisterAsync(Request(amount: 100m), actingUserId: 1, actingRole: UserRole.Admin);
        var controller = CreateController(stack, userId: ManagerUserId.ToString(CultureInfo.InvariantCulture), role: UserRole.Manager);

        var forbidden = await controller.CancelAsync(expense.Id, new PettyCashCancelRequest { Reason = "Anulacion solicitada por gerencia" }, CancellationToken.None);
        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(forbidden.Result).Value);
        Assert.Equal("PettyCashExpense", problem.Extensions["authorizationAction"]);
        var context = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(problem.Extensions["authorizationContext"]);

        var created = await stack.Coordinator.CreateAsync(
            PettyCashContract(
                sessionId: session.Id,
                amountBsS: (decimal)context["amountBss"]!,
                amountUsd: (decimal)context["amountUsd"]!),
            ManagerUserId,
            "Manager Tres",
            UserRole.Manager);
        Assert.Equal(CreateAuthorizationOutcome.Created, created.Outcome);

        var refused = await stack.Coordinator.ResolveAsync(created.Request!.Id, ManagerUserId, "Manager Tres", approved: true);
        Assert.NotEqual(ResolveAuthorizationOutcome.Resolved, refused.Outcome);

        await stack.Coordinator.ResolveAsync(created.Request.Id, AdminUserId, "Admin Dos", approved: true);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;
        controller.ControllerContext.HttpContext.Request.Headers["X-Authorization-Token"] = token;

        var response = await controller.CancelAsync(expense.Id, new PettyCashCancelRequest { Reason = "Anulado con autorizacion de Admin" }, CancellationToken.None);

        var result = Assert.IsType<PettyCashExpenseResultDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(PettyCashStatus.Cancelled, result.Status);
        Assert.Equal(ManagerUserId, result.CancelledByUserId);
        Assert.Equal(1, await stack.Db.CashTransactions.AsNoTracking()
            .CountAsync(candidate => candidate.Source == CashTransactionSource.PettyCashReversal));
    }
}
