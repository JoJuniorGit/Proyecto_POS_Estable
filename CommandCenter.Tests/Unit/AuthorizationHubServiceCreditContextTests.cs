using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T4-prep (Paso 11/12, S4): contexto discriminado CreditOverLimit del cliente WPF. El payload
/// ManualPriceOverride conserva byte a byte sus siete claves históricas; el sobregiro agrega
/// actionType numérico + cliente/montos para que el hub del backend ligue el token al mismo hash.
/// </summary>
public class AuthorizationHubServiceCreditContextTests
{
    private static readonly Uri FakeBaseAddress = new("http://127.0.0.1:1/");

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _body;

        public StubHttpMessageHandler(string body)
        {
            _body = body;
        }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }

    private static CreditOverLimitAuthorizationRequestContext NewCreditContext() => new()
    {
        SaleId = 501,
        CustomerId = 55,
        CustomerName = "Maria Perez",
        FinancedUSD = 100m,
        AvailableCreditUSD = 50m,
        Terminal = "Caja-03"
    };

    [Fact]
    public void BuildRequestPayload_CreditOverLimit_CarriesNumericActionTypeAndExactContext()
    {
        var payload = AuthorizationHubService.BuildRequestPayload(NewCreditContext());

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;

        Assert.Equal(501, root.GetProperty("saleId").GetInt32());
        Assert.Equal((int)AuthorizationActionType.CreditOverLimit, root.GetProperty("actionType").GetInt32());
        Assert.Equal(55, root.GetProperty("customerId").GetInt32());
        Assert.Equal("Maria Perez", root.GetProperty("customerName").GetString());
        Assert.Equal(100m, root.GetProperty("financedUSD").GetDecimal());
        Assert.Equal(50m, root.GetProperty("availableCreditUSD").GetDecimal());
        Assert.Equal("Caja-03", root.GetProperty("terminal").GetString());
        Assert.Equal(7, root.EnumerateObject().Count());
        Assert.False(root.TryGetProperty("productId", out _));
    }

    [Fact]
    public void BuildRequestPayload_ManualPriceOverride_KeepsLegacySevenKeyPayloadWithoutActionType()
    {
        var payload = AuthorizationHubService.BuildRequestPayload(new AuthorizationRequestContext
        {
            SaleId = 77,
            ProductId = 5,
            ProductName = "Café 1kg",
            Quantity = 2m,
            CustomUnitPriceUsd = 3.5m,
            CustomUnitPriceLocal = 35m,
            Terminal = "Caja-01"
        });

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;

        Assert.Equal(7, root.EnumerateObject().Count());
        Assert.False(root.TryGetProperty("actionType", out _));
        Assert.Equal(77, root.GetProperty("saleId").GetInt32());
        Assert.Equal(5, root.GetProperty("productId").GetInt32());
    }

    [Fact]
    public async Task RequestAuthorizationAsync_CreditOverLimit_PostsNumericActionTypeOverRest()
    {
        var handler = new StubHttpMessageHandler("""
            {"requestId":66,"status":"Pending","remainingLifetimeSeconds":60,"expiresAt":"2026-10-18T12:01:00Z"}
            """);
        using var httpClient = new HttpClient(handler) { BaseAddress = FakeBaseAddress };
        using var service = new AuthorizationHubService(httpClient, enableRealtime: false);

        var info = await service.RequestAuthorizationAsync(NewCreditContext());

        Assert.Equal(66, info.RequestId);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal((int)AuthorizationActionType.CreditOverLimit, body.RootElement.GetProperty("actionType").GetInt32());
        Assert.Equal(55, body.RootElement.GetProperty("customerId").GetInt32());
        Assert.Equal(100m, body.RootElement.GetProperty("financedUSD").GetDecimal());
        Assert.Equal(50m, body.RootElement.GetProperty("availableCreditUSD").GetDecimal());
    }

    [Fact]
    public void AuthorizationRequestedPayload_ParsesCreditOverLimitContext()
    {
        const string json = """
            {"requestId":42,"actionType":"CreditOverLimit","saleId":501,"requestedByName":"Cajero 01","terminal":"Caja-03","context":{"saleId":501,"customerId":55,"customerName":"Maria Perez","financedUSD":100,"availableCreditUSD":50},"createdAt":"2026-10-18T12:00:00Z","expiresAt":"2026-10-18T12:01:00Z"}
            """;

        var payload = JsonSerializer.Deserialize<AuthorizationRequestedPayload>(json)!;

        Assert.Equal(AuthorizationActionType.CreditOverLimit, payload.ParsedActionType);
        var context = payload.GetCreditOverLimitContext();
        Assert.NotNull(context);
        Assert.Equal(55, context!.CustomerId);
        Assert.Equal(501, context.SaleId);
        Assert.Equal("Maria Perez", context.CustomerName);
        Assert.Equal(100m, context.FinancedUSD);
        Assert.Equal(50m, context.AvailableCreditUSD);
        Assert.Equal("Caja-03", context.Terminal);
    }

    [Fact]
    public void AuthorizationRequestedPayload_ManualPriceOverride_DoesNotProduceCreditContext()
    {
        const string json = """
            {"requestId":42,"actionType":"ManualPriceOverride","saleId":77,"context":{"productName":"Café 1kg"}}
            """;

        var payload = JsonSerializer.Deserialize<AuthorizationRequestedPayload>(json)!;

        Assert.Equal(AuthorizationActionType.ManualPriceOverride, payload.ParsedActionType);
        Assert.Null(payload.GetCreditOverLimitContext());
    }

    [Fact]
    public void AuthorizationRequestedPayload_MalformedCreditContext_ReturnsNull()
    {
        const string json = """
            {"requestId":42,"actionType":"CreditOverLimit","saleId":501,"context":{"customerId":55}}
            """;

        var payload = JsonSerializer.Deserialize<AuthorizationRequestedPayload>(json)!;

        Assert.Null(payload.GetCreditOverLimitContext());
    }
}
