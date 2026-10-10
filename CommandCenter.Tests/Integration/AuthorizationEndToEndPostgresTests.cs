using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Backend.API.Hubs;
using Backend.API.Services;
using Core.Entities;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.150-T11 (E1): E2E full-stack del hub de autorizaciones remotas contra la composición REAL
/// (Program.cs + WebApplicationFactory), PostgreSQL real (Npgsql), clientes SignalR reales sobre
/// TestServer (LongPolling) y HTTP real para la acción protegida. Cada escenario crea su propia
/// base <c>pos_e2e_&lt;run&gt;</c>, la migra/siembra por el arranque real y la descarta al final.
/// Gating del repo: sin TEST_POSTGRES_CONNECTION cada test retorna en silencio.
/// </summary>
[Collection(PostgresRealCollection.Name)]
[Trait("Category", "RequiresDocker")]
public class AuthorizationEndToEndPostgresTests
{
    private const decimal Quantity = 2m;
    private const decimal CustomUnitPriceUsd = 9.99m;
    private const decimal CustomUnitPriceLocal = 499.50m;

    private static RequestAuthorizationContract Contract(
        int saleId,
        int productId,
        string productName,
        string terminal) => AuthorizationEndToEndApi.BuildManualPriceOverrideContract(
            saleId,
            productId,
            productName,
            Quantity,
            CustomUnitPriceUsd,
            CustomUnitPriceLocal,
            terminal);

    // ---------------------------------------------------------------- 1. happy full journey

    [Fact]
    public async Task HappyFullJourney_HubRequestRemoteApprovalHttpConsume_PersistsItemRequestAndRemoteAudit()
    {
        await using var app = await AuthorizationEndToEndApplication.CreateAsync("journey");
        if (!app.IsAvailable) return;

        var cashier = await app.CreateUserAsync(7001, UserRole.Cashier, "Cajero E2E Journey");
        var admin = await app.CreateUserAsync(7002, UserRole.Admin, "Admin E2E Journey");
        await app.CreateProductAsync(9701, "Cafe E2E Journey", priceUsd: 12.50m, priceBsS: 625.00m);

        var cashierToken = app.IssueToken(cashier);
        using var cashierHttp = app.CreateHttpClient(cashierToken);
        var saleId = await AuthorizationEndToEndApi.StartSaleAsync(cashierHttp);

        await using var cashierHub = app.CreateHubConnection(cashierToken);
        await using var adminHub = app.CreateHubConnection(app.IssueToken(admin));
        var requested = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolved = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        adminHub.On<JsonElement>("AuthorizationRequested", payload => requested.TrySetResult(payload));
        cashierHub.On<JsonElement>("AuthorizationResolved", payload => resolved.TrySetResult(payload));
        await cashierHub.StartAsync();
        await adminHub.StartAsync();

        var create = await cashierHub.InvokeAsync<JsonElement>(
            "RequestAuthorization",
            Contract(saleId, 9701, "Cafe E2E Journey", "Caja-E2E-Journey"));
        Assert.True(create.GetProperty("success").GetBoolean());
        var requestId = create.GetProperty("requestId").GetInt32();
        Assert.Equal("Pending", create.GetProperty("status").GetString());
        Assert.False(create.GetProperty("deduplicated").GetBoolean());
        Assert.InRange(create.GetProperty("remainingLifetimeSeconds").GetInt32(), 58, 60);

        var requestedPayload = await requested.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(requestId, requestedPayload.GetProperty("requestId").GetInt32());
        Assert.Equal("ManualPriceOverride", requestedPayload.GetProperty("actionType").GetString());
        Assert.Equal(saleId, requestedPayload.GetProperty("saleId").GetInt32());
        Assert.Equal("Cajero E2E Journey", requestedPayload.GetProperty("requestedByName").GetString());
        Assert.Equal("Caja-E2E-Journey", requestedPayload.GetProperty("terminal").GetString());
        var context = requestedPayload.GetProperty("context");
        Assert.Equal("Cafe E2E Journey", context.GetProperty("productName").GetString());
        Assert.Equal(Quantity, context.GetProperty("quantity").GetDecimal());
        Assert.Equal(CustomUnitPriceUsd, context.GetProperty("customUnitPriceUsd").GetDecimal());
        Assert.True(requestedPayload.TryGetProperty("createdAt", out _));
        Assert.True(requestedPayload.TryGetProperty("expiresAt", out _));

        var resolve = await adminHub.InvokeAsync<JsonElement>(
            "ResolveAuthorization",
            requestId,
            true,
            "Aprobado E2E Journey");
        Assert.True(resolve.GetProperty("success").GetBoolean());
        Assert.Equal("Approved", resolve.GetProperty("status").GetString());
        Assert.Equal("Admin E2E Journey", resolve.GetProperty("resolvedByName").GetString());

        var resolvedPayload = await resolved.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(requestId, resolvedPayload.GetProperty("requestId").GetInt32());
        Assert.Equal("Approved", resolvedPayload.GetProperty("status").GetString());
        Assert.True(resolvedPayload.GetProperty("approved").GetBoolean());
        Assert.Equal("Admin E2E Journey", resolvedPayload.GetProperty("resolvedByName").GetString());
        Assert.Equal("Remote", resolvedPayload.GetProperty("resolutionMode").GetString());
        var token = AuthorizationEndToEndApi.ReadRequiredToken(resolvedPayload);

        var exchangeRate = await app.GetSafeExchangeRateAsync();
        using var addResponse = await AuthorizationEndToEndApi.SendAddItemAsync(
            cashierHttp,
            saleId,
            AuthorizationEndToEndApi.BuildAddItemPayload(9701, Quantity, exchangeRate, CustomUnitPriceUsd, CustomUnitPriceLocal),
            token,
            "E2E-JOURNEY-01");
        Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);
        var saleBody = await addResponse.Content.ReadFromJsonAsync<JsonElement>();
        var item = saleBody.GetProperty("items")[0];
        Assert.Equal(9701, item.GetProperty("productId").GetInt32());
        Assert.Equal(CustomUnitPriceUsd, item.GetProperty("unitPrice").GetDecimal());
        Assert.True(item.GetProperty("isCustomPrice").GetBoolean());

        await using var db = app.CreateSalesDbContext();
        var persistedItem = await db.SaleItems.AsNoTracking().SingleAsync(row => row.SaleId == saleId);
        Assert.Equal(CustomUnitPriceUsd, persistedItem.UnitPrice);
        Assert.True(persistedItem.IsCustomPrice);

        var persistedRequest = await db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId);
        Assert.Equal(AuthorizationStatus.Approved, persistedRequest.Status);
        Assert.NotNull(persistedRequest.ConsumedAt);

        var audits = await db.AuthorizationAudits.AsNoTracking().Where(row => row.RequestId == requestId).ToListAsync();
        var audit = Assert.Single(audits);
        Assert.Equal(AuthorizationActionType.ManualPriceOverride, audit.ActionType);
        Assert.Equal(saleId, audit.SaleId);
        Assert.Equal(cashier.Id, audit.RequestedByUserId);
        Assert.Equal("Cajero E2E Journey", audit.RequestedByName);
        Assert.Equal(AuthorizationStatus.Approved, audit.Status);
        Assert.Equal(AuthorizationResolutionMode.Remote, audit.ResolutionMode);
        Assert.Equal(admin.Id, audit.ResolvedByUserId);
        Assert.Equal("Admin E2E Journey", audit.ResolvedByName);
        Assert.Equal("Aprobado E2E Journey", audit.Reason);
        Assert.Equal("Caja-E2E-Journey", audit.Terminal);
        Assert.Equal(persistedRequest.CreatedAt, audit.RequestedAt);
        Assert.True(audit.ResolvedAt >= audit.RequestedAt);
    }

    // ---------------------------------------------------------------- 2. race

    [Fact]
    public async Task Race_TwoElevatedClientsResolve_FirstWinsAndSecondGetsExactMessage()
    {
        await using var app = await AuthorizationEndToEndApplication.CreateAsync("race");
        if (!app.IsAvailable) return;

        var cashier = await app.CreateUserAsync(7101, UserRole.Cashier, "Cajero E2E Race");
        var firstAdmin = await app.CreateUserAsync(7102, UserRole.Admin, "Admin E2E Race Uno");
        var secondAdmin = await app.CreateUserAsync(7103, UserRole.Admin, "Admin E2E Race Dos");
        await app.CreateProductAsync(9711, "Producto E2E Race", priceUsd: 10m, priceBsS: 500m);

        var cashierToken = app.IssueToken(cashier);
        using var cashierHttp = app.CreateHttpClient(cashierToken);
        var saleId = await AuthorizationEndToEndApi.StartSaleAsync(cashierHttp);

        await using var cashierHub = app.CreateHubConnection(cashierToken);
        await using var firstAdminHub = app.CreateHubConnection(app.IssueToken(firstAdmin));
        await using var secondAdminHub = app.CreateHubConnection(app.IssueToken(secondAdmin));
        var requesterResolved = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        cashierHub.On<JsonElement>("AuthorizationResolved", payload => requesterResolved.TrySetResult(payload));
        await cashierHub.StartAsync();
        await firstAdminHub.StartAsync();
        await secondAdminHub.StartAsync();

        var create = await cashierHub.InvokeAsync<JsonElement>(
            "RequestAuthorization",
            Contract(saleId, 9711, "Producto E2E Race", "Caja-E2E-Race"));
        var requestId = create.GetProperty("requestId").GetInt32();

        var firstTask = firstAdminHub.InvokeAsync<JsonElement>("ResolveAuthorization", requestId, true, "Gano primero");
        var secondTask = secondAdminHub.InvokeAsync<JsonElement>("ResolveAuthorization", requestId, true, "Llego tarde");
        var results = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(20));

        var firstSuccess = results[0].GetProperty("success").GetBoolean();
        var secondSuccess = results[1].GetProperty("success").GetBoolean();
        Assert.NotEqual(firstSuccess, secondSuccess);
        var winner = firstSuccess ? results[0] : results[1];
        var loser = firstSuccess ? results[1] : results[0];
        var expectedWinnerName = firstSuccess ? "Admin E2E Race Uno" : "Admin E2E Race Dos";
        Assert.Equal(expectedWinnerName, winner.GetProperty("resolvedByName").GetString());
        Assert.Equal("Approved", winner.GetProperty("status").GetString());
        Assert.Equal(
            $"Esta solicitud ya fue resuelta por {expectedWinnerName}.",
            loser.GetProperty("message").GetString());

        var requesterPayload = await requesterResolved.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(requestId, requesterPayload.GetProperty("requestId").GetInt32());
        Assert.Equal("Approved", requesterPayload.GetProperty("status").GetString());
        AuthorizationEndToEndApi.ReadRequiredToken(requesterPayload);

        await using var db = app.CreateSalesDbContext();
        var persisted = await db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId);
        Assert.Equal(AuthorizationStatus.Approved, persisted.Status);
        Assert.Equal(expectedWinnerName, persisted.ResolvedByName);
        Assert.NotNull(persisted.ResolvedAt);
        var audits = await db.AuthorizationAudits.AsNoTracking().Where(row => row.RequestId == requestId).ToListAsync();
        var audit = Assert.Single(audits);
        Assert.Equal(persisted.ResolvedByUserId, audit.ResolvedByUserId);
        Assert.Equal(persisted.ResolvedByName, audit.ResolvedByName);
    }

    // ---------------------------------------------------------------- 3. local fallback

    [Fact]
    public async Task LocalFallback_RealSupervisorCredentials_ResolvesLocalConsumesTokenAndFailsClosedOnWrongPassword()
    {
        await using var app = await AuthorizationEndToEndApplication.CreateAsync("local");
        if (!app.IsAvailable) return;

        const string supervisorPassword = "SuperClaveE2E123!";
        var cashier = await app.CreateUserAsync(7201, UserRole.Cashier, "Cajero E2E Local");
        var supervisor = await app.CreateUserAsync(7202, UserRole.Manager, "Supervisora E2E Local", supervisorPassword);
        await app.CreateProductAsync(9721, "Producto E2E Local", priceUsd: 8m, priceBsS: 400m);

        using var cashierHttp = app.CreateHttpClient(app.IssueToken(cashier));
        var saleId = await AuthorizationEndToEndApi.StartSaleAsync(cashierHttp);
        var contract = Contract(saleId, 9721, "Producto E2E Local", "Caja-E2E-Local");
        var requestId = await AuthorizationEndToEndApi.CreateRequestAsync(cashierHttp, contract);

        using (var response = await AuthorizationEndToEndApi.LocalResolveAsync(
            cashierHttp,
            requestId,
            supervisor.Username,
            supervisorPassword,
            "En sitio"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(supervisor.Id, body.GetProperty("supervisorUserId").GetInt32());
            Assert.Equal("Supervisora E2E Local", body.GetProperty("supervisorName").GetString());
            var token = AuthorizationEndToEndApi.ReadRequiredToken(body);

            using var scope = app.Factory.Services.CreateScope();
            var claims = scope.ServiceProvider.GetRequiredService<IAuthorizationTokenService>().Validate(token);
            Assert.NotNull(claims);
            Assert.Equal(requestId, claims!.RequestId);
            Assert.Equal(cashier.Id, claims.CashierUserId);
            Assert.Equal(saleId, claims.SaleId);

            var exchangeRate = await app.GetSafeExchangeRateAsync();
            using var addResponse = await AuthorizationEndToEndApi.SendAddItemAsync(
                cashierHttp,
                saleId,
                AuthorizationEndToEndApi.BuildAddItemPayload(9721, Quantity, exchangeRate, CustomUnitPriceUsd, CustomUnitPriceLocal),
                token,
                "E2E-LOCAL-01");
            Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);
        }

        // Segundo escenario: credenciales incorrectas → mensaje genérico, solicitud Pending, sin auditoría.
        var secondRequestId = await AuthorizationEndToEndApi.CreateRequestAsync(cashierHttp, contract);
        Assert.NotEqual(requestId, secondRequestId);
        using (var wrong = await AuthorizationEndToEndApi.LocalResolveAsync(
            cashierHttp,
            secondRequestId,
            supervisor.Username,
            "ClaveIncorrecta!",
            reason: null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            var problem = await wrong.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(
                "Credenciales inválidas o sin privilegios para autorizar.",
                problem.GetProperty("detail").GetString());
        }

        await using var db = app.CreateSalesDbContext();
        var audits = await db.AuthorizationAudits.AsNoTracking().Where(row => row.RequestId == requestId).ToListAsync();
        var audit = Assert.Single(audits);
        Assert.Equal(AuthorizationActionType.ManualPriceOverride, audit.ActionType);
        Assert.Equal(AuthorizationStatus.Approved, audit.Status);
        Assert.Equal(AuthorizationResolutionMode.Local, audit.ResolutionMode);
        Assert.Equal(supervisor.Id, audit.ResolvedByUserId);
        Assert.Equal("Supervisora E2E Local", audit.ResolvedByName);
        Assert.Equal("En sitio", audit.Reason);
        Assert.Equal(cashier.Id, audit.RequestedByUserId);

        var consumed = await db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId);
        Assert.NotNull(consumed.ConsumedAt);
        Assert.Equal(AuthorizationResolutionMode.Local, consumed.ResolutionMode);

        var stillPending = await db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == secondRequestId);
        Assert.Equal(AuthorizationStatus.Pending, stillPending.Status);
        Assert.Equal(0, await db.AuthorizationAudits.CountAsync(row => row.RequestId == secondRequestId));

        var supervisorRow = await db.Users.AsNoTracking().SingleAsync(row => row.Id == supervisor.Id);
        Assert.Equal(1, supervisorRow.AccessFailedCount);
    }

    // ---------------------------------------------------------------- 4. expiry via real hosted job

    [Fact]
    public async Task Expiry_RealHostedSweepJob_ExpiresPushesAndAuditsNullResolver()
    {
        await using var app = await AuthorizationEndToEndApplication.CreateAsync("expiry");
        if (!app.IsAvailable) return;

        var cashier = await app.CreateUserAsync(7301, UserRole.Cashier, "Cajero E2E Expiry");
        await app.CreateProductAsync(9731, "Producto E2E Expiry", priceUsd: 5m, priceBsS: 250m);

        var cashierToken = app.IssueToken(cashier);
        using var cashierHttp = app.CreateHttpClient(cashierToken);
        var saleId = await AuthorizationEndToEndApi.StartSaleAsync(cashierHttp);
        var requestId = await AuthorizationEndToEndApi.CreateRequestAsync(
            cashierHttp,
            Contract(saleId, 9731, "Producto E2E Expiry", "Caja-E2E-Expiry"));

        await using var cashierHub = app.CreateHubConnection(cashierToken);
        var expiredPush = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        cashierHub.On<JsonElement>("AuthorizationExpired", payload => expiredPush.TrySetResult(payload));
        await cashierHub.StartAsync();

        // Backdate por el DbContext real: el barrido hosted (AuthorizationExpiryJob) encontrará
        // la solicitud vencida en el próximo tick.
        await using (var backdateDb = app.CreateSalesDbContext())
        {
            var row = await backdateDb.AuthorizationRequests.SingleAsync(candidate => candidate.Id == requestId);
            row.ExpiresAt = DateTime.UtcNow.AddSeconds(-5);
            await backdateDb.SaveChangesAsync();
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        var status = default(JsonElement);
        while (DateTime.UtcNow < deadline)
        {
            status = await AuthorizationEndToEndApi.GetStatusAsync(cashierHttp, requestId);
            if (string.Equals(status.GetProperty("status").GetString(), "Expired", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(250);
        }

        Assert.Equal("Expired", status.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("resolvedAt").ValueKind);

        var expiredPayload = await expiredPush.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(requestId, expiredPayload.GetProperty("requestId").GetInt32());
        Assert.Equal("Expired", expiredPayload.GetProperty("status").GetString());

        await using var db = app.CreateSalesDbContext();
        var persisted = await db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId);
        Assert.Equal(AuthorizationStatus.Expired, persisted.Status);
        Assert.NotNull(persisted.ResolvedAt);

        var audits = await db.AuthorizationAudits.AsNoTracking().Where(row => row.RequestId == requestId).ToListAsync();
        var audit = Assert.Single(audits);
        Assert.Equal(AuthorizationStatus.Expired, audit.Status);
        Assert.Null(audit.ResolutionMode);
        Assert.Null(audit.ResolvedByUserId);
        Assert.Null(audit.ResolvedByName);
        Assert.Equal(persisted.ResolvedAt, audit.ResolvedAt);
    }

    // ---------------------------------------------------------------- 5. audit immutability

    [Fact]
    public async Task AuditImmutability_RealPostgresTrigger_RejectsUpdateAndDeleteWithoutChangingRow()
    {
        await using var app = await AuthorizationEndToEndApplication.CreateAsync("audit");
        if (!app.IsAvailable) return;

        var cashier = await app.CreateUserAsync(7401, UserRole.Cashier, "Cajero E2E Audit");
        var admin = await app.CreateUserAsync(7402, UserRole.Admin, "Admin E2E Audit");
        await app.CreateProductAsync(9741, "Producto E2E Audit", priceUsd: 7m, priceBsS: 350m);

        using var cashierHttp = app.CreateHttpClient(app.IssueToken(cashier));
        using var adminHttp = app.CreateHttpClient(app.IssueToken(admin));
        var saleId = await AuthorizationEndToEndApi.StartSaleAsync(cashierHttp);
        var requestId = await AuthorizationEndToEndApi.CreateRequestAsync(
            cashierHttp,
            Contract(saleId, 9741, "Producto E2E Audit", "Caja-E2E-Audit"));
        await AuthorizationEndToEndApi.ResolveAsync(adminHttp, requestId, approved: true, reason: "Aprobado auditoría");

        int auditId;
        await using (var db = app.CreateSalesDbContext())
        {
            var audit = await db.AuthorizationAudits.AsNoTracking().SingleAsync(row => row.RequestId == requestId);
            auditId = audit.Id;
        }

        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();

        await using (var update = new NpgsqlCommand(
            "UPDATE \"AuthorizationAudits\" SET \"Reason\" = 'manipulado' WHERE \"Id\" = @id",
            connection))
        {
            update.Parameters.AddWithValue("id", auditId);
            var updateException = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
            Assert.Equal("P0001", updateException.SqlState);
            Assert.Contains("append-only", updateException.MessageText, StringComparison.Ordinal);
        }

        await using (var delete = new NpgsqlCommand(
            "DELETE FROM \"AuthorizationAudits\" WHERE \"Id\" = @id",
            connection))
        {
            delete.Parameters.AddWithValue("id", auditId);
            var deleteException = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
            Assert.Equal("P0001", deleteException.SqlState);
            Assert.Contains("append-only", deleteException.MessageText, StringComparison.Ordinal);
        }

        await using var verifyDb = app.CreateSalesDbContext();
        var persisted = await verifyDb.AuthorizationAudits.AsNoTracking().SingleAsync(row => row.Id == auditId);
        Assert.Equal("Aprobado auditoría", persisted.Reason);
        Assert.Equal(1, await verifyDb.AuthorizationAudits.CountAsync(row => row.RequestId == requestId));
    }

    // ---------------------------------------------------------------- 6. token binding

    [Fact]
    public async Task TokenBinding_ApprovedForSaleA_RejectedOnSaleBWithoutBurningTheToken()
    {
        await using var app = await AuthorizationEndToEndApplication.CreateAsync("binding");
        if (!app.IsAvailable) return;

        var cashier = await app.CreateUserAsync(7501, UserRole.Cashier, "Cajero E2E Binding");
        var admin = await app.CreateUserAsync(7502, UserRole.Admin, "Admin E2E Binding");
        await app.CreateProductAsync(9751, "Producto E2E Binding", priceUsd: 11m, priceBsS: 550m);

        using var cashierHttp = app.CreateHttpClient(app.IssueToken(cashier));
        using var adminHttp = app.CreateHttpClient(app.IssueToken(admin));
        var approvedSaleId = await AuthorizationEndToEndApi.StartSaleAsync(cashierHttp);
        var otherSaleId = await AuthorizationEndToEndApi.StartSaleAsync(cashierHttp);
        var requestId = await AuthorizationEndToEndApi.CreateRequestAsync(
            cashierHttp,
            Contract(approvedSaleId, 9751, "Producto E2E Binding", "Caja-E2E-Binding"));
        await AuthorizationEndToEndApi.ResolveAsync(adminHttp, requestId, approved: true, reason: null);
        var status = await AuthorizationEndToEndApi.GetStatusAsync(cashierHttp, requestId);
        var token = AuthorizationEndToEndApi.ReadRequiredToken(status);

        var exchangeRate = await app.GetSafeExchangeRateAsync();
        var payload = AuthorizationEndToEndApi.BuildAddItemPayload(9751, Quantity, exchangeRate, CustomUnitPriceUsd, CustomUnitPriceLocal);

        using (var wrongSale = await AuthorizationEndToEndApi.SendAddItemAsync(
            cashierHttp,
            otherSaleId,
            payload,
            token,
            "E2E-BINDING-01"))
        {
            await AuthorizationEndToEndApi.AssertPriceOverrideForbiddenAsync(wrongSale);
        }

        await using (var db = app.CreateSalesDbContext())
        {
            Assert.Equal(0, await db.SaleItems.CountAsync(row => row.SaleId == otherSaleId));
            var untouched = await db.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId);
            Assert.Null(untouched.ConsumedAt);
        }

        using (var correctSale = await AuthorizationEndToEndApi.SendAddItemAsync(
            cashierHttp,
            approvedSaleId,
            payload,
            token,
            "E2E-BINDING-02"))
        {
            Assert.Equal(HttpStatusCode.OK, correctSale.StatusCode);
        }

        await using (var verifyDb = app.CreateSalesDbContext())
        {
            Assert.Equal(1, await verifyDb.SaleItems.CountAsync(row => row.SaleId == approvedSaleId));
            var consumed = await verifyDb.AuthorizationRequests.AsNoTracking().SingleAsync(row => row.Id == requestId);
            Assert.NotNull(consumed.ConsumedAt);
            Assert.Equal(1, await verifyDb.AuthorizationAudits.CountAsync(row => row.RequestId == requestId));
        }
    }
}
