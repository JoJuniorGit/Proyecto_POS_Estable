using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// Paso 14 (F3-WU1, F3-S1/S4): contratos HTTP y matriz RBAC de la API de rentabilidad y del CRUD
/// de reglas de comisión. Corre contra el pipeline real y una base PostgreSQL aislada.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class ProfitabilityHttpRbacTests
{
    [Fact]
    public async Task ProfitabilityAndFeeRoutes_EnforceTheRoleMatrix()
    {
        await using var app = await AuthorizationEndToEndApplication.CreateAsync("profitability-rbac");
        var admin = await app.CreateUserAsync(9201, UserRole.Admin, "Admin Rentabilidad");
        var manager = await app.CreateUserAsync(9202, UserRole.Manager, "Manager Rentabilidad");
        var cashier = await app.CreateUserAsync(9203, UserRole.Cashier, "Cajero Rentabilidad");

        using var adminClient = app.CreateHttpClient(app.IssueToken(admin));
        using var managerClient = app.CreateHttpClient(app.IssueToken(manager));
        using var cashierClient = app.CreateHttpClient(app.IssueToken(cashier));
        using var anonymous = app.Factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Client-Version", AuthorizationEndToEndApplication.ClientVersion);

        var protectedPaths = new[]
        {
            "/api/profitability/summary",
            "/api/profitability/breakdown",
            "/api/payment-method-fees"
        };

        foreach (var path in protectedPaths)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cashierClient.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync("/api/profitability/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync("/api/profitability/breakdown")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.GetAsync("/api/payment-method-fees")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/profitability/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/profitability/breakdown")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/payment-method-fees")).StatusCode);
    }

    [Fact]
    public async Task PaymentMethodFees_CrudSoftDeleteAndProfitabilityFilterPropagation()
    {
        await using var app = await AuthorizationEndToEndApplication.CreateAsync("profitability-fees");
        var admin = await app.CreateUserAsync(9211, UserRole.Admin, "Admin Comisiones");
        using var adminClient = app.CreateHttpClient(app.IssueToken(admin));

        var profile = new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "USD",
            PrimaryCurrencySymbol = "$",
            SecondaryCurrencyCode = "VES",
            SecondaryCurrencySymbol = "Bs.S",
            IsConfigured = true
        };
        Assert.Equal(
            HttpStatusCode.OK,
            (await adminClient.PutAsJsonAsync("/api/settings/monetary-profile", profile)).StatusCode);

        var effectiveFromUtc = DateTime.UtcNow.AddDays(-1);
        var createRequest = new CreatePaymentMethodFeeRuleRequest
        {
            PaymentMethodId = 2,
            FeeType = PaymentMethodFeeType.Percentage,
            Percentage = 1m,
            FixedAmount = null,
            CurrencyCode = "USD",
            EffectiveFromUtc = effectiveFromUtc,
            EffectiveToUtc = null,
            IsActive = true
        };

        using var createResponse = await adminClient.PostAsJsonAsync("/api/payment-method-fees", createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var ruleId = created.GetProperty("id").GetInt32();
        Assert.Equal(2, created.GetProperty("paymentMethodId").GetInt32());
        Assert.Equal("USD", created.GetProperty("currencyCode").GetString());
        Assert.Equal(1m, created.GetProperty("percentage").GetDecimal());
        Assert.True(created.GetProperty("isActive").GetBoolean());

        using var listResponse = await adminClient.GetAsync("/api/payment-method-fees?paymentMethodId=2&activeOnly=true");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listed = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, listed.ValueKind);
        Assert.Contains(listed.EnumerateArray(), row => row.GetProperty("id").GetInt32() == ruleId);

        var updateRequest = new UpdatePaymentMethodFeeRuleRequest
        {
            FeeType = PaymentMethodFeeType.Percentage,
            Percentage = 1.5m,
            FixedAmount = null,
            CurrencyCode = "USD",
            EffectiveFromUtc = effectiveFromUtc,
            EffectiveToUtc = null,
            IsActive = true
        };
        using var updateResponse = await adminClient.PutAsJsonAsync($"/api/payment-method-fees/{ruleId}", updateRequest);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1.5m, updated.GetProperty("percentage").GetDecimal());

        var invalidRequest = new CreatePaymentMethodFeeRuleRequest
        {
            PaymentMethodId = 2,
            FeeType = PaymentMethodFeeType.Percentage,
            Percentage = 150m,
            CurrencyCode = "USD",
            EffectiveFromUtc = effectiveFromUtc,
            IsActive = true
        };
        using var invalidResponse = await adminClient.PostAsJsonAsync("/api/payment-method-fees", invalidRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        var overlappingRequest = new CreatePaymentMethodFeeRuleRequest
        {
            PaymentMethodId = 2,
            FeeType = PaymentMethodFeeType.Percentage,
            Percentage = 1m,
            CurrencyCode = "USD",
            EffectiveFromUtc = effectiveFromUtc.AddDays(1),
            IsActive = true
        };
        using var overlappingResponse = await adminClient.PostAsJsonAsync("/api/payment-method-fees", overlappingRequest);
        Assert.Equal(HttpStatusCode.BadRequest, overlappingResponse.StatusCode);

        using var deleteResponse = await adminClient.DeleteAsync($"/api/payment-method-fees/{ruleId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using var getDeletedResponse = await adminClient.GetAsync($"/api/payment-method-fees/{ruleId}");
        Assert.Equal(HttpStatusCode.OK, getDeletedResponse.StatusCode);
        var deleted = await getDeletedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(deleted.GetProperty("isActive").GetBoolean());

        using var summaryResponse = await adminClient.GetAsync(
            "/api/profitability/summary?preset=Custom&fromDate=2026-11-01&toDate=2026-11-02");
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summary = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();

        // The global time zone falls back to Venezuela (UTC-4): each local day opens at 04:00 UTC.
        Assert.Equal(
            new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc),
            summary.GetProperty("fromUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(
            new DateTime(2026, 11, 3, 4, 0, 0, DateTimeKind.Utc),
            summary.GetProperty("toUtc").GetDateTime().ToUniversalTime());
        Assert.False(string.IsNullOrWhiteSpace(summary.GetProperty("timeZoneId").GetString()));
        Assert.Equal("USD", summary.GetProperty("reportCurrencyCode").GetString());
        Assert.False(summary.GetProperty("isSingleCurrency").GetBoolean());

        Assert.True(summary.TryGetProperty("accrued", out var accrued));
        Assert.Equal(0m, accrued.GetProperty("grossSales").GetDecimal());
        Assert.True(summary.TryGetProperty("realized", out _));
        Assert.True(summary.TryGetProperty("coverage", out _));

        var breakdown = summary.GetProperty("breakdown");
        Assert.Equal(JsonValueKind.Array, breakdown.GetProperty("series").ValueKind);
        Assert.Equal(JsonValueKind.Array, breakdown.GetProperty("paymentMethods").ValueKind);
        Assert.Equal(JsonValueKind.Array, breakdown.GetProperty("categories").ValueKind);
        Assert.Equal(JsonValueKind.Array, breakdown.GetProperty("products").ValueKind);
        Assert.Equal(JsonValueKind.Array, breakdown.GetProperty("sellers").ValueKind);
        Assert.Equal(JsonValueKind.Array, breakdown.GetProperty("alerts").ValueKind);

        using var breakdownResponse = await adminClient.GetAsync(
            "/api/profitability/breakdown?preset=Custom&fromDate=2026-11-01&toDate=2026-11-02");
        Assert.Equal(HttpStatusCode.OK, breakdownResponse.StatusCode);
        var breakdownOnly = await breakdownResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, breakdownOnly.GetProperty("alerts").ValueKind);
    }
}
