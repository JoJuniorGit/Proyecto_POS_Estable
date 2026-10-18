using System;
using System.Collections.Generic;
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
/// T4 (Paso 13 / 8.188, S1/S4/S5/S7): contrato HTTP del cliente WPF de caja chica. El backend
/// resuelve la tasa y la valoración dual; el cliente solo envía el nominal, la categoría, el
/// soporte documental y el token opcional de autorización. El 403 se parsea a una excepción
/// tipada con el contexto exacto (sesión, moneda, montos duales y categoría).
/// </summary>
public class PettyCashWpfClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }

    private static (CashDrawerService Service, RecordingHandler Handler) CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new RecordingHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return (new CashDrawerService(http), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Empty(HttpStatusCode status) => new(status);

    private static PettyCashExpenseClientRequest NewRequest() => new()
    {
        Currency = PettyCashCurrencyClient.Usd,
        Amount = 25.5m,
        Category = PettyCashCategoryClient.MantenimientoReparaciones,
        Concept = "Reparación de vitrina",
        Beneficiary = "Ferretería Central",
        ReferenceNumber = "FAC-0091",
        IdempotencyKey = "idem-8188"
    };

    private const string ExpenseJson = """
        {
          "id": 7,
          "voucherNumber": 1,
          "sessionId": 3,
          "category": 2,
          "currency": 1,
          "amountBsS": 1275.00,
          "amountUsd": 25.5000,
          "historicalRate": 50.0000,
          "concept": "Reparación de vitrina",
          "beneficiary": "Ferretería Central",
          "referenceNumber": "FAC-0091",
          "createdByUserId": 4,
          "createdAtUtc": "2026-10-18T14:00:00Z",
          "authorizedByUserId": 9,
          "authorizationRequestId": 12,
          "status": 0,
          "cashTransactionId": 88,
          "isReplay": false
        }
        """;

    [Fact]
    public async Task RegisterPettyCashExpense_PostsBodyAndOptionalAuthorizationHeader()
    {
        var (service, handler) = CreateService(_ => Json(HttpStatusCode.OK, ExpenseJson));

        var result = await service.RegisterPettyCashExpenseAsync(NewRequest(), authorizationToken: "tok-1");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/cashdrawer/petty-cash", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("tok-1", Assert.Single(handler.LastRequest.Headers.GetValues("X-Authorization-Token")));
        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;
        Assert.Equal((int)PettyCashCurrencyClient.Usd, root.GetProperty("currency").GetInt32());
        Assert.Equal(25.5m, root.GetProperty("amount").GetDecimal());
        Assert.Equal((int)PettyCashCategoryClient.MantenimientoReparaciones, root.GetProperty("category").GetInt32());
        Assert.Equal("Reparación de vitrina", root.GetProperty("concept").GetString());
        Assert.Equal("Ferretería Central", root.GetProperty("beneficiary").GetString());
        Assert.Equal("FAC-0091", root.GetProperty("referenceNumber").GetString());
        Assert.Equal("idem-8188", root.GetProperty("idempotencyKey").GetString());

        Assert.Equal(7, result.Id);
        Assert.Equal("VCC-000001", result.VoucherCode);
        Assert.Equal(PettyCashCurrencyClient.Usd, result.Currency);
        Assert.Equal(PettyCashStatusClient.Active, result.Status);
        Assert.Equal(1275.00m, result.AmountBsS);
        Assert.Equal(25.5000m, result.AmountUsd);
    }

    [Fact]
    public async Task RegisterPettyCashExpense_WithoutToken_DoesNotSendAuthorizationHeader()
    {
        var (service, handler) = CreateService(_ => Json(HttpStatusCode.OK, ExpenseJson));

        await service.RegisterPettyCashExpenseAsync(NewRequest());

        Assert.False(handler.LastRequest!.Headers.Contains("X-Authorization-Token"));
    }

    [Fact]
    public async Task RegisterPettyCashExpense_Authorization403_ThrowsTypedExceptionWithExactContext()
    {
        var (service, _) = CreateService(_ => Json(HttpStatusCode.Forbidden, """
            {
              "title": "Acceso denegado",
              "detail": "Se requiere autorización de un Administrador o Supervisor.",
              "authorizationRequired": true,
              "authorizationAction": "PettyCashExpense",
              "authorizationContext": {
                "sessionId": 3,
                "currency": 1,
                "amountBss": 1275.00,
                "amountUsd": 25.5000,
                "category": 2
              }
            }
            """));

        var ex = await Assert.ThrowsAsync<PettyCashAuthorizationException>(() =>
            service.RegisterPettyCashExpenseAsync(NewRequest()));

        Assert.Equal("PettyCashExpense", ex.AuthorizationAction);
        Assert.Equal(3, ex.Context.SessionId);
        Assert.Equal(PettyCashCurrencyClient.Usd, ex.Context.Currency);
        Assert.Equal(1275.00m, ex.Context.AmountBsS);
        Assert.Equal(25.5000m, ex.Context.AmountUsd);
        Assert.Equal(PettyCashCategoryClient.MantenimientoReparaciones, ex.Context.Category);
        Assert.Contains("autorización", ex.Message);
    }

    [Fact]
    public async Task RegisterPettyCashExpense_403WithOtherAction_DegradesToGenericAuthorizationException()
    {
        var (service, _) = CreateService(_ => Json(HttpStatusCode.Forbidden, """
            {
              "detail": "Precio manual requiere autorización.",
              "authorizationRequired": true,
              "authorizationAction": "ManualPriceOverride"
            }
            """));

        var ex = await Assert.ThrowsAsync<AuthorizationRequiredException>(() =>
            service.RegisterPettyCashExpenseAsync(NewRequest()));

        Assert.Equal("ManualPriceOverride", ex.AuthorizationAction);
    }

    [Fact]
    public async Task CancelPettyCashExpense_PostsReasonAndToken()
    {
        var (service, handler) = CreateService(_ => Json(HttpStatusCode.OK, ExpenseJson));

        await service.CancelPettyCashExpenseAsync(7, "Gasto duplicado", authorizationToken: "tok-2");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/cashdrawer/petty-cash/7/cancel", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("tok-2", Assert.Single(handler.LastRequest.Headers.GetValues("X-Authorization-Token")));
        Assert.Contains("\"reason\":\"Gasto duplicado\"", handler.LastBody);
    }

    [Fact]
    public async Task GetPettyCashExpenses_UsesSessionQueryAndParsesRows()
    {
        var (service, handler) = CreateService(_ => Json(HttpStatusCode.OK, $"[{ExpenseJson}]"));

        var rows = await service.GetPettyCashExpensesAsync(3);

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/api/cashdrawer/petty-cash", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("?sessionId=3", handler.LastRequest.RequestUri.Query);
        Assert.Single(rows);
        Assert.Equal("VCC-000001", rows[0].VoucherCode);
    }

    [Fact]
    public async Task GetPettyCashVoucher_ReturnsPdfBytesOrNullOnNotFound()
    {
        var (ok, okHandler) = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46 })
        });

        var bytes = await ok.GetPettyCashVoucherAsync(7);

        Assert.Equal("/api/cashdrawer/petty-cash/7/receipt", okHandler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46 }, bytes);

        var (notFound, _) = CreateService(_ => Empty(HttpStatusCode.NotFound));
        Assert.Null(await notFound.GetPettyCashVoucherAsync(7));
    }

    [Fact]
    public async Task GetPhysicalBalances_ReturnsPerCurrencyBalances()
    {
        var (service, handler) = CreateService(_ => Json(HttpStatusCode.OK, """{"bsS":1500.25,"usd":32.5000}"""));

        var balances = await service.GetPhysicalBalancesAsync(3);

        Assert.Equal("/api/cashdrawer/physical-balances", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("?sessionId=3", handler.LastRequest.RequestUri.Query);
        Assert.Equal(1500.25m, balances.BsS);
        Assert.Equal(32.5000m, balances.Usd);
    }

    [Fact]
    public async Task CloseSession_WithOptionalUsd_SendsFieldOnlyWhenProvided()
    {
        var sessionJson = """
            {"id":1,"status":1,"openingBalanceLocal":500.00,"openingExchangeRate":50.00,"transactions":[]}
            """;

        var (withUsd, withUsdHandler) = CreateService(_ => Json(HttpStatusCode.OK, sessionJson));
        await withUsd.CloseSessionAsync(700m, 50m, 25.5m);
        Assert.Equal("/api/cashdrawer/close", withUsdHandler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("\"actualClosingBalanceUsd\":25.5", withUsdHandler.LastBody);

        var (withoutUsd, withoutUsdHandler) = CreateService(_ => Json(HttpStatusCode.OK, sessionJson));
        await withoutUsd.CloseSessionAsync(700m, 50m);
        Assert.DoesNotContain("actualClosingBalanceUsd", withoutUsdHandler.LastBody);
    }

    [Fact]
    public void BuildRequestPayload_PettyCash_CarriesNumericActionAndExactContextWithoutSale()
    {
        var context = new PettyCashExpenseAuthorizationRequestContext
        {
            SessionId = 3,
            Currency = PettyCashCurrencyClient.Usd,
            AmountBsS = 1275.00m,
            AmountUsd = 25.5000m,
            Category = PettyCashCategoryClient.MantenimientoReparaciones,
            Terminal = "Caja-04"
        };

        var payload = AuthorizationHubService.BuildRequestPayload(context);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;

        Assert.Equal((int)AuthorizationActionType.PettyCashExpense, root.GetProperty("actionType").GetInt32());
        Assert.Equal(3, root.GetProperty("pettyCashSessionId").GetInt32());
        Assert.Equal((int)PettyCashCurrencyClient.Usd, root.GetProperty("pettyCashCurrency").GetInt32());
        Assert.Equal(1275.00m, root.GetProperty("pettyCashAmountBsS").GetDecimal());
        Assert.Equal(25.5000m, root.GetProperty("pettyCashAmountUsd").GetDecimal());
        Assert.Equal((int)PettyCashCategoryClient.MantenimientoReparaciones, root.GetProperty("pettyCashCategory").GetInt32());
        Assert.Equal("Caja-04", root.GetProperty("terminal").GetString());
        Assert.Equal(7, root.EnumerateObject().Count());
        Assert.False(root.TryGetProperty("saleId", out _));
        Assert.False(root.TryGetProperty("productId", out _));
    }

    [Fact]
    public void AuthorizationRequestedPayload_ParsesPettyCashContext()
    {
        const string json = """
            {"requestId":42,"actionType":"PettyCashExpense","saleId":null,"requestedByName":"Cajero 01","terminal":"Caja-04","context":{"sessionId":3,"currency":1,"amountBsS":1275.00,"amountUsd":25.5000,"category":2},"createdAt":"2026-10-18T14:00:00Z","expiresAt":"2026-10-18T14:01:00Z"}
            """;

        var payload = JsonSerializer.Deserialize<AuthorizationRequestedPayload>(json)!;

        Assert.Equal(AuthorizationActionType.PettyCashExpense, payload.ParsedActionType);
        var context = payload.GetPettyCashExpenseContext();
        Assert.NotNull(context);
        Assert.Equal(3, context!.SessionId);
        Assert.Equal(PettyCashCurrencyClient.Usd, context.Currency);
        Assert.Equal(1275.00m, context.AmountBsS);
        Assert.Equal(25.5000m, context.AmountUsd);
        Assert.Equal(PettyCashCategoryClient.MantenimientoReparaciones, context.Category);
        Assert.Equal("Caja-04", context.Terminal);
    }

    [Fact]
    public void AuthorizationRequestedPayload_ManualAction_DoesNotProducePettyCashContext()
    {
        const string json = """
            {"requestId":42,"actionType":"ManualPriceOverride","saleId":77,"context":{"productName":"Café 1kg"}}
            """;

        var payload = JsonSerializer.Deserialize<AuthorizationRequestedPayload>(json)!;

        Assert.Null(payload.GetPettyCashExpenseContext());
    }

    [Fact]
    public void AuthorizationWaitViewModel_PettyCash_ShowsSessionCategoryAndDualAmountsWithoutBreakingManual()
    {
        var hub = new Moq.Mock<IAuthorizationHubService>();
        using var pettyCash = new Desktop.Client.ViewModels.AuthorizationWaitViewModel(
            hub.Object,
            new PettyCashExpenseAuthorizationRequestContext
            {
                SessionId = 3,
                Currency = PettyCashCurrencyClient.BsS,
                AmountBsS = 1000m,
                AmountUsd = 20m,
                Category = PettyCashCategoryClient.InsumosLimpieza
            },
            _ => Task.CompletedTask);

        Assert.Equal("Sesión", pettyCash.ReferenceLabel);
        Assert.Equal("#3", pettyCash.SaleNumberDisplay);
        Assert.Equal("Insumos y Limpieza", pettyCash.PettyCashCategoryDisplay);
        Assert.Equal(1000m, pettyCash.PettyCashAmountBsS);
        Assert.Equal(20m, pettyCash.PettyCashAmountUsd);
        Assert.Contains("20", pettyCash.PettyCashAmountDisplay);
        Assert.Contains("Caja chica", pettyCash.OperationSummary);

        using var manual = new Desktop.Client.ViewModels.AuthorizationWaitViewModel(
            hub.Object,
            new AuthorizationRequestContext { SaleId = 77, ProductId = 5, ProductName = "Café" },
            _ => Task.CompletedTask);

        Assert.Equal("Factura", manual.ReferenceLabel);
        Assert.Equal("#77", manual.SaleNumberDisplay);
        Assert.Empty(manual.PettyCashCategoryDisplay);
        Assert.Empty(manual.PettyCashAmountDisplay);
    }
}
