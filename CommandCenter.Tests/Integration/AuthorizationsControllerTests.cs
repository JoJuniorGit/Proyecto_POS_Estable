using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Hubs;
using Backend.API.Services;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.150 (T4, design D4): par REST del hub de autorizaciones (create, estado con recuperacion
/// de token, resolucion remota race-safe y fallback local). Corre sobre TestServer con el
/// pipeline MVC real, autenticacion por headers de prueba y SQLite.
/// </summary>
public class AuthorizationsControllerTests
{
    private const string TestKey = "POS_Test_Super_Secret_Key_At_Least_32_Chars_Long!";

    private static RequestAuthorizationContract CreateContract(int saleId = 445, int productId = 10) => new()
    {
        SaleId = saleId,
        ProductId = productId,
        ProductName = "Cafe molido",
        Quantity = 2m,
        CustomUnitPriceUsd = 9.99m,
        CustomUnitPriceLocal = 1234.5m,
        Terminal = "Caja-01"
    };

    private static async Task<int> CreateRequestAsync(HttpClient client, int saleId = 445)
    {
        using var response = await client.PostAsJsonAsync("/api/authorizations", CreateContract(saleId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestId").GetInt32();
    }

    private static async Task<ProblemDetails> ReadProblemAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        return problem!;
    }

    // ---------------------------------------------------------------- creacion

    [Fact]
    public async Task Create_AsCashier_ReturnsPendingRequest()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");

        using var response = await cashier.PostAsJsonAsync("/api/authorizations", CreateContract());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requestId = body.GetProperty("requestId").GetInt32();
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("deduplicated").GetBoolean());
        Assert.InRange(body.GetProperty("remainingLifetimeSeconds").GetInt32(), 58, 60);

        var persisted = await app.Db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId);
        Assert.Equal(AuthorizationStatus.Pending, persisted.Status);
        Assert.Equal(70, persisted.RequestedByUserId);
        Assert.Equal("Cajero 70", persisted.RequestedByName);
        Assert.Equal("Caja-01", persisted.Terminal);
    }

    [Fact]
    public async Task Create_DuplicatePending_ReturnsExistingRequestAsDeduplicated()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        var firstId = await CreateRequestAsync(cashier);

        using var response = await cashier.PostAsJsonAsync("/api/authorizations", CreateContract());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("deduplicated").GetBoolean());
        Assert.Equal(firstId, body.GetProperty("requestId").GetInt32());
        Assert.Equal(1, await app.Db.AuthorizationRequests.CountAsync());
    }

    [Fact]
    public async Task Create_AsElevated_ReturnsConflictWithExactMessage()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var admin = app.CreateClient(2, UserRole.Admin, "Admin Dos");

        using var response = await admin.PostAsJsonAsync("/api/authorizations", CreateContract());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(AuthorizationMessages.ElevationNotRequired, problem.Detail);
        Assert.Equal(0, await app.Db.AuthorizationRequests.CountAsync());
    }

    [Fact]
    public async Task Create_AsDriver_ReturnsForbiddenWithExactMessage()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var driver = app.CreateClient(90, UserRole.Driver, "Conductor 90");

        using var response = await driver.PostAsJsonAsync("/api/authorizations", CreateContract());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(AuthorizationMessages.DriverBlocked, problem.Detail);
        Assert.Equal(0, await app.Db.AuthorizationRequests.CountAsync());
    }

    [Fact]
    public async Task Create_Unauthenticated_Returns401()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var anonymous = app.CreateAnonymousClient();

        using var response = await anonymous.PostAsJsonAsync("/api/authorizations", CreateContract());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await app.Db.AuthorizationRequests.CountAsync());
    }

    // ---------------------------------------------------------------- estado y recuperacion

    [Fact]
    public async Task GetStatus_AsRequester_ReturnsRequestWithoutTokenWhilePending()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        var requestId = await CreateRequestAsync(cashier);

        using var response = await cashier.GetAsync($"/api/authorizations/{requestId}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(requestId, body.GetProperty("id").GetInt32());
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("token").ValueKind);
    }

    [Fact]
    public async Task GetStatus_AsOtherCashier_Returns404()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        using var otherCashier = app.CreateClient(71, UserRole.Cashier, "Cajero 71");
        var requestId = await CreateRequestAsync(cashier);

        using var response = await otherCashier.GetAsync($"/api/authorizations/{requestId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(AuthorizationMessages.RequestNotFound, problem.Detail);
    }

    [Fact]
    public async Task GetStatus_AfterApproval_RecoversTokenForRequesterOnly()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        using var admin = app.CreateClient(2, UserRole.Admin, "Admin Dos");
        var requestId = await CreateRequestAsync(cashier);
        using var resolveResponse = await admin.PostAsJsonAsync(
            $"/api/authorizations/{requestId}/resolve",
            new { approved = true, reason = "Aprobado remoto" });
        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);

        using var requesterResponse = await cashier.GetAsync($"/api/authorizations/{requestId}");
        var requesterBody = await requesterResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, requesterResponse.StatusCode);
        Assert.Equal("Approved", requesterBody.GetProperty("status").GetString());
        var token = requesterBody.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        var claims = app.TokenService.Validate(token);
        Assert.NotNull(claims);
        Assert.Equal(requestId, claims!.RequestId);
        Assert.Equal(70, claims.CashierUserId);

        using var elevatedResponse = await admin.GetAsync($"/api/authorizations/{requestId}");
        var elevatedBody = await elevatedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, elevatedResponse.StatusCode);
        Assert.Equal("Approved", elevatedBody.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, elevatedBody.GetProperty("token").ValueKind);
    }

    [Fact]
    public async Task GetStatus_AfterTokenWindow_DoesNotReturnToken()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        using var admin = app.CreateClient(2, UserRole.Admin, "Admin Dos");
        var requestId = await CreateRequestAsync(cashier);
        using var resolveResponse = await admin.PostAsJsonAsync(
            $"/api/authorizations/{requestId}/resolve",
            new { approved = true, reason = (string?)null });
        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);

        var row = await app.Db.AuthorizationRequests.SingleAsync(candidate => candidate.Id == requestId);
        row.ResolvedAt = row.ResolvedAt!.Value.AddSeconds(-61);
        await app.Db.SaveChangesAsync();

        using var response = await cashier.GetAsync($"/api/authorizations/{requestId}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Approved", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("token").ValueKind);
    }

    // ---------------------------------------------------------------- resolucion remota

    [Fact]
    public async Task Resolve_FirstWins_SecondGetsExactRaceMessage()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        using var firstAdmin = app.CreateClient(2, UserRole.Admin, "Admin Uno");
        using var secondAdmin = app.CreateClient(3, UserRole.Admin, "Admin Dos");
        var requestId = await CreateRequestAsync(cashier);

        using var first = await firstAdmin.PostAsJsonAsync(
            $"/api/authorizations/{requestId}/resolve",
            new { approved = true, reason = "Primero" });
        using var second = await secondAdmin.PostAsJsonAsync(
            $"/api/authorizations/{requestId}/resolve",
            new { approved = false, reason = "Tarde" });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await ReadProblemAsync(second);
        Assert.Equal("Esta solicitud ya fue resuelta por Admin Uno.", problem.Detail);

        var persisted = await app.Db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId);
        Assert.Equal(AuthorizationStatus.Approved, persisted.Status);
        Assert.Equal(2, persisted.ResolvedByUserId);
        Assert.Equal(1, await app.Db.AuthorizationAudits.CountAsync(audit => audit.RequestId == requestId));
    }

    [Fact]
    public async Task Resolve_AsCashier_Returns403AndLeavesRequestPending()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        var requestId = await CreateRequestAsync(cashier);

        using var response = await cashier.PostAsJsonAsync(
            $"/api/authorizations/{requestId}/resolve",
            new { approved = true, reason = (string?)null });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var persisted = await app.Db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId);
        Assert.Equal(AuthorizationStatus.Pending, persisted.Status);
    }

    [Fact]
    public async Task Resolve_ExpiredRequest_ReturnsConflictWithExactExpiredMessage()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        using var admin = app.CreateClient(2, UserRole.Admin, "Admin Dos");
        var requestId = await CreateRequestAsync(cashier);
        var row = await app.Db.AuthorizationRequests.SingleAsync(candidate => candidate.Id == requestId);
        row.ExpiresAt = DateTime.UtcNow.AddSeconds(-5);
        await app.Db.SaveChangesAsync();

        using var response = await admin.PostAsJsonAsync(
            $"/api/authorizations/{requestId}/resolve",
            new { approved = true, reason = (string?)null });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(AuthorizationMessages.RequestExpired, problem.Detail);

        var persisted = await app.Db.AuthorizationRequests.AsNoTracking().SingleAsync(candidate => candidate.Id == requestId);
        Assert.Equal(AuthorizationStatus.Expired, persisted.Status);
        Assert.Equal(1, await app.Db.AuthorizationAudits.CountAsync(audit => audit.RequestId == requestId && audit.Status == AuthorizationStatus.Expired));
    }

    [Fact]
    public async Task Resolve_NotFound_Returns404()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var admin = app.CreateClient(2, UserRole.Admin, "Admin Dos");

        using var response = await admin.PostAsJsonAsync(
            "/api/authorizations/99999/resolve",
            new { approved = true, reason = (string?)null });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(AuthorizationMessages.RequestNotFound, problem.Detail);
    }

    // ---------------------------------------------------------------- W1 (8.151): cancelacion

    [Fact]
    public async Task Cancel_AsRequester_ReturnsCancelledStatusAndSingleAudit()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        var requestId = await CreateRequestAsync(cashier);

        using var response = await cashier.PostAsync($"/api/authorizations/{requestId}/cancel", content: null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(requestId, body.GetProperty("requestId").GetInt32());
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("resolvedAt").ValueKind);

        var persisted = await app.Db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId);
        Assert.Equal(AuthorizationStatus.Cancelled, persisted.Status);
        Assert.NotNull(persisted.ResolvedAt);
        Assert.Null(persisted.ResolutionMode);

        var audit = await app.Db.AuthorizationAudits.AsNoTracking().SingleAsync(row => row.RequestId == requestId);
        Assert.Equal(AuthorizationStatus.Cancelled, audit.Status);
        Assert.Null(audit.ResolutionMode);
        Assert.Null(audit.ResolvedByUserId);
        Assert.Null(audit.ResolvedByName);
    }

    [Fact]
    public async Task Cancel_AsOtherCashier_ReturnsForbiddenWithExactMessage()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        using var otherCashier = app.CreateClient(71, UserRole.Cashier, "Cajero 71");
        var requestId = await CreateRequestAsync(cashier);

        using var response = await otherCashier.PostAsync($"/api/authorizations/{requestId}/cancel", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("Solo el solicitante puede cancelar la solicitud.", problem.Detail);
        Assert.Equal(AuthorizationStatus.Pending, (await app.Db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId)).Status);
        Assert.Equal(0, await app.Db.AuthorizationAudits.CountAsync(row => row.RequestId == requestId));
    }

    [Fact]
    public async Task Cancel_AfterApproval_ReturnsConflictWithRaceMessage()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        using var admin = app.CreateClient(2, UserRole.Admin, "Admin Uno");
        var requestId = await CreateRequestAsync(cashier);
        using var resolveResponse = await admin.PostAsJsonAsync(
            $"/api/authorizations/{requestId}/resolve",
            new { approved = true, reason = "Aprobado remoto" });
        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);

        using var response = await cashier.PostAsync($"/api/authorizations/{requestId}/cancel", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("Esta solicitud ya fue resuelta por Admin Uno.", problem.Detail);
        Assert.Equal(1, await app.Db.AuthorizationAudits.CountAsync(row => row.RequestId == requestId));
    }

    [Fact]
    public async Task Cancel_ExpiredRequest_ReturnsConflictWithExpiredMessage()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        var requestId = await CreateRequestAsync(cashier);
        var row = await app.Db.AuthorizationRequests.SingleAsync(candidate => candidate.Id == requestId);
        row.ExpiresAt = DateTime.UtcNow.AddSeconds(-5);
        await app.Db.SaveChangesAsync();

        using var response = await cashier.PostAsync($"/api/authorizations/{requestId}/cancel", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(AuthorizationMessages.RequestExpired, problem.Detail);
        Assert.Equal(AuthorizationStatus.Expired, (await app.Db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId)).Status);
    }

    [Fact]
    public async Task Cancel_NotFound_Returns404()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");

        using var response = await cashier.PostAsync("/api/authorizations/99999/cancel", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(AuthorizationMessages.RequestNotFound, problem.Detail);
    }

    [Fact]
    public async Task Cancel_Unauthenticated_Returns401()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var anonymous = app.CreateAnonymousClient();

        using var response = await anonymous.PostAsync("/api/authorizations/99999/cancel", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------------------------------------------------------- fallback local

    [Fact]
    public async Task LocalResolve_ValidSupervisor_ReturnsTokenAndSupervisorIdentity()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        await app.SeedSupervisorAsync(80, UserRole.Admin, "SuperClave123!");
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        var requestId = await CreateRequestAsync(cashier);

        using var response = await cashier.PostAsJsonAsync(
            $"/api/authorizations/{requestId}/local-resolve",
            new { username = "usuario80", password = "SuperClave123!", reason = "En sitio" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(80, body.GetProperty("supervisorUserId").GetInt32());
        Assert.Equal("Supervisora 80", body.GetProperty("supervisorName").GetString());
        var token = body.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        var claims = app.TokenService.Validate(token);
        Assert.NotNull(claims);
        Assert.Equal(requestId, claims!.RequestId);
        Assert.Equal(70, claims.CashierUserId);

        var persisted = await app.Db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId);
        Assert.Equal(AuthorizationStatus.Approved, persisted.Status);
        Assert.Equal(AuthorizationResolutionMode.Local, persisted.ResolutionMode);
        Assert.Equal(80, persisted.ResolvedByUserId);
        Assert.Equal(1, await app.Db.AuthorizationAudits.CountAsync(audit => audit.RequestId == requestId && audit.ResolutionMode == AuthorizationResolutionMode.Local));
    }

    [Fact]
    public async Task LocalResolve_WrongPassword_ReturnsGenericMessageAndCountsAttempt()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        await app.SeedSupervisorAsync(80, UserRole.Admin, "SuperClave123!");
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        var requestId = await CreateRequestAsync(cashier);

        using var response = await cashier.PostAsJsonAsync(
            $"/api/authorizations/{requestId}/local-resolve",
            new { username = "usuario80", password = "ClaveIncorrecta", reason = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(AuthorizationMessages.InvalidCredentials, problem.Detail);

        var user = await app.Db.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == 80);
        Assert.Equal(1, user.AccessFailedCount);
        var persisted = await app.Db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId);
        Assert.Equal(AuthorizationStatus.Pending, persisted.Status);
    }

    [Fact]
    public async Task LocalResolve_LockedSupervisor_ReturnsGenericMessageWithoutDisclosure()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        await app.SeedSupervisorAsync(
            81,
            UserRole.Admin,
            "SuperClave123!",
            accessFailedCount: 5,
            lockoutEndUtc: DateTime.UtcNow.AddMinutes(5));
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        var requestId = await CreateRequestAsync(cashier);

        using var response = await cashier.PostAsJsonAsync(
            $"/api/authorizations/{requestId}/local-resolve",
            new { username = "usuario81", password = "SuperClave123!", reason = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(AuthorizationMessages.InvalidCredentials, problem.Detail);
        var persisted = await app.Db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId);
        Assert.Equal(AuthorizationStatus.Pending, persisted.Status);
    }

    [Fact]
    public async Task LocalResolve_MissingCredentials_ReturnsGenericMessage()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        var requestId = await CreateRequestAsync(cashier);

        using var response = await cashier.PostAsJsonAsync(
            $"/api/authorizations/{requestId}/local-resolve",
            new { username = "", password = "", reason = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(AuthorizationMessages.InvalidCredentials, problem.Detail);
    }

    [Fact]
    public async Task LocalResolve_ExpiredRequest_ReturnsConflictWithExpiredMessage()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        await app.SeedSupervisorAsync(82, UserRole.Admin, "SuperClave123!");
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");
        var requestId = await CreateRequestAsync(cashier);
        var row = await app.Db.AuthorizationRequests.SingleAsync(candidate => candidate.Id == requestId);
        row.ExpiresAt = DateTime.UtcNow.AddSeconds(-5);
        await app.Db.SaveChangesAsync();

        using var response = await cashier.PostAsJsonAsync(
            $"/api/authorizations/{requestId}/local-resolve",
            new { username = "usuario82", password = "SuperClave123!", reason = (string?)null });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(AuthorizationMessages.RequestExpired, problem.Detail);
    }

    [Fact]
    public async Task LocalResolve_NotFound_Returns404()
    {
        await using var app = await ControllerTestApplication.CreateAsync();
        using var cashier = app.CreateClient(70, UserRole.Cashier, "Cajero 70");

        using var response = await cashier.PostAsJsonAsync(
            "/api/authorizations/99999/local-resolve",
            new { username = "usuario80", password = "SuperClave123!", reason = (string?)null });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(AuthorizationMessages.RequestNotFound, problem.Detail);
    }

    // ---------------------------------------------------------------- infraestructura

    private sealed class ControllerTestApplication : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly WebApplication _application;

        public SalesDbContext Db { get; }

        public AuthorizationTokenService TokenService { get; }

        private ControllerTestApplication(
            SqliteConnection connection,
            WebApplication application,
            SalesDbContext db,
            AuthorizationTokenService tokenService)
        {
            _connection = connection;
            _application = application;
            Db = db;
            TokenService = tokenService;
        }

        public static async Task<ControllerTestApplication> CreateAsync()
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
            builder.Services.AddControllers().AddApplicationPart(typeof(AuthorizationsController).Assembly);
            builder.Services.AddSignalR();
            builder.Services.AddAuthentication("TestAuth")
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>("TestAuth", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton<IAuthorizationTokenService>(tokenService);
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
            application.MapControllers();
            await application.StartAsync();
            return new ControllerTestApplication(connection, application, db, tokenService);
        }

        public HttpClient CreateClient(int userId, UserRole role, string name)
        {
            var client = _application.GetTestServer().CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-User-Id", userId.ToString(CultureInfo.InvariantCulture));
            client.DefaultRequestHeaders.Add("X-Test-User-Role", role.ToString());
            client.DefaultRequestHeaders.Add("X-Test-User-Name", name);
            return client;
        }

        public HttpClient CreateAnonymousClient() => _application.GetTestServer().CreateClient();

        public async Task SeedSupervisorAsync(
            int id,
            UserRole role,
            string password,
            int accessFailedCount = 0,
            DateTime? lockoutEndUtc = null)
        {
            Db.Users.Add(new User
            {
                Id = id,
                Cedula = $"V-{id:D8}",
                Name = $"Supervisora {id}",
                Username = $"usuario{id}",
                FullName = $"Supervisora {id}",
                Role = role,
                PasswordHash = Core.Security.PasswordHasher.HashPassword(password),
                AccessFailedCount = accessFailedCount,
                LockoutEndUtc = lockoutEndUtc
            });
            await Db.SaveChangesAsync();
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
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(name) ? $"Usuario {userId}" : name),
                new Claim(ClaimTypes.Role, role)
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
