using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Hubs;
using Backend.API.Services;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.150 (T4, design D4): hub de autorizaciones. Los grupos se fijan con mocks del Hub y el
/// flujo create/resolve corre con clientes SignalR reales sobre TestServer (LongPolling) contra
/// el coordinador real y SQLite; la precedencia de mocks del notifier sigue a
/// PaymentMethodSignalRIntegrationTests.
/// </summary>
public class AuthorizationHubIntegrationTests
{
    private static readonly DateTime FixedUtc = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private const string TestKey = "POS_Test_Super_Secret_Key_At_Least_32_Chars_Long!";

    // ---------------------------------------------------------------- grupos del hub

    private static Mock<HubCallerContext> CreateCallerContext(int userId, UserRole role, string name)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.Role, role.ToString())
        }, "TestAuth");

        var context = new Mock<HubCallerContext>();
        context.SetupGet(caller => caller.ConnectionId).Returns($"conn-{userId}");
        context.SetupGet(caller => caller.User).Returns(new ClaimsPrincipal(identity));
        return context;
    }

    private static AuthorizationHub CreateHub(Mock<IGroupManager> groups, HubCallerContext context)
    {
        var coordinator = new Mock<IAuthorizationCoordinator>();
        return new AuthorizationHub(coordinator.Object)
        {
            Context = context,
            Groups = groups.Object,
            Clients = new Mock<IHubCallerClients>().Object
        };
    }

    [Fact]
    public async Task OnConnectedAsync_Admin_JoinsUserAndElevatedGroups()
    {
        var groups = new Mock<IGroupManager>();
        groups.Setup(manager => manager.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var hub = CreateHub(groups, CreateCallerContext(2, UserRole.Admin, "Admin Dos").Object);

        await hub.OnConnectedAsync();

        groups.Verify(manager => manager.AddToGroupAsync("conn-2", "user:2", It.IsAny<CancellationToken>()), Times.Once);
        groups.Verify(manager => manager.AddToGroupAsync("conn-2", "role:elevated", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnConnectedAsync_Manager_JoinsElevatedGroup()
    {
        var groups = new Mock<IGroupManager>();
        groups.Setup(manager => manager.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var hub = CreateHub(groups, CreateCallerContext(3, UserRole.Manager, "Manager Tres").Object);

        await hub.OnConnectedAsync();

        groups.Verify(manager => manager.AddToGroupAsync("conn-3", "user:3", It.IsAny<CancellationToken>()), Times.Once);
        groups.Verify(manager => manager.AddToGroupAsync("conn-3", "role:elevated", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnConnectedAsync_Cashier_JoinsOnlyUserGroup()
    {
        var groups = new Mock<IGroupManager>();
        groups.Setup(manager => manager.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var hub = CreateHub(groups, CreateCallerContext(70, UserRole.Cashier, "Cajero 70").Object);

        await hub.OnConnectedAsync();

        groups.Verify(manager => manager.AddToGroupAsync("conn-70", "user:70", It.IsAny<CancellationToken>()), Times.Once);
        groups.Verify(manager => manager.AddToGroupAsync(It.IsAny<string>(), "role:elevated", It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------- flujo con clientes reales

    [Fact]
    public async Task HubFlow_RequestAndRemoteApproval_PushesPayloadsAndTokenToRequesterOnly()
    {
        await using var app = await HubTestApplication.CreateAsync();
        app.Db.Users.Add(new User
        {
            Id = 70,
            Cedula = "V-00000070",
            Name = "Cajero 70",
            Username = "usuario70",
            FullName = "Cajero 70",
            Role = UserRole.Cashier
        });
        app.Db.Sales.Add(new Sale { Id = 445, CashierId = 70, Status = SaleStatus.Pending, Date = FixedUtc });
        await app.Db.SaveChangesAsync();

        await using var cashier = app.CreateConnection(70, UserRole.Cashier, "Cajero 70");
        await using var admin = app.CreateConnection(2, UserRole.Admin, "Admin Dos");
        await cashier.StartAsync();
        await admin.StartAsync();

        var requested = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        admin.On<JsonElement>("AuthorizationRequested", payload => requested.TrySetResult(payload));

        var createResult = await cashier.InvokeAsync<JsonElement>("RequestAuthorization", new RequestAuthorizationContract
        {
            SaleId = 445,
            ProductId = 10,
            ProductName = "Cafe molido",
            Quantity = 2m,
            CustomUnitPriceUsd = 9.99m,
            CustomUnitPriceLocal = 1234.5m,
            Terminal = "Caja-01"
        });

        Assert.True(createResult.GetProperty("success").GetBoolean());
        var requestId = createResult.GetProperty("requestId").GetInt32();
        Assert.Equal("Pending", createResult.GetProperty("status").GetString());
        Assert.False(createResult.GetProperty("deduplicated").GetBoolean());
        Assert.InRange(createResult.GetProperty("remainingLifetimeSeconds").GetInt32(), 58, 60);

        var requestedPayload = await requested.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(requestId, requestedPayload.GetProperty("requestId").GetInt32());
        Assert.Equal("ManualPriceOverride", requestedPayload.GetProperty("actionType").GetString());
        Assert.Equal(445, requestedPayload.GetProperty("saleId").GetInt32());
        Assert.Equal("Cajero 70", requestedPayload.GetProperty("requestedByName").GetString());
        Assert.Equal("Caja-01", requestedPayload.GetProperty("terminal").GetString());
        Assert.Equal("Cafe molido", requestedPayload.GetProperty("context").GetProperty("productName").GetString());
        Assert.True(requestedPayload.TryGetProperty("createdAt", out _));
        Assert.True(requestedPayload.TryGetProperty("expiresAt", out _));

        var cashierResolved = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        cashier.On<JsonElement>("AuthorizationResolved", payload => cashierResolved.TrySetResult(payload));
        var adminClosure = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        admin.On<JsonElement>("AuthorizationResolved", payload => adminClosure.TrySetResult(payload));

        var resolveResult = await admin.InvokeAsync<JsonElement>("ResolveAuthorization", requestId, true, "Aprobado remoto");

        Assert.True(resolveResult.GetProperty("success").GetBoolean());
        Assert.Equal("Approved", resolveResult.GetProperty("status").GetString());
        Assert.Equal("Admin Dos", resolveResult.GetProperty("resolvedByName").GetString());

        var cashierPayload = await cashierResolved.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(requestId, cashierPayload.GetProperty("requestId").GetInt32());
        Assert.Equal("Approved", cashierPayload.GetProperty("status").GetString());
        var token = cashierPayload.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        var claims = app.TokenService.Validate(token);
        Assert.NotNull(claims);
        Assert.Equal(requestId, claims!.RequestId);
        Assert.Equal(70, claims.CashierUserId);
        Assert.Equal(445, claims.SaleId);

        var closurePayload = await adminClosure.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(requestId, closurePayload.GetProperty("requestId").GetInt32());
        Assert.Equal("Admin Dos", closurePayload.GetProperty("resolvedByName").GetString());
        Assert.False(closurePayload.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task RequestAuthorization_DuplicatePending_ReturnsSameRequestIdWithoutSecondPush()
    {
        await using var app = await HubTestApplication.CreateAsync();
        await using var cashier = app.CreateConnection(70, UserRole.Cashier, "Cajero 70");
        await cashier.StartAsync();

        var contract = new RequestAuthorizationContract
        {
            SaleId = 446,
            ProductId = 11,
            ProductName = "Harina",
            Quantity = 1m,
            CustomUnitPriceUsd = 4.5m,
            Terminal = "Caja-01"
        };

        var first = await cashier.InvokeAsync<JsonElement>("RequestAuthorization", contract);
        var second = await cashier.InvokeAsync<JsonElement>("RequestAuthorization", contract);

        Assert.True(first.GetProperty("success").GetBoolean());
        Assert.False(first.GetProperty("deduplicated").GetBoolean());
        Assert.True(second.GetProperty("success").GetBoolean());
        Assert.True(second.GetProperty("deduplicated").GetBoolean());
        Assert.Equal(first.GetProperty("requestId").GetInt32(), second.GetProperty("requestId").GetInt32());
        Assert.Equal(1, await app.Db.AuthorizationRequests.CountAsync());
    }

    [Fact]
    public async Task ResolveAuthorization_NonElevatedInvoker_GetsDeniedResultAndRequestStaysPending()
    {
        await using var app = await HubTestApplication.CreateAsync();
        await using var cashier = app.CreateConnection(70, UserRole.Cashier, "Cajero 70");
        await cashier.StartAsync();

        var createResult = await cashier.InvokeAsync<JsonElement>("RequestAuthorization", new RequestAuthorizationContract
        {
            SaleId = 447,
            ProductId = 12,
            ProductName = "Azucar",
            Quantity = 3m,
            CustomUnitPriceUsd = 2.25m,
            Terminal = "Caja-01"
        });
        var requestId = createResult.GetProperty("requestId").GetInt32();

        var denied = await cashier.InvokeAsync<JsonElement>("ResolveAuthorization", requestId, true, "Intento de cajero");

        Assert.False(denied.GetProperty("success").GetBoolean());
        var persisted = await app.Db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId);
        Assert.Equal(AuthorizationStatus.Pending, persisted.Status);
        Assert.Null(persisted.ResolvedByUserId);
    }

    [Fact]
    public async Task ResolveAuthorization_SecondAdminLosesRaceWithExactMessage()
    {
        await using var app = await HubTestApplication.CreateAsync();
        await using var cashier = app.CreateConnection(70, UserRole.Cashier, "Cajero 70");
        await using var firstAdmin = app.CreateConnection(2, UserRole.Admin, "Admin Uno");
        await using var secondAdmin = app.CreateConnection(3, UserRole.Admin, "Admin Dos");
        await cashier.StartAsync();
        await firstAdmin.StartAsync();
        await secondAdmin.StartAsync();

        var createResult = await cashier.InvokeAsync<JsonElement>("RequestAuthorization", new RequestAuthorizationContract
        {
            SaleId = 448,
            ProductId = 13,
            ProductName = "Leche",
            Quantity = 1m,
            CustomUnitPriceUsd = 1.75m,
            Terminal = "Caja-01"
        });
        var requestId = createResult.GetProperty("requestId").GetInt32();

        var first = await firstAdmin.InvokeAsync<JsonElement>("ResolveAuthorization", requestId, true, null);
        var second = await secondAdmin.InvokeAsync<JsonElement>("ResolveAuthorization", requestId, false, "Tarde");

        Assert.True(first.GetProperty("success").GetBoolean());
        Assert.False(second.GetProperty("success").GetBoolean());
        Assert.Equal("Esta solicitud ya fue resuelta por Admin Uno.", second.GetProperty("message").GetString());
        var persisted = await app.Db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId);
        Assert.Equal(AuthorizationStatus.Approved, persisted.Status);
        Assert.Equal("Admin Uno", persisted.ResolvedByName);
    }

    [Fact]
    public async Task HubConnection_WithoutCredentials_IsRejected()
    {
        await using var app = await HubTestApplication.CreateAsync();
        await using var anonymous = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "hubs/authorization"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
            })
            .Build();

        var exception = await Record.ExceptionAsync(() => anonymous.StartAsync());

        Assert.NotNull(exception);
        Assert.NotEqual(HubConnectionState.Connected, anonymous.State);
    }

    // ---------------------------------------------------------------- enrutamiento del notifier

    private static AuthorizationRequestDto NotifierRequest(
        AuthorizationStatus status = AuthorizationStatus.Pending,
        AuthorizationResolutionMode? mode = null,
        DateTime? resolvedAt = null) => new()
        {
            Id = 7,
            ActionType = AuthorizationActionType.ManualPriceOverride,
            SaleId = 445,
            RequestedByUserId = 70,
            RequestedByName = "Cajero 70",
            Terminal = "Caja-01",
            Status = status,
            ResolutionMode = mode,
            ResolvedByUserId = mode is null ? null : 2,
            ResolvedByName = mode is null ? null : "Admin Dos",
            ResolutionReason = mode is null ? null : "Aprobado",
            ContextJson = """{"productId":10,"productName":"Cafe molido","quantity":2}""",
            ContextHash = new string('a', 64),
            CreatedAt = FixedUtc,
            ExpiresAt = FixedUtc.AddSeconds(60),
            ResolvedAt = resolvedAt
        };

    private static (SignalRAuthorizationNotifier Notifier, Mock<IClientProxy> Elevated, Mock<IClientProxy> Requester) CreateNotifier()
    {
        var elevated = new Mock<IClientProxy>();
        elevated.Setup(proxy => proxy.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var requester = new Mock<IClientProxy>();
        requester.Setup(proxy => proxy.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.Setup(registry => registry.Group("role:elevated")).Returns(elevated.Object);
        clients.Setup(registry => registry.Group("user:70")).Returns(requester.Object);
        var hubContext = new Mock<IHubContext<AuthorizationHub>>();
        hubContext.SetupGet(context => context.Clients).Returns(clients.Object);

        return (new SignalRAuthorizationNotifier(hubContext.Object), elevated, requester);
    }

    [Fact]
    public async Task NotifyRequestCreated_SendsEnrichedPayloadToElevatedGroupOnly()
    {
        var (notifier, elevated, requester) = CreateNotifier();
        JsonElement? captured = null;
        elevated.Setup(proxy => proxy.SendCoreAsync("AuthorizationRequested", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, arguments, _) => captured = JsonSerializer.SerializeToElement(arguments[0]))
            .Returns(Task.CompletedTask);

        await notifier.NotifyRequestCreatedAsync(NotifierRequest());

        Assert.NotNull(captured);
        Assert.Equal(7, captured!.Value.GetProperty("requestId").GetInt32());
        Assert.Equal("ManualPriceOverride", captured.Value.GetProperty("actionType").GetString());
        Assert.Equal(445, captured.Value.GetProperty("saleId").GetInt32());
        Assert.Equal("Cajero 70", captured.Value.GetProperty("requestedByName").GetString());
        Assert.Equal("Caja-01", captured.Value.GetProperty("terminal").GetString());
        Assert.Equal("Cafe molido", captured.Value.GetProperty("context").GetProperty("productName").GetString());
        Assert.Equal(FixedUtc.AddSeconds(60), captured.Value.GetProperty("expiresAt").GetDateTime());
        requester.Verify(proxy => proxy.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NotifyResolved_SendsTokenToRequesterAndClosureWithoutTokenToElevated()
    {
        var (notifier, elevated, requester) = CreateNotifier();
        JsonElement? requesterPayload = null;
        JsonElement? elevatedPayload = null;
        requester.Setup(proxy => proxy.SendCoreAsync("AuthorizationResolved", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, arguments, _) => requesterPayload = JsonSerializer.SerializeToElement(arguments[0]))
            .Returns(Task.CompletedTask);
        elevated.Setup(proxy => proxy.SendCoreAsync("AuthorizationResolved", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, arguments, _) => elevatedPayload = JsonSerializer.SerializeToElement(arguments[0]))
            .Returns(Task.CompletedTask);

        var resolvedAt = FixedUtc.AddSeconds(5);
        await notifier.NotifyResolvedAsync(
            NotifierRequest(AuthorizationStatus.Approved, AuthorizationResolutionMode.Remote, resolvedAt),
            "signed-token");

        Assert.NotNull(requesterPayload);
        Assert.Equal("Approved", requesterPayload!.Value.GetProperty("status").GetString());
        Assert.Equal("signed-token", requesterPayload.Value.GetProperty("token").GetString());
        Assert.Equal("Admin Dos", requesterPayload.Value.GetProperty("resolvedByName").GetString());

        Assert.NotNull(elevatedPayload);
        Assert.Equal(7, elevatedPayload!.Value.GetProperty("requestId").GetInt32());
        Assert.Equal("Admin Dos", elevatedPayload.Value.GetProperty("resolvedByName").GetString());
        Assert.Equal("Remote", elevatedPayload.Value.GetProperty("resolutionMode").GetString());
        Assert.False(elevatedPayload.Value.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task NotifyExpired_SendsPayloadToRequesterAndElevatedWithoutToken()
    {
        var (notifier, elevated, requester) = CreateNotifier();

        await notifier.NotifyExpiredAsync(NotifierRequest(AuthorizationStatus.Expired, resolvedAt: FixedUtc.AddSeconds(60)));

        requester.Verify(
            proxy => proxy.SendCoreAsync("AuthorizationExpired", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
        elevated.Verify(
            proxy => proxy.SendCoreAsync("AuthorizationExpired", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ---------------------------------------------------------------- filtro global de sello (8.157)

    [Fact]
    public async Task HubConnection_WithRevokedStamp_ReconnectIsRejectedByStampValidationFilter()
    {
        await using var app = await HubTestApplication.CreateAsync(enableStampValidation: true);
        const string stamp = "stamp-vigente-8157";
        SeedUser(app, 70, stamp);

        // Sello vigente: conexión e invocación fluyen sin cambios de comportamiento.
        await using var valid = app.CreateConnection(70, UserRole.Cashier, "Cajero 70", securityStamp: stamp);
        await valid.StartAsync();
        Assert.Equal(HubConnectionState.Connected, valid.State);
        var validInvoke = await valid.InvokeAsync<JsonElement>("ResolveAuthorization", 8157, true, null);
        Assert.False(validInvoke.GetProperty("success").GetBoolean());

        // Revocación real: rota el sello e invalida la caché (sin IHubContexts el push es no-op).
        await app.StampValidator!.RevokeUserStampAsync(70);

        // La reconexión con el token viejo queda rechazada por el filtro global.
        await using var revoked = app.CreateConnection(70, UserRole.Cashier, "Cajero 70", securityStamp: stamp);
        var rejection = await StartAndAwaitRejectionAsync(revoked);

        var hubException = Assert.IsType<HubException>(rejection);
        // El cliente prefija el error del CloseMessage ("The server closed the connection with the
        // following error: ..."); el mensaje canónico del middleware viaja embebido.
        Assert.Contains(StampValidationHubFilter.InvalidStampMessage, hubException.Message);
        Assert.NotEqual(HubConnectionState.Connected, revoked.State);
    }

    [Fact]
    public async Task HubConnection_WithRevokedStamp_InvokeIsRejectedAndConnectionAborted()
    {
        await using var app = await HubTestApplication.CreateAsync(enableStampValidation: true);
        const string stamp = "stamp-vigente-invoke-8157";
        SeedUser(app, 70, stamp);

        await using var connection = app.CreateConnection(70, UserRole.Cashier, "Cajero 70", securityStamp: stamp);
        await connection.StartAsync();
        Assert.Equal(HubConnectionState.Connected, connection.State);

        // Invocación válida previa: garantiza que la validación de OnConnectedAsync del servidor
        // ya terminó (el DbContext compartido del harness no tolera operaciones solapadas) y
        // confirma que el filtro no altera el camino válido.
        var validInvoke = await connection.InvokeAsync<JsonElement>("ResolveAuthorization", 8157, true, null);
        Assert.False(validInvoke.GetProperty("success").GetBoolean());

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };

        await app.StampValidator!.RevokeUserStampAsync(70);

        // Con la conexión viva, la invocación siguiente revalida el sello (forceImmediateCheck)
        // y el filtro aborta: la invocación no puede completar con resultado.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            connection.InvokeAsync<JsonElement>("ResolveAuthorization", 8157, true, null)
                .WaitAsync(TimeSpan.FromSeconds(15)));

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    private static void SeedUser(HubTestApplication app, int id, string securityStamp)
    {
        app.Db.Users.Add(new User
        {
            Id = id,
            Cedula = $"V-{id:D8}",
            Username = $"usuario{id}",
            Name = $"Cajero {id}",
            FullName = $"Cajero {id}",
            Role = UserRole.Cashier,
            IsActive = true,
            SecurityStamp = securityStamp
        });
        app.Db.SaveChanges();
    }

    /// <summary>
    /// Arranca la conexión y normaliza el rechazo del servidor: según el timing de LongPolling el
    /// cierre puede observarse dentro de StartAsync o inmediatamente después vía el evento Closed.
    /// </summary>
    private static async Task<Exception> StartAndAwaitRejectionAsync(HubConnection connection)
    {
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += exception => { closed.TrySetResult(exception); return Task.CompletedTask; };

        try
        {
            await connection.StartAsync();
        }
        catch (Exception startException)
        {
            return startException;
        }

        var closedException = await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(closedException);
        return closedException!;
    }

    // ---------------------------------------------------------------- infraestructura

    private sealed class HubTestApplication : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly WebApplication _application;

        public SalesDbContext Db { get; }

        public AuthorizationTokenService TokenService { get; }

        /// <summary>8.157: validador real (SQLite) cuando el filtro global está habilitado.</summary>
        public SecurityStampValidator? StampValidator { get; }

        public TestServer Server => _application.GetTestServer();

        private HubTestApplication(
            SqliteConnection connection,
            WebApplication application,
            SalesDbContext db,
            AuthorizationTokenService tokenService,
            SecurityStampValidator? stampValidator)
        {
            _connection = connection;
            _application = application;
            Db = db;
            TokenService = tokenService;
            StampValidator = stampValidator;
        }

        public static async Task<HubTestApplication> CreateAsync(bool enableStampValidation = false)
        {
            var (db, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
            var tokenService = new AuthorizationTokenService(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "JWT_SETTINGS_KEY", TestKey },
                    { "JwtSettings:Issuer", "SolucionesPos" },
                    { "JwtSettings:Audience", "PosClient" }
                }).Build(),
                TimeSpan.FromSeconds(60));

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Services.AddSignalR(options =>
            {
                options.EnableDetailedErrors = true;
                if (enableStampValidation)
                {
                    options.AddFilter<StampValidationHubFilter>();
                }
            });
            builder.Services.AddAuthentication("TestAuth")
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>("TestAuth", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton<IAuthorizationTokenService>(tokenService);

            SecurityStampValidator? stampValidator = null;
            if (enableStampValidation)
            {
                stampValidator = new SecurityStampValidator(db, new MemoryCache(new MemoryCacheOptions()));
                builder.Services.AddSingleton<ISecurityStampValidator>(stampValidator);
            }
            builder.Services.AddScoped<IAuthorizationService>(provider =>
                new AuthorizationService(provider.GetRequiredService<SalesDbContext>(), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)));
            builder.Services.AddScoped<IAuthorizationNotifier, SignalRAuthorizationNotifier>();
            builder.Services.AddScoped<IAuthorizationCoordinator>(provider =>
                new AuthorizationCoordinator(
                    provider.GetRequiredService<IAuthorizationService>(),
                    provider.GetRequiredService<IAuthorizationTokenService>(),
                    provider.GetRequiredService<IAuthorizationNotifier>(),
                    provider.GetRequiredService<SalesDbContext>(),
                    TimeSpan.FromSeconds(60)));

            var application = builder.Build();
            application.UseAuthentication();
            application.UseAuthorization();
            application.MapHub<AuthorizationHub>("/hubs/authorization").RequireAuthorization();
            await application.StartAsync();
            return new HubTestApplication(connection, application, db, tokenService, stampValidator);
        }

        public HubConnection CreateConnection(int userId, UserRole role, string name, string? securityStamp = null)
        {
            var headers = new Dictionary<string, string>
            {
                ["X-Test-User-Id"] = userId.ToString(CultureInfo.InvariantCulture),
                ["X-Test-User-Role"] = role.ToString(),
                ["X-Test-User-Name"] = name
            };
            if (!string.IsNullOrWhiteSpace(securityStamp))
            {
                headers["X-Test-User-Stamp"] = securityStamp;
            }

            return new HubConnectionBuilder()
                .WithUrl(new Uri(Server.BaseAddress, "hubs/authorization"), options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                    options.Headers = headers;
                })
                .Build();
        }

        public async ValueTask DisposeAsync()
        {
            await _application.DisposeAsync();
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class HeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public HeaderAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var userId = Request.Headers["X-Test-User-Id"].ToString();
            var role = Request.Headers["X-Test-User-Role"].ToString();
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var name = Request.Headers["X-Test-User-Name"].ToString();
            var securityStamp = Request.Headers["X-Test-User-Stamp"].ToString();
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(name) ? $"Usuario {userId}" : name),
                new Claim(ClaimTypes.Role, role)
            };
            if (!string.IsNullOrWhiteSpace(securityStamp))
            {
                claims.Add(new Claim("security_stamp", securityStamp));
            }
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
