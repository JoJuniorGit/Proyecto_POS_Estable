using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Backend.API.Controllers;
using Backend.API.Hubs;
using Backend.API.Services;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.150 (T5, design D5): gate de la accion protegida ManualPriceOverride sobre
/// POST /api/sales/{id}/items. Ciclo completo por HTTP: solicitud del cajero, aprobacion
/// remota, recuperacion del token, consumo single-use, replay idempotente y contrato 403
/// con extensiones authorizationRequired/authorizationAction.
/// 8.151 (R10, design D8): ademas fija el alcance deliberado del PUT /api/sales/{id}/items
/// (403 por roles del repositorio, sin extensiones de flujo y sin aceptar X-Authorization-Token).
/// </summary>
public class ProtectedActionAuthorizationTests
{
    private const string TestKey = "POS_Test_Super_Secret_Key_At_Least_32_Chars_Long!";
    private const string ForbiddenMessage = "Modificación de precios no autorizada. Se requiere rol de Administrador o Supervisor.";
    private const int CashierId = 70;
    private const int AdminId = 2;
    private const int ProductId = 10;
    private const decimal Quantity = 2m;
    private const decimal CustomUsd = 9.99m;
    private const decimal CustomLocal = 1234.5m;
    private const decimal ExchangeRate = 50m;

    /// <summary>Mensaje exacto del 403 del PUT (rechazo del servicio por precio custom sin rol elevado).</summary>
    private static string ExpectedPutPriceOverrideMessage =>
        $"Modificación de precio no autorizada para el producto 'Producto {ProductId}'. Se requiere autorización de Administrador o Supervisor.";

    [Fact]
    public async Task PriceOverride_WithValidToken_AddsItemAndConsumesRequest()
    {
        await using var app = await ProtectedActionTestApplication.CreateAsync();
        var saleId = await app.StartSaleAsync(CashierId);
        using var cashier = app.CreateClient(CashierId, UserRole.Cashier, "Cajero 70");
        using var admin = app.CreateClient(AdminId, UserRole.Admin, "Admin Dos");
        var requestId = await CreateRequestAsync(cashier, saleId);
        var token = await ApproveAndRecoverTokenAsync(cashier, admin, requestId);

        using var response = await SendAddItemAsync(cashier, saleId, BuildItemPayload(), token, "PROT-HAPPY-01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = body.GetProperty("items")[0];
        Assert.Equal(ProductId, item.GetProperty("productId").GetInt32());
        Assert.Equal(CustomUsd, item.GetProperty("unitPrice").GetDecimal());
        Assert.True(item.GetProperty("isCustomPrice").GetBoolean());

        var persisted = await app.Db.AuthorizationRequests.AsNoTracking().SingleAsync(request => request.Id == requestId);
        Assert.NotNull(persisted.ConsumedAt);
        Assert.Equal(1, await app.Db.SaleItems.CountAsync(saleItem => saleItem.SaleId == saleId));
    }

    [Fact]
    public async Task PriceOverride_WithoutToken_ReturnsForbiddenContractWithoutMutation()
    {
        await using var app = await ProtectedActionTestApplication.CreateAsync();
        var saleId = await app.StartSaleAsync(CashierId);
        using var cashier = app.CreateClient(CashierId, UserRole.Cashier, "Cajero 70");

        // Sin Idempotency-Key a proposito: el gate debe correr antes de la idempotencia.
        using var response = await SendAddItemAsync(cashier, saleId, BuildItemPayload(), token: null, idempotencyKey: null);

        await AssertPriceOverrideForbiddenAsync(response);
        Assert.Equal(0, await app.Db.SaleItems.CountAsync(saleItem => saleItem.SaleId == saleId));
    }

    [Fact]
    public async Task PriceOverride_ReusedToken_ReturnsForbiddenWithoutMutation()
    {
        await using var app = await ProtectedActionTestApplication.CreateAsync();
        var saleId = await app.StartSaleAsync(CashierId);
        using var cashier = app.CreateClient(CashierId, UserRole.Cashier, "Cajero 70");
        using var admin = app.CreateClient(AdminId, UserRole.Admin, "Admin Dos");
        var requestId = await CreateRequestAsync(cashier, saleId);
        var token = await ApproveAndRecoverTokenAsync(cashier, admin, requestId);

        using (var first = await SendAddItemAsync(cashier, saleId, BuildItemPayload(), token, "PROT-REUSE-01"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var second = await SendAddItemAsync(cashier, saleId, BuildItemPayload(), token, "PROT-REUSE-02");

        await AssertPriceOverrideForbiddenAsync(second);
        Assert.Equal(1, await app.Db.SaleItems.CountAsync(saleItem => saleItem.SaleId == saleId));
        // El rechazo por token reusado no deja rastro de idempotencia (MISS sin ejecucion).
        Assert.False(await app.Db.IdempotentRequests.AnyAsync(record => record.Key == "PROT-REUSE-02"));
    }

    [Fact]
    public async Task PriceOverride_ContextMismatch_ReturnsForbiddenWithoutMutation()
    {
        await using var app = await ProtectedActionTestApplication.CreateAsync();
        var saleId = await app.StartSaleAsync(CashierId);
        using var cashier = app.CreateClient(CashierId, UserRole.Cashier, "Cajero 70");
        using var admin = app.CreateClient(AdminId, UserRole.Admin, "Admin Dos");
        var requestId = await CreateRequestAsync(cashier, saleId);
        var token = await ApproveAndRecoverTokenAsync(cashier, admin, requestId);

        using var response = await SendAddItemAsync(
            cashier,
            saleId,
            BuildItemPayload(customUsd: 5.00m, customLocal: 600m),
            token,
            "PROT-MISMATCH-01");

        await AssertPriceOverrideForbiddenAsync(response);
        Assert.Equal(0, await app.Db.SaleItems.CountAsync(saleItem => saleItem.SaleId == saleId));
        var persisted = await app.Db.AuthorizationRequests.AsNoTracking().SingleAsync(request => request.Id == requestId);
        Assert.Null(persisted.ConsumedAt);
    }

    [Fact]
    public async Task PriceOverride_ExpiredTokenWindow_ReturnsForbidden()
    {
        await using var app = await ProtectedActionTestApplication.CreateAsync();
        var saleId = await app.StartSaleAsync(CashierId);
        using var cashier = app.CreateClient(CashierId, UserRole.Cashier, "Cajero 70");
        using var admin = app.CreateClient(AdminId, UserRole.Admin, "Admin Dos");
        var requestId = await CreateRequestAsync(cashier, saleId);
        var token = await ApproveAndRecoverTokenAsync(cashier, admin, requestId);

        var row = await app.Db.AuthorizationRequests.SingleAsync(request => request.Id == requestId);
        row.ResolvedAt = row.ResolvedAt!.Value.AddSeconds(-61);
        await app.Db.SaveChangesAsync();

        using var response = await SendAddItemAsync(cashier, saleId, BuildItemPayload(), token, "PROT-EXPIRED-01");

        await AssertPriceOverrideForbiddenAsync(response);
        Assert.Equal(0, await app.Db.SaleItems.CountAsync(saleItem => saleItem.SaleId == saleId));
        var persisted = await app.Db.AuthorizationRequests.AsNoTracking().SingleAsync(request => request.Id == requestId);
        Assert.Null(persisted.ConsumedAt);
    }

    [Fact]
    public async Task PriceOverride_IdempotentReplayAfterSuccess_ReturnsStoredResponseWithoutDuplicate()
    {
        await using var app = await ProtectedActionTestApplication.CreateAsync();
        var saleId = await app.StartSaleAsync(CashierId);
        using var cashier = app.CreateClient(CashierId, UserRole.Cashier, "Cajero 70");
        using var admin = app.CreateClient(AdminId, UserRole.Admin, "Admin Dos");
        var requestId = await CreateRequestAsync(cashier, saleId);
        var token = await ApproveAndRecoverTokenAsync(cashier, admin, requestId);

        using (var first = await SendAddItemAsync(cashier, saleId, BuildItemPayload(), token, "PROT-REPLAY-01"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        // Mismo payload + misma clave + mismo token ya consumido: la idempotencia gana (HIT).
        using var replay = await SendAddItemAsync(cashier, saleId, BuildItemPayload(), token, "PROT-REPLAY-01");

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("HIT", replay.Headers.GetValues("X-Cache-Lookup").Single());
        Assert.Equal(1, await app.Db.SaleItems.CountAsync(saleItem => saleItem.SaleId == saleId));
    }

    [Fact]
    public async Task PriceOverride_TokenApprovedForAnotherSale_IsRejectedWithoutMutationAndStaysUsable()
    {
        await using var app = await ProtectedActionTestApplication.CreateAsync();
        var approvedSaleId = await app.StartSaleAsync(CashierId);
        var otherSaleId = await app.StartSaleAsync(CashierId);
        using var cashier = app.CreateClient(CashierId, UserRole.Cashier, "Cajero 70");
        using var admin = app.CreateClient(AdminId, UserRole.Admin, "Admin Dos");
        var requestId = await CreateRequestAsync(cashier, approvedSaleId);
        var token = await ApproveAndRecoverTokenAsync(cashier, admin, requestId);

        // El token fue aprobado para approvedSaleId: consumirlo en otra venta debe rechazarse
        // sin mutar y sin quemar el token (el claim de venta no puede validarse contra si mismo).
        using (var wrongSale = await SendAddItemAsync(cashier, otherSaleId, BuildItemPayload(), token, "PROT-CROSSSALE-01"))
        {
            await AssertPriceOverrideForbiddenAsync(wrongSale);
        }

        Assert.Equal(0, await app.Db.SaleItems.CountAsync(saleItem => saleItem.SaleId == otherSaleId));
        var persisted = await app.Db.AuthorizationRequests.AsNoTracking().SingleAsync(request => request.Id == requestId);
        Assert.Null(persisted.ConsumedAt);

        using var correctSale = await SendAddItemAsync(cashier, approvedSaleId, BuildItemPayload(), token, "PROT-CROSSSALE-02");
        Assert.Equal(HttpStatusCode.OK, correctSale.StatusCode);
        Assert.Equal(1, await app.Db.SaleItems.CountAsync(saleItem => saleItem.SaleId == approvedSaleId));
    }

    [Fact]
    public async Task PriceOverride_ElevatedUserWithoutToken_Proceeds()
    {
        await using var app = await ProtectedActionTestApplication.CreateAsync();
        var saleId = await app.StartSaleAsync(CashierId);
        using var admin = app.CreateClient(AdminId, UserRole.Admin, "Admin Dos");

        using var response = await SendAddItemAsync(admin, saleId, BuildItemPayload(), token: null, idempotencyKey: "PROT-ELEVATED-01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = body.GetProperty("items")[0];
        Assert.Equal(CustomUsd, item.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(1, await app.Db.SaleItems.CountAsync(saleItem => saleItem.SaleId == saleId));
        Assert.Equal(0, await app.Db.AuthorizationRequests.CountAsync());
    }

    // ------------------------------------------- R10: alcance deliberado del PUT /{id}/items

    [Fact]
    public async Task UpdateSaleItems_NonElevatedWithCustomPrice_ReturnsRoleBasedForbiddenWithoutAuthorizationExtensions()
    {
        await using var app = await ProtectedActionTestApplication.CreateAsync();
        var saleId = await app.StartSaleAsync(CashierId);
        await app.MarkSaleOnHoldClaimedByAsync(saleId, CashierId);
        using var cashier = app.CreateClient(CashierId, UserRole.Cashier, "Cajero 70");

        using var response = await SendUpdateItemsAsync(cashier, saleId, BuildUpdateItemsPayload(), token: null);

        await AssertPutPriceOverrideForbiddenAsync(response);
        Assert.Equal(0, await app.Db.SaleItems.CountAsync(saleItem => saleItem.SaleId == saleId));
    }

    [Fact]
    public async Task UpdateSaleItems_NonElevatedWithAuthorizationTokenHeader_IgnoresTokenAndKeepsRoleBasedForbidden()
    {
        await using var app = await ProtectedActionTestApplication.CreateAsync();
        var saleId = await app.StartSaleAsync(CashierId);
        await app.MarkSaleOnHoldClaimedByAsync(saleId, CashierId);
        using var cashier = app.CreateClient(CashierId, UserRole.Cashier, "Cajero 70");

        // El PUT no implementa el flujo de token: el header se ignora y el 403 por roles persiste.
        using var response = await SendUpdateItemsAsync(cashier, saleId, BuildUpdateItemsPayload(), token: "token-no-vinculado-al-put");

        await AssertPutPriceOverrideForbiddenAsync(response);
        Assert.Equal(0, await app.Db.SaleItems.CountAsync(saleItem => saleItem.SaleId == saleId));
    }

    // ---------------------------------------------------------------- helpers

    private static object BuildItemPayload(
        decimal? customUsd = CustomUsd,
        decimal? customLocal = CustomLocal,
        int productId = ProductId,
        decimal quantity = Quantity)
        => new
        {
            productId,
            quantity,
            exchangeRate = ExchangeRate,
            customUnitPriceUsd = customUsd,
            customUnitPriceLocal = customLocal
        };

    private static RequestAuthorizationContract CreateContract(int saleId) => new()
    {
        SaleId = saleId,
        ProductId = ProductId,
        ProductName = "Cafe molido",
        Quantity = Quantity,
        CustomUnitPriceUsd = CustomUsd,
        CustomUnitPriceLocal = CustomLocal,
        Terminal = "Caja-01"
    };

    private static async Task<int> CreateRequestAsync(HttpClient cashier, int saleId)
    {
        using var response = await cashier.PostAsJsonAsync("/api/authorizations", CreateContract(saleId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("requestId").GetInt32();
    }

    private static async Task<string> ApproveAndRecoverTokenAsync(HttpClient cashier, HttpClient admin, int requestId)
    {
        using var resolve = await admin.PostAsJsonAsync(
            $"/api/authorizations/{requestId}/resolve",
            new { approved = true, reason = (string?)null });
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);

        using var status = await cashier.GetAsync($"/api/authorizations/{requestId}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var body = await status.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        return token!;
    }

    private static async Task<HttpResponseMessage> SendAddItemAsync(
        HttpClient client,
        int saleId,
        object payload,
        string? token,
        string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sales/{saleId}/items")
        {
            Content = JsonContent.Create(payload)
        };
        if (token is not null)
        {
            request.Headers.Add("X-Authorization-Token", token);
        }
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    private static async Task AssertPriceOverrideForbiddenAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ForbiddenMessage, body.GetProperty("detail").GetString());
        Assert.True(body.GetProperty("authorizationRequired").GetBoolean());
        Assert.Equal("ManualPriceOverride", body.GetProperty("authorizationAction").GetString());
    }

    private static object BuildUpdateItemsPayload() => new
    {
        items = new[]
        {
            new { productId = ProductId, quantity = Quantity, unitPrice = CustomUsd }
        }
    };

    private static async Task<HttpResponseMessage> SendUpdateItemsAsync(
        HttpClient client,
        int saleId,
        object payload,
        string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/sales/{saleId}/items")
        {
            Content = JsonContent.Create(payload)
        };
        if (token is not null)
        {
            request.Headers.Add("X-Authorization-Token", token);
        }

        return await client.SendAsync(request);
    }

    /// <summary>
    /// R10 (design D8): el PUT conserva el 403 por roles del repositorio — sin las extensiones
    /// authorizationRequired/authorizationAction del contrato de acciones protegidas.
    /// </summary>
    private static async Task AssertPutPriceOverrideForbiddenAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ExpectedPutPriceOverrideMessage, body.GetProperty("detail").GetString());
        Assert.False(body.TryGetProperty("authorizationRequired", out _));
        Assert.False(body.TryGetProperty("authorizationAction", out _));
    }

    // ---------------------------------------------------------------- infraestructura

    private sealed class ProtectedActionTestApplication : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly WebApplication _application;

        public SalesDbContext Db { get; }

        public SalesService SalesService { get; }

        public AuthorizationTokenService TokenService { get; }

        private ProtectedActionTestApplication(
            SqliteConnection connection,
            WebApplication application,
            SalesDbContext db,
            SalesService salesService,
            AuthorizationTokenService tokenService)
        {
            _connection = connection;
            _application = application;
            Db = db;
            SalesService = salesService;
            TokenService = tokenService;
        }

        public static async Task<ProtectedActionTestApplication> CreateAsync()
        {
            var (db, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
            await TestDatabaseFactory.SeedStandardSalesDataAsync(db);
            // Sales.CashierId tiene FK a Users: los solicitantes/elevados deben existir.
            db.Users.AddRange(CreateUser(CashierId, UserRole.Cashier), CreateUser(AdminId, UserRole.Admin));
            await db.SaveChangesAsync();

            var tokenService = new AuthorizationTokenService(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "JWT_SETTINGS_KEY", TestKey },
                    { "JwtSettings:Issuer", "SolucionesPos" },
                    { "JwtSettings:Audience", "PosClient" }
                }).Build(),
                TimeSpan.FromSeconds(60));

            var inventory = new Mock<IInventoryService>();
            inventory.Setup(service => service.GetSaleProductByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int productId, CancellationToken _) => CreateProductInfo(productId));
            inventory.Setup(service => service.GetSaleProductsByIdsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<int> productIds, CancellationToken _) => productIds.Select(CreateProductInfo).ToArray());
            inventory.Setup(service => service.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(0m);

            var mediator = new Mock<IMediator>();
            var cashDrawerService = new CashDrawerService(db);
            var settings = new Mock<ISystemSettingsService>();
            var salesService = new SalesService(
                db,
                inventory.Object,
                mediator.Object,
                cashDrawerService,
                settings.Object);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Services.AddControllers().AddApplicationPart(typeof(SalesController).Assembly);
            builder.Services.AddSignalR();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddAuthentication("TestAuth")
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>("TestAuth", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton(salesService);
            builder.Services.AddScoped<ISalesService>(_ => salesService);
            builder.Services.AddScoped<Core.Interfaces.ICurrentUserService, CurrentUserService>();
            builder.Services.AddScoped<Core.Interfaces.IIdempotencyService>(_ => new IdempotencyService(db));
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
            return new ProtectedActionTestApplication(connection, application, db, salesService, tokenService);
        }

        public async Task<int> StartSaleAsync(int cashierId)
        {
            var sale = await SalesService.StartSaleAsync(cashierId);
            return sale.Id;
        }

        /// <summary>
        /// R10: deja la venta en el estado real que exige el PUT (OnHold con reclamo del editor).
        /// Se escribe directo en la fila para no depender del flujo de hold (cliente real/hold).
        /// </summary>
        public async Task MarkSaleOnHoldClaimedByAsync(int saleId, int cashierId)
        {
            var sale = await Db.Sales.SingleAsync(candidate => candidate.Id == saleId);
            sale.Status = SaleStatus.OnHold;
            sale.ClaimedByUserId = cashierId;
            sale.ClaimedByUserName = $"Usuario {cashierId}";
            sale.ClaimAction = SaleClaimAction.Editing;
            sale.ClaimedAtUtc = DateTime.UtcNow;
            await Db.SaveChangesAsync();
        }

        private static SaleProductInfoDto CreateProductInfo(int productId) => new()
        {
            Id = productId,
            Name = $"Producto {productId}",
            IsActive = true,
            PriceUSD = 10m,
            PriceBsS = 100m,
            IsFractional = true
        };

        private static User CreateUser(int id, UserRole role) => new()
        {
            Id = id,
            Cedula = $"V-{id:D8}",
            Name = $"Usuario {id}",
            Username = $"usuario{id}",
            FullName = $"Usuario {id}",
            Role = role,
            PasswordHash = Core.Security.PasswordHasher.HashPassword("ClaveDePrueba123!")
        };

        public HttpClient CreateClient(int userId, UserRole role, string name)
        {
            var client = _application.GetTestServer().CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-User-Id", userId.ToString(CultureInfo.InvariantCulture));
            client.DefaultRequestHeaders.Add("X-Test-User-Role", role.ToString());
            client.DefaultRequestHeaders.Add("X-Test-User-Name", name);
            return client;
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
