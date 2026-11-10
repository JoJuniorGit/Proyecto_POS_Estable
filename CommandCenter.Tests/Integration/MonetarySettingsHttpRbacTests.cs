using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.211-T6-U16 (S3/S11): HTTP-level RBAC for the monetary settings and client profile routes.
/// Runs against the real pipeline and an isolated PostgreSQL database.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class PostgresMonetarySettingsHttpRbacTests
{
    [Fact]
    public async Task MonetaryRoutes_EnforceAdminManagerAndAuthenticatedClientPolicies()
    {
        await using var app = await AuthorizationEndToEndApplication.CreateAsync("monetary-rbac");
        var admin = await app.CreateUserAsync(9101, UserRole.Admin, "Admin Monetario");
        var manager = await app.CreateUserAsync(9102, UserRole.Manager, "Manager Monetario");
        var cashier = await app.CreateUserAsync(9103, UserRole.Cashier, "Cajero Monetario");
        var driver = await app.CreateUserAsync(9104, UserRole.Driver, "Driver Monetario");

        using var adminClient = app.CreateHttpClient(app.IssueToken(admin));
        using var managerClient = app.CreateHttpClient(app.IssueToken(manager));
        using var cashierClient = app.CreateHttpClient(app.IssueToken(cashier));
        using var driverClient = app.CreateHttpClient(app.IssueToken(driver));
        using var anonymous = app.Factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Client-Version", "1.0.0");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/settings/monetary-profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashierClient.GetAsync("/api/settings/monetary-profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await driverClient.GetAsync("/api/settings/monetary-profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync("/api/settings/monetary-profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/settings/monetary-profile")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await cashierClient.GetAsync("/api/settings/monetary-setup-status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await driverClient.GetAsync("/api/settings/monetary-setup-status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync("/api/settings/monetary-setup-status")).StatusCode);

        var profile = new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "USD",
            PrimaryCurrencySymbol = "$",
            SecondaryCurrencyCode = "VES",
            SecondaryCurrencySymbol = "Bs.S",
            IsConfigured = true
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.PutAsJsonAsync("/api/settings/monetary-profile", profile)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await driverClient.PutAsJsonAsync("/api/settings/monetary-profile", profile)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.PutAsJsonAsync("/api/settings/monetary-profile", profile)).StatusCode);

        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        Assert.Equal(HttpStatusCode.Forbidden, (await cashierClient.GetAsync($"/api/settings/manual-currency-rates?date={date:yyyy-MM-dd}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await driverClient.GetAsync($"/api/settings/manual-currency-rates?date={date:yyyy-MM-dd}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync($"/api/settings/manual-currency-rates?date={date:yyyy-MM-dd}")).StatusCode);

        var manualRate = new ManualCurrencyRateRequest
        {
            FromCurrencyCode = "USD",
            ToCurrencyCode = "COP",
            Rate = 4000m,
            RateDate = date
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.PutAsJsonAsync("/api/settings/manual-currency-rates", manualRate)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminClient.PutAsJsonAsync("/api/settings/manual-currency-rates", manualRate)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await cashierClient.GetAsync("/api/client/monetary-profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/client/monetary-profile")).StatusCode);
    }
}

