using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Hubs;
using Backend.API.Services;
using Desktop.Client.Services;
using Microsoft.AspNetCore.SignalR;
using Sales.Module.DTOs;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.150 (T9): contrato y mapeos del cliente WPF del hub de autorizaciones. Sin red: el par REST
/// se ejercita con un handler determinista y el hub se desactiva con enableRealtime:false (el
/// transporte SignalR real no se abre en pruebas unitarias).
/// </summary>
public class AuthorizationHubServiceTests
{
    private static readonly Uri FakeBaseAddress = new("http://127.0.0.1:1/");

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public StubHttpMessageHandler(string body, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _body = body;
            _statusCode = statusCode;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }

    private static AuthorizationRequestContext NewContext() => new()
    {
        SaleId = 77,
        ProductId = 5,
        ProductName = "Café 1kg",
        Quantity = 2m,
        CustomUnitPriceUsd = 3.5m,
        CustomUnitPriceLocal = 35m,
        Terminal = "Caja-01"
    };

    private static AuthorizationHubService CreateRestOnlyService(StubHttpMessageHandler handler, UserSession? session = null)
        => new(new HttpClient(handler) { BaseAddress = FakeBaseAddress }, session, enableRealtime: false);

    // ── Contrato con el backend ───────────────────────────────────────────

    [Fact]
    public void HubEventNames_MatchBackendNotifierConstants()
    {
        Assert.Equal(SignalRAuthorizationNotifier.RequestedEvent, AuthorizationHubService.RequestedEvent);
        Assert.Equal(SignalRAuthorizationNotifier.ResolvedEvent, AuthorizationHubService.ResolvedEvent);
        Assert.Equal(SignalRAuthorizationNotifier.ExpiredEvent, AuthorizationHubService.ExpiredEvent);
    }

    [Fact]
    public void HubMethodNames_MatchBackendHubMethodNameAttributes()
    {
        var requestMethod = typeof(AuthorizationHub)
            .GetMethod(nameof(AuthorizationHub.RequestAuthorizationAsync))!
            .GetCustomAttribute<HubMethodNameAttribute>()!
            .Name;
        var resolveMethod = typeof(AuthorizationHub)
            .GetMethod(nameof(AuthorizationHub.ResolveAuthorizationAsync))!
            .GetCustomAttribute<HubMethodNameAttribute>()!
            .Name;

        Assert.Equal(AuthorizationHubService.RequestAuthorizationMethod, requestMethod);
        Assert.Equal(AuthorizationHubService.ResolveAuthorizationMethod, resolveMethod);
    }

    [Fact]
    public void BuildRequestPayload_UsesExactBackendContractKeysAndValues()
    {
        var payload = AuthorizationHubService.BuildRequestPayload(NewContext());

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;

        Assert.Equal(77, root.GetProperty("saleId").GetInt32());
        Assert.Equal(5, root.GetProperty("productId").GetInt32());
        Assert.Equal("Café 1kg", root.GetProperty("productName").GetString());
        Assert.Equal(2m, root.GetProperty("quantity").GetDecimal());
        Assert.Equal(3.5m, root.GetProperty("customUnitPriceUsd").GetDecimal());
        Assert.Equal(35m, root.GetProperty("customUnitPriceLocal").GetDecimal());
        Assert.Equal("Caja-01", root.GetProperty("terminal").GetString());
        Assert.Equal(7, root.EnumerateObject().Count());
    }

    [Fact]
    public void BuildRequestPayload_NullContext_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AuthorizationHubService.BuildRequestPayload(null!));
    }

    // ── Mapeo de payloads push del hub ────────────────────────────────────

    [Fact]
    public void AuthorizationResolvedPayload_DeserializesHubCamelCaseShape()
    {
        const string json = """
            {"requestId":42,"status":"Approved","approved":true,"resolvedByName":"Admin Uno","resolutionMode":"Remote","reason":null,"token":"tok-1"}
            """;

        var payload = JsonSerializer.Deserialize<AuthorizationResolvedPayload>(json)!;

        Assert.Equal(42, payload.RequestId);
        Assert.Equal("Approved", payload.Status);
        Assert.True(payload.Approved);
        Assert.Equal("Admin Uno", payload.ResolvedByName);
        Assert.Equal("Remote", payload.ResolutionMode);
        Assert.Null(payload.Reason);
        Assert.Equal("tok-1", payload.Token);
    }

    [Fact]
    public void AuthorizationRequestedPayload_DeserializesHubCamelCaseShape()
    {
        const string json = """
            {"requestId":42,"actionType":"ManualPriceOverride","saleId":77,"requestedByName":"Cajero 01","terminal":"Caja-01","context":{"productName":"Café 1kg"},"createdAt":"2026-10-08T12:00:00Z","expiresAt":"2026-10-08T12:01:00Z"}
            """;

        var payload = JsonSerializer.Deserialize<AuthorizationRequestedPayload>(json)!;

        Assert.Equal(42, payload.RequestId);
        Assert.Equal("ManualPriceOverride", payload.ActionType);
        Assert.Equal(77, payload.SaleId);
        Assert.Equal("Cajero 01", payload.RequestedByName);
        Assert.Equal("Caja-01", payload.Terminal);
        Assert.Equal("Café 1kg", payload.Context!.Value.GetProperty("productName").GetString());
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero), payload.CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 12, 1, 0, TimeSpan.Zero), payload.ExpiresAt);
    }

    [Fact]
    public void AuthorizationExpiredPayload_DeserializesHubCamelCaseShape()
    {
        const string json = """
            {"requestId":42,"status":"Expired","expiresAt":"2026-10-08T12:01:00Z","expiredAt":"2026-10-08T12:01:05Z"}
            """;

        var payload = JsonSerializer.Deserialize<AuthorizationExpiredPayload>(json)!;

        Assert.Equal(42, payload.RequestId);
        Assert.Equal("Expired", payload.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 12, 1, 0, TimeSpan.Zero), payload.ExpiresAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 12, 1, 5, TimeSpan.Zero), payload.ExpiredAt);
    }

    // ── Access token provider (patron ExchangeRateService) ────────────────

    [Fact]
    public async Task CreateAccessTokenProvider_ReturnsCurrentSessionToken()
    {
        var session = new UserSession();
        session.Token = "tok-abc";
        var provider = AuthorizationHubService.CreateAccessTokenProvider(new Uri("https://192.168.1.50:5001/"), session);

        Assert.Equal("tok-abc", await provider());
    }

    [Fact]
    public async Task CreateAccessTokenProvider_NonLoopbackHttp_ReturnsNullEvenWithSession()
    {
        var session = new UserSession();
        session.Token = "tok-abc";
        var provider = AuthorizationHubService.CreateAccessTokenProvider(new Uri("http://192.168.1.50:5000/"), session);

        Assert.Null(await provider());
    }

    [Fact]
    public async Task CreateAccessTokenProvider_LoopbackHttp_ReturnsSessionToken()
    {
        var session = new UserSession();
        session.Token = "tok-abc";
        var provider = AuthorizationHubService.CreateAccessTokenProvider(new Uri("http://localhost:5000/"), session);

        Assert.Equal("tok-abc", await provider());
    }

    [Fact]
    public async Task CreateAccessTokenProvider_WithoutSession_ReturnsNull()
    {
        var provider = AuthorizationHubService.CreateAccessTokenProvider(new Uri("https://192.168.1.50:5001/"), null);

        Assert.Null(await provider());
    }

    [Fact]
    public void CreateAccessTokenProvider_NullHubUri_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AuthorizationHubService.CreateAccessTokenProvider(null!, new UserSession()));
    }

    // ── Par REST: create / status / local-resolve ────────────────────────

    [Fact]
    public async Task RequestAuthorizationAsync_WithoutHubConnection_UsesRestPairAndMapsResult()
    {
        var handler = new StubHttpMessageHandler("""
            {"requestId":55,"actionType":"ManualPriceOverride","saleId":77,"status":"Pending","remainingLifetimeSeconds":60,"expiresAt":"2026-10-08T12:01:00Z","deduplicated":true}
            """);
        using var service = CreateRestOnlyService(handler);

        var info = await service.RequestAuthorizationAsync(NewContext());

        Assert.Equal(55, info.RequestId);
        Assert.True(info.Deduplicated);
        Assert.Equal(60, info.RemainingLifetimeSeconds);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 12, 1, 0, TimeSpan.Zero), info.ExpiresAt);

        var request = Assert.IsType<HttpRequestMessage>(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/authorizations", request.RequestUri!.AbsolutePath);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(77, body.RootElement.GetProperty("saleId").GetInt32());
        Assert.Equal("Café 1kg", body.RootElement.GetProperty("productName").GetString());
    }

    [Fact]
    public async Task RequestAuthorizationAsync_RestFailure_ThrowsWithServerMessageAndStatus()
    {
        var handler = new StubHttpMessageHandler(
            """{"message":"Los usuarios con rol de Administrador o Supervisor no requieren autorización."}""",
            HttpStatusCode.Conflict);
        using var service = CreateRestOnlyService(handler);

        var exception = await Assert.ThrowsAsync<AuthorizationHubException>(() => service.RequestAuthorizationAsync(NewContext()));

        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("Los usuarios con rol de Administrador o Supervisor no requieren autorización.", exception.Message);
    }

    [Fact]
    public async Task ResolveAuthorizationAsync_WithoutHubConnection_UsesRestPairAndMapsResult()
    {
        var handler = new StubHttpMessageHandler("""
            {"requestId":55,"status":"Approved","approved":true,"resolvedByName":"Admin Uno","resolutionMode":"Remote","reason":null}
            """);
        using var service = CreateRestOnlyService(handler);

        var result = await service.ResolveAuthorizationAsync(55, approved: true, reason: null);

        Assert.Equal("Approved", result.Status);
        Assert.Equal("Admin Uno", result.ResolvedByName);
        Assert.Null(result.Reason);

        var request = Assert.IsType<HttpRequestMessage>(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/authorizations/55/resolve", request.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.True(body.RootElement.GetProperty("approved").GetBoolean());
    }

    [Fact]
    public async Task ResolveAuthorizationAsync_RaceFailure_ThrowsExactRaceMessage()
    {
        var handler = new StubHttpMessageHandler(
            """{"message":"Esta solicitud ya fue resuelta por Admin Dos."}""",
            HttpStatusCode.Conflict);
        using var service = CreateRestOnlyService(handler);

        var exception = await Assert.ThrowsAsync<AuthorizationHubException>(() => service.ResolveAuthorizationAsync(55, approved: false, reason: "no aplica"));

        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("Esta solicitud ya fue resuelta por Admin Dos.", exception.Message);
    }

    [Fact]
    public async Task GetStatusAsync_MapsControllerPayloadAndTargetsRoute()
    {
        var handler = new StubHttpMessageHandler("""
            {"id":55,"actionType":"ManualPriceOverride","saleId":77,"requestedByUserId":3,"requestedByName":"Cajero 01","terminal":"Caja-01","status":"Approved","resolutionMode":"Remote","resolvedByUserId":9,"resolvedByName":"Admin Uno","reason":"Precio acordado","contextJson":"{\"productName\":\"Café 1kg\"}","createdAt":"2026-10-08T12:00:00Z","expiresAt":"2026-10-08T12:01:00Z","resolvedAt":"2026-10-08T12:00:30Z","consumedAt":null,"remainingLifetimeSeconds":30,"token":"tok-1"}
            """);
        using var service = CreateRestOnlyService(handler);

        var status = await service.GetStatusAsync(55);

        Assert.NotNull(status);
        Assert.Equal(55, status!.Id);
        Assert.Equal("Approved", status.Status);
        Assert.True(status.Approved);
        Assert.Equal("Remote", status.ResolutionMode);
        Assert.Equal("Admin Uno", status.ResolvedByName);
        Assert.Equal("Precio acordado", status.Reason);
        Assert.Equal("tok-1", status.Token);
        Assert.Equal(30, status.RemainingLifetimeSeconds);

        var request = Assert.IsType<HttpRequestMessage>(handler.LastRequest);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/authorizations/55", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetStatusAsync_NotFound_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler(
            """{"message":"La solicitud de autorización no existe."}""",
            HttpStatusCode.NotFound);
        using var service = CreateRestOnlyService(handler);

        var status = await service.GetStatusAsync(55);

        Assert.Null(status);
    }

    [Fact]
    public async Task LocalResolveAsync_PostsCredentialsAndMapsToken()
    {
        var handler = new StubHttpMessageHandler("""
            {"token":"local-tok","supervisorUserId":9,"supervisorName":"Supervisora","status":"Approved"}
            """);
        using var service = CreateRestOnlyService(handler);

        var result = await service.LocalResolveAsync(55, "supervisora", "clave-buena", reason: null);

        Assert.Equal("local-tok", result.Token);
        Assert.Equal(9, result.SupervisorUserId);
        Assert.Equal("Supervisora", result.SupervisorName);
        Assert.Equal("Approved", result.Status);

        var request = Assert.IsType<HttpRequestMessage>(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/authorizations/55/local-resolve", request.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("supervisora", body.RootElement.GetProperty("username").GetString());
        Assert.Equal("clave-buena", body.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task LocalResolveAsync_InvalidCredentials_ThrowsExactMessage()
    {
        var handler = new StubHttpMessageHandler(
            """{"message":"Credenciales inválidas o sin privilegios para autorizar."}""",
            HttpStatusCode.BadRequest);
        using var service = CreateRestOnlyService(handler);

        var exception = await Assert.ThrowsAsync<AuthorizationHubException>(() => service.LocalResolveAsync(55, "x", "y"));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(AuthorizationMessages.InvalidCredentials, exception.Message);
        Assert.Equal("Credenciales inválidas o sin privilegios para autorizar.", exception.Message);
    }

    // ── Cancelacion del solicitante (W3, R4-client) ──────────────────────

    [Fact]
    public async Task CancelRequestAsync_PostsBodylessToCancelRouteAndTreatsSuccess()
    {
        var handler = new StubHttpMessageHandler("""
            {"requestId":55,"status":"Cancelled","resolvedAt":"2026-10-08T12:00:30Z"}
            """);
        using var service = CreateRestOnlyService(handler);

        var exception = await Record.ExceptionAsync(() => service.CancelRequestAsync(55));

        Assert.Null(exception);
        var request = Assert.IsType<HttpRequestMessage>(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/authorizations/55/cancel", request.RequestUri!.AbsolutePath);
        Assert.Null(handler.LastRequestBody);
    }

    [Fact]
    public async Task CancelRequestAsync_RaceFailure_ThrowsWithServerMessageAndStatus()
    {
        var handler = new StubHttpMessageHandler(
            """{"message":"Esta solicitud ya fue resuelta por Admin Dos."}""",
            HttpStatusCode.Conflict);
        using var service = CreateRestOnlyService(handler);

        var exception = await Assert.ThrowsAsync<AuthorizationHubException>(() => service.CancelRequestAsync(55));

        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("Esta solicitud ya fue resuelta por Admin Dos.", exception.Message);
    }

    [Fact]
    public async Task CancelRequestAsync_ForbiddenFailure_ThrowsWithStatusAndMessage()
    {
        var handler = new StubHttpMessageHandler(
            """{"message":"Solo el solicitante puede cancelar la solicitud."}""",
            HttpStatusCode.Forbidden);
        using var service = CreateRestOnlyService(handler);

        var exception = await Assert.ThrowsAsync<AuthorizationHubException>(() => service.CancelRequestAsync(55));

        Assert.Equal(403, exception.StatusCode);
        Assert.Equal("Solo el solicitante puede cancelar la solicitud.", exception.Message);
    }

    // ── Ciclo de vida ─────────────────────────────────────────────────────

    [Fact]
    public void Dispose_MultipleCalls_AreIdempotentAndDoNotThrow()
    {
        using var httpClient = new HttpClient { BaseAddress = FakeBaseAddress };
        var service = new AuthorizationHubService(httpClient, enableRealtime: false);

        Assert.Null(Record.Exception(() => service.Dispose()));
        Assert.Null(Record.Exception(() => service.Dispose()));
        Assert.Null(Record.Exception(() => service.Dispose()));
    }

    [Fact]
    public async Task DisposeAsync_MultipleCalls_AreIdempotentAndDoNotThrow()
    {
        using var httpClient = new HttpClient { BaseAddress = FakeBaseAddress };
        var service = new AuthorizationHubService(httpClient, enableRealtime: false);

        Assert.Null(await Record.ExceptionAsync(async () => await service.DisposeAsync()));
        Assert.Null(await Record.ExceptionAsync(async () => await service.DisposeAsync()));
        Assert.Null(await Record.ExceptionAsync(async () => await service.DisposeAsync()));
    }

    [Fact]
    public async Task Dispose_FollowedByDisposeAsync_DoesNotThrow()
    {
        using var httpClient = new HttpClient { BaseAddress = FakeBaseAddress };
        var service = new AuthorizationHubService(httpClient, enableRealtime: false);

        Assert.Null(Record.Exception(() => service.Dispose()));
        Assert.Null(await Record.ExceptionAsync(async () => await service.DisposeAsync()));
    }

    [Fact]
    public async Task DisposeAsync_FollowedByDispose_DoesNotThrow()
    {
        using var httpClient = new HttpClient { BaseAddress = FakeBaseAddress };
        var service = new AuthorizationHubService(httpClient, enableRealtime: false);

        Assert.Null(await Record.ExceptionAsync(async () => await service.DisposeAsync()));
        Assert.Null(Record.Exception(() => service.Dispose()));
    }

    // ── AddItemAsync: header del token de autorizacion ────────────────────

    [Fact]
    public async Task SalesService_AddItemAsync_SendsAuthorizationTokenHeader_WhenProvided()
    {
        var handler = new StubHttpMessageHandler("""{"id":1,"items":[]}""");
        using var client = new HttpClient(handler) { BaseAddress = FakeBaseAddress };
        var sales = new SalesService(client);

        await sales.AddItemAsync(1, 2, 1m, 10m, null, null, "tok-123");

        var request = Assert.IsType<HttpRequestMessage>(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/sales/1/items", request.RequestUri!.AbsolutePath);
        Assert.True(request.Headers.TryGetValues("X-Authorization-Token", out var values));
        Assert.Equal("tok-123", Assert.Single(values));
        Assert.False(string.IsNullOrWhiteSpace(request.Headers.GetValues("Idempotency-Key").Single()));
    }

    [Fact]
    public async Task SalesService_AddItemAsync_OmitsAuthorizationTokenHeader_WhenNotProvided()
    {
        var handler = new StubHttpMessageHandler("""{"id":1,"items":[]}""");
        using var client = new HttpClient(handler) { BaseAddress = FakeBaseAddress };
        var sales = new SalesService(client);

        await sales.AddItemAsync(1, 2, 1m, 10m);

        var request = Assert.IsType<HttpRequestMessage>(handler.LastRequest);
        Assert.False(request.Headers.Contains("X-Authorization-Token"));
    }
}
