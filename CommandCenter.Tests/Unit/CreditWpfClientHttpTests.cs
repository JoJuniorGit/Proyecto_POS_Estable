using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T4 (F3, Paso 11/12): contrato HTTP del cliente WPF de crédito CxC. La tasa del abono la
/// resuelve el servidor y el 403 de sobregiro llega como ProblemDetails con el contexto exacto
/// que el hub debe reproducir para ligar el token de autorización.
/// </summary>
public class CreditWpfClientHttpTests
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

    private static (SalesService Service, RecordingHandler Handler) CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new RecordingHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return (new SalesService(http), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task GetCreditProfile_ParsesStatusAvailableAndMora()
    {
        var (service, handler) = CreateService(_ => Json(HttpStatusCode.OK, """
            {
              "customerId": 7,
              "customerName": "Ana Pérez",
              "isActive": true,
              "isCreditEnabled": true,
              "creditStatus": 2,
              "creditLimitUSD": 500.00,
              "currentDebtUSD": 120.50,
              "availableCreditUSD": 379.50,
              "utilizationPercent": 24.10,
              "paymentTermDays": 30,
              "creditGraceDays": 5,
              "overdueUSD": 20.00,
              "overdueCount": 1
            }
            """));

        var profile = await service.GetCreditProfileAsync(7);

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/api/sales/customers/7/credit-profile", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal(7, profile.CustomerId);
        Assert.Equal(379.50m, profile.AvailableCreditUSD);
        Assert.Equal(1, profile.OverdueCount);
        Assert.Equal(Core.Entities.CreditStatus.ManuallyBlocked, profile.Status);
        Assert.Contains("Bloqueado", profile.StatusLabel);
    }

    [Fact]
    public async Task UpdateCreditProfile_SendsPutWithProfilePayload()
    {
        var (service, handler) = CreateService(_ => Json(HttpStatusCode.OK, """
            { "customerId": 7, "isCreditEnabled": true, "creditStatus": 0, "creditLimitUSD": 800.00,
              "paymentTermDays": 45, "creditGraceDays": 10 }
            """));

        await service.UpdateCreditProfileAsync(7, new CreditProfileUpdateClientDto
        {
            IsCreditEnabled = true,
            CreditLimitUSD = 800m,
            PaymentTermDays = 45,
            CreditGraceDays = 10,
            CreditNotes = "Acuerdo especial",
            Status = Core.Entities.CreditStatus.Active
        });

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal("/api/sales/customers/7/credit-profile", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("\"isCreditEnabled\":true", handler.LastBody);
        Assert.Contains("\"creditLimitUSD\":800", handler.LastBody);
        Assert.Contains("\"paymentTermDays\":45", handler.LastBody);
        Assert.Contains("\"creditGraceDays\":10", handler.LastBody);
        Assert.Contains("\"creditStatus\":0", handler.LastBody);
        Assert.Contains("Acuerdo especial", handler.LastBody);
    }

    [Fact]
    public async Task GetCreditReceivables_BuildsFilterQueryString()
    {
        var (service, handler) = CreateService(_ => Json(HttpStatusCode.OK, """
            { "items": [], "totalOpenUSD": 0, "totalOverdueUSD": 0, "openCount": 0, "todayRate": 36.50 }
            """));

        await service.GetCreditReceivablesAsync(new CreditReceivablesFilterClientDto
        {
            CustomerId = 7,
            Status = "Vencida",
            OverdueOnly = true,
            Search = "Ana Pérez"
        });

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("customerId=7", query);
        Assert.Contains("status=Vencida", query);
        Assert.Contains("overdueOnly=true", query);
        Assert.Contains("search=Ana%20P%C3%A9rez", query);
    }

    [Fact]
    public async Task GetCreditStatement_DeserializesDualBalanceAndPaymentHistory()
    {
        var (service, _) = CreateService(_ => Json(HttpStatusCode.OK, """
            {
              "profile": { "customerId": 7, "customerName": "Ana Pérez", "isCreditEnabled": true, "creditStatus": 0 },
              "todayRate": 40.00,
              "currentDebtTodayBsS": 4000.00,
              "receivables": [
                { "receivableId": 1, "saleId": 10, "invoiceNumber": 12, "originalUSD": 100.00,
                  "balanceUSD": 100.00, "originalBsS": 3600.00, "historicalRate": 36.00,
                  "balanceTodayBsS": 4000.00, "status": "Vigente", "isOverdue": false }
              ],
              "payments": [
                { "paymentId": 5, "amountUSD": 20.00, "amountBsS": 800.00, "paymentRate": 40.00,
                  "fxDifferenceBsS": 80.00, "paymentMethodName": "Efectivo" }
              ]
            }
            """));

        var statement = await service.GetCreditStatementAsync(7);

        Assert.Equal(40.00m, statement.TodayRate);
        Assert.Single(statement.Receivables);
        Assert.Equal(4000.00m, statement.Receivables[0].BalanceTodayBsS);
        Assert.Single(statement.Payments);
        Assert.Equal(80.00m, statement.Payments[0].FxDifferenceBsS);
    }

    [Fact]
    public async Task CompleteCreditSale_WithAuthorizationToken_SendsHeaderAndBody()
    {
        var (service, handler) = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("321", Encoding.UTF8, "application/json")
        });

        var invoice = await service.CompleteCreditSaleAsync(
            9,
            36.50m,
            new List<SalePaymentDto> { new(1, 10m, 365m, null) },
            cashierId: 4,
            authorizationToken: "tok-1");

        Assert.Equal(321, invoice);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/sales/9/complete-credit", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("tok-1", Assert.Single(handler.LastRequest.Headers.GetValues("X-Authorization-Token")));
        Assert.Contains("\"exchangeRate\":36.50", handler.LastBody);
        Assert.Contains("\"cashierId\":4", handler.LastBody);
        Assert.True(handler.LastRequest.Headers.Contains("Idempotency-Key"));
    }

    [Fact]
    public async Task CompleteCreditSale_OverLimit403_ThrowsTypedExceptionWithExactContext()
    {
        var (service, _) = CreateService(_ => Json(HttpStatusCode.Forbidden, """
            {
              "title": "Acceso denegado",
              "detail": "El monto financiado excede el crédito disponible.",
              "authorizationRequired": true,
              "authorizationAction": "CreditOverLimit",
              "authorizationContext": {
                "saleId": 9,
                "customerId": 7,
                "customerName": "Ana Pérez",
                "financedUSD": 120.5000,
                "availableCreditUSD": 100.0000
              }
            }
            """));

        var ex = await Assert.ThrowsAsync<CreditOverLimitAuthorizationException>(() =>
            service.CompleteCreditSaleAsync(9, 36.50m, Array.Empty<SalePaymentDto>()));

        Assert.Equal(9, ex.Context.SaleId);
        Assert.Equal(7, ex.Context.CustomerId);
        Assert.Equal("Ana Pérez", ex.Context.CustomerName);
        Assert.Equal(120.5000m, ex.Context.FinancedUSD);
        Assert.Equal(100.0000m, ex.Context.AvailableCreditUSD);
        Assert.Equal("CreditOverLimit", ex.AuthorizationAction);
        Assert.Contains("excede", ex.Message);
    }

    [Fact]
    public async Task RegisterCreditPayment_PostsAmountAndIdempotencyKey()
    {
        var (service, handler) = CreateService(_ => Json(HttpStatusCode.OK, """
            { "paymentId": 55, "customerId": 7, "amountUSD": 25.00, "amountBsS": 912.50,
              "paymentRate": 36.50, "fxDifferenceBsS": 12.50, "allocations": [] }
            """));

        var result = await service.RegisterCreditPaymentAsync(new CreditPaymentRequestClientDto
        {
            CustomerId = 7,
            AmountUSD = 25m,
            PaymentMethodId = 1,
            ReferenceNumber = "REF-1",
            ReceivableIds = new List<int> { 11 },
            IdempotencyKey = "idem-1"
        });

        Assert.Equal(55, result.PaymentId);
        Assert.Equal(12.50m, result.FxDifferenceBsS);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/sales/credit-payments", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("idem-1", Assert.Single(handler.LastRequest.Headers.GetValues("Idempotency-Key")));
        Assert.Contains("\"amountUSD\":25", handler.LastBody);
        Assert.Contains("\"receivableIds\":[11]", handler.LastBody);
    }

    [Fact]
    public async Task CreditReceipts_ReturnNullOnNotFoundAndBytesOnSuccess()
    {
        var (notFound, _) = CreateService(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await notFound.GetCreditSaleReceiptAsync(9));
        Assert.Null(await notFound.GetCreditPaymentReceiptAsync(55));

        var (ok, handler) = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
        });

        var saleReceipt = await ok.GetCreditSaleReceiptAsync(9);
        Assert.Equal(new byte[] { 1, 2, 3 }, saleReceipt);
        Assert.Equal("/api/sales/9/credit-receipt", handler.LastRequest!.RequestUri!.AbsolutePath);

        var paymentReceipt = await ok.GetCreditPaymentReceiptAsync(55);
        Assert.Equal(new byte[] { 1, 2, 3 }, paymentReceipt);
        Assert.Equal("/api/sales/credit-payments/55/receipt", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task CancelAndReturn_SendExpectedPathsAndBodies()
    {
        var (service, handler) = CreateService(_ => Json(HttpStatusCode.OK, """
            { "saleId": 9, "receivableId": 3, "customerId": 7, "cancelledOriginalUSD": 100.00,
              "status": 4, "saleStatus": 3 }
            """));

        await service.CancelCreditSaleAsync(9, "Error de digitación");
        Assert.Equal("/api/sales/9/credit-cancel", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("Error de digitaci", handler.LastBody);

        (service, handler) = CreateService(_ => Json(HttpStatusCode.OK, """
            { "returnId": 2, "saleId": 9, "receivableId": 3, "customerId": 7, "returnedUSD": 10.00,
              "refundedUSD": 0, "refundedBsS": 0, "balanceAfterUSD": 90.00, "status": 1, "items": [] }
            """));

        var result = await service.RegisterCreditReturnAsync(9, new CreditReturnRequestClientDto
        {
            Items = new List<CreditReturnItemClientRequestDto> { new() { SaleItemId = 4, Quantity = 1m } },
            Reason = "Producto dañado",
            IdempotencyKey = "ret-1"
        });

        Assert.Equal(90.00m, result.BalanceAfterUSD);
        Assert.Equal("/api/sales/9/credit-return", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("ret-1", Assert.Single(handler.LastRequest.Headers.GetValues("Idempotency-Key")));
        Assert.Contains("\"saleItemId\":4", handler.LastBody);
    }
}
