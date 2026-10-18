using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Hubs;
using Backend.API.Services;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Receipts;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// T3b1 (F2b, Paso 11/12, S4/S5/S7): autorización de sobregiro de cupo (CreditOverLimit) y
/// comprobante de venta a crédito. Cubre el ciclo completo del canal de autorizaciones
/// (canonicalizador → create → resolve → consume), los contratos 403 del cierre a crédito, la
/// regla dura de no-bypass (bloqueos/mora/deshabilitado/plazo jamás autorizables) y el PDF de
/// entrega con contexto CxC.
/// </summary>
public class CreditOverLimitAuthorizationTests
{
    private const string TestKey = "POS_Test_Super_Secret_Key_At_Least_32_Chars_Long!";
    private const int CashierId = 70;
    private const int AdminId = 2;
    private const int CustomerId = 55;
    private const decimal FinancedUSD = 100m;
    private const decimal AvailableCreditUSD = 50m;

    private static CreditOverLimitContext CreditContext(
        int customerId = CustomerId,
        int saleId = 501,
        decimal financedUsd = FinancedUSD,
        decimal availableUsd = AvailableCreditUSD) => new()
        {
            CustomerId = customerId,
            SaleId = saleId,
            FinancedUSD = financedUsd,
            AvailableCreditUSD = availableUsd
        };

    private static AuthorizationTokenService CreateTokenService(int ttlSeconds = 60) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "JWT_SETTINGS_KEY", TestKey },
            { "JwtSettings:Issuer", "SolucionesPos" },
            { "JwtSettings:Audience", "PosClient" },
            { "JwtSettings:ExpiryMinutes", "60" }
        }).Build(), TimeSpan.FromSeconds(ttlSeconds));

    // ---------------------------------------------------------------- canonicalizador

    [Fact]
    public void Canonicalizer_CreditOverLimit_IsDeterministicCultureInvariantAndScaleNormalized()
    {
        var baseline = AuthorizationContextCanonicalizer.ComputeCreditOverLimitHash(CreditContext());
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(baseline, AuthorizationContextCanonicalizer.ComputeCreditOverLimitHash(CreditContext()));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Matches("^[0-9a-f]{64}$", baseline);
        Assert.Equal(baseline, AuthorizationContextCanonicalizer.ComputeCreditOverLimitHash(
            CreditContext(financedUsd: 100.0000m, availableUsd: 50.0000m)));

        Assert.NotEqual(baseline, AuthorizationContextCanonicalizer.ComputeCreditOverLimitHash(CreditContext(customerId: 56)));
        Assert.NotEqual(baseline, AuthorizationContextCanonicalizer.ComputeCreditOverLimitHash(CreditContext(saleId: 502)));
        Assert.NotEqual(baseline, AuthorizationContextCanonicalizer.ComputeCreditOverLimitHash(CreditContext(financedUsd: 101m)));
        Assert.NotEqual(baseline, AuthorizationContextCanonicalizer.ComputeCreditOverLimitHash(CreditContext(availableUsd: 49m)));
    }

    [Fact]
    public void BuildCreditOverLimitPayload_DisplayJsonCarriesCustomerContextAndBoundedName()
    {
        var payload = AuthorizationContextCanonicalizer.BuildCreditOverLimitPayload(CreditContext(), "Maria Perez");

        Assert.Contains("\"customerId\":55", payload.ContextJson);
        Assert.Contains("\"customerName\":\"Maria Perez\"", payload.ContextJson);
        Assert.Contains("\"saleId\":501", payload.ContextJson);
        Assert.Contains("\"financedUSD\":100", payload.ContextJson);
        Assert.Contains("\"availableCreditUSD\":50", payload.ContextJson);

        // El nombre es presentación: jamás altera el hash que liga el token al contexto exacto.
        var first = AuthorizationContextCanonicalizer.BuildCreditOverLimitPayload(CreditContext(), "Cliente A");
        var second = AuthorizationContextCanonicalizer.BuildCreditOverLimitPayload(CreditContext(), "Cliente B");
        Assert.Equal(first.ContextHash, second.ContextHash);

        var longName = new string('x', 500);
        var bounded = AuthorizationContextCanonicalizer.BuildCreditOverLimitPayload(CreditContext(), longName);
        Assert.DoesNotContain(longName, bounded.ContextJson);
        Assert.Contains(new string('x', AuthorizationContextCanonicalizer.MaxCustomerNameLength), bounded.ContextJson);
    }

    // ---------------------------------------------------------------- servicio: señal y bypass

    [Fact]
    public async Task CompleteCreditSale_OverLimit_ThrowsAuthorizationSignalWithExactContext()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, creditLimitUsd: 50m);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        var service = CreateSalesHarness(context);

        var ex = await Assert.ThrowsAsync<CreditOverLimitRequiresAuthorizationException>(() =>
            service.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }));

        Assert.Equal(sale.Id, ex.SaleId);
        Assert.Equal(customer.Id, ex.CustomerId);
        Assert.Equal(customer.Name, ex.CustomerName);
        Assert.Equal(100m, ex.FinancedUSD);
        Assert.Equal(50m, ex.AvailableCreditUSD);
        Assert.Contains("Crédito insuficiente", ex.Message);

        Assert.Equal(0, await context.Receivables.AsNoTracking().CountAsync());
        Assert.Equal(SaleStatus.Pending, (await context.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == sale.Id)).Status);
    }

    [Fact]
    public async Task CompleteCreditSale_OverLimitAuthorized_CompletesAndCreatesReceivable()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, creditLimitUsd: 50m);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        var service = CreateSalesHarness(context);

        var invoiceNumber = await service.CompleteCreditSaleAsync(
            sale.Id,
            new CreditSaleRequest { ExchangeRate = 50m },
            creditOverLimitAuthorized: true);

        Assert.True(invoiceNumber > 0);
        var persisted = await context.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, persisted.Status);

        var receivable = await context.Receivables.AsNoTracking().SingleAsync(candidate => candidate.SaleId == sale.Id);
        Assert.Equal(100.0000m, receivable.OriginalUSD);
        Assert.Equal(100.0000m, receivable.BalanceUSD);
        Assert.Equal(5000.00m, receivable.OriginalBsS);
        Assert.Equal(50.0000m, receivable.HistoricalRate);
        Assert.Equal(ReceivableStatus.Vigente, receivable.Status);
    }

    [Fact]
    public async Task CompleteCreditSale_ManuallyBlocked_IsNeverBypassedByAuthorizationFlag()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, creditStatus: CreditStatus.ManuallyBlocked);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        var service = CreateSalesHarness(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }, creditOverLimitAuthorized: true));

        Assert.Contains("bloqueado manualmente", ex.Message);
        Assert.Equal(0, await context.Receivables.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CompleteCreditSale_SuspendedByMora_IsNeverBypassedByAuthorizationFlag()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, creditStatus: CreditStatus.SuspendedByMora);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        var service = CreateSalesHarness(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }, creditOverLimitAuthorized: true));

        Assert.Contains("morosidad", ex.Message);
        Assert.Equal(0, await context.Receivables.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CompleteCreditSale_OverdueBeyondGrace_IsNeverBypassedByAuthorizationFlag()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, creditGraceDays: 3);
        await SeedOverdueReceivableAsync(context, customer.Id, balanceUsd: 10m, daysAgo: 10);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        var service = CreateSalesHarness(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }, creditOverLimitAuthorized: true));

        Assert.Contains("vencidas", ex.Message);
        Assert.Equal(0, await context.Receivables.AsNoTracking().CountAsync(candidate => candidate.SaleId == sale.Id));
    }

    [Fact]
    public async Task CompleteCreditSale_CreditDisabled_IsNeverBypassedByAuthorizationFlag()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, isCreditEnabled: false);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        var service = CreateSalesHarness(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }, creditOverLimitAuthorized: true));

        Assert.Contains("línea de crédito", ex.Message);
        Assert.Equal(0, await context.Receivables.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CompleteCreditSale_InvalidPaymentTerm_IsNeverBypassedByAuthorizationFlag()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, paymentTermDays: 0);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        var service = CreateSalesHarness(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }, creditOverLimitAuthorized: true));

        Assert.Contains("plazo", ex.Message);
        Assert.Equal(0, await context.Receivables.AsNoTracking().CountAsync());
    }

    // ---------------------------------------------------------------- coordinador

    [Fact]
    public async Task CreateAsync_CreditOverLimit_BuildsCanonicalContextAndNotifiesDisplayPayload()
    {
        using var stack = new CoordinatorStack();
        var sale = await stack.SeedPendingSaleAsync(CashierId);

        var result = await stack.Coordinator.CreateAsync(
            BuildCreditContract(sale.Id),
            CashierId,
            "Cajero 70",
            UserRole.Cashier);

        Assert.Equal(CreateAuthorizationOutcome.Created, result.Outcome);
        var persisted = await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(AuthorizationActionType.CreditOverLimit, persisted.ActionType);
        Assert.Equal(sale.Id, persisted.SaleId);

        var expectedHash = AuthorizationContextCanonicalizer.ComputeCreditOverLimitHash(
            CreditContext(saleId: sale.Id));
        Assert.Equal(expectedHash, persisted.ContextHash);
        Assert.Matches("^[0-9a-f]{64}$", persisted.ContextHash);
        Assert.Contains("\"customerName\":\"Maria Sobregiro\"", persisted.ContextJson);
        Assert.Contains("\"financedUSD\":100", persisted.ContextJson);
        Assert.Contains("\"availableCreditUSD\":50", persisted.ContextJson);
        Assert.Contains($"\"saleId\":{sale.Id}", persisted.ContextJson);

        // El push a supervisores (AuthorizationRequested) lleva el contexto tipado completo.
        var notification = Assert.Single(stack.Notifier.CreatedRequests);
        Assert.Equal(AuthorizationActionType.CreditOverLimit, notification.ActionType);
        Assert.Contains("\"customerId\":55", notification.ContextJson);
    }

    [Fact]
    public async Task CreateAsync_CreditOverLimitWithoutContext_IsRejectedWithoutPersisting()
    {
        using var stack = new CoordinatorStack();
        var sale = await stack.SeedPendingSaleAsync(CashierId);

        var result = await stack.Coordinator.CreateAsync(
            new RequestAuthorizationContract
            {
                SaleId = sale.Id,
                ActionType = AuthorizationActionType.CreditOverLimit,
                CustomerName = "Sin Monto"
            },
            CashierId,
            "Cajero 70",
            UserRole.Cashier);

        Assert.Equal(CreateAuthorizationOutcome.UnknownAction, result.Outcome);
        Assert.Equal(0, await stack.Db.AuthorizationRequests.CountAsync());
    }

    [Fact]
    public async Task EndToEnd_CreditOverLimit_CreateResolveConsume_IsSingleUseAndContextBound()
    {
        using var stack = new CoordinatorStack();
        var sale = await stack.SeedPendingSaleAsync(CashierId);
        var requestId = await CreateApprovedCreditRequestAsync(stack, sale.Id);
        var token = stack.Notifier.ResolvedNotifications.Single().Token;
        Assert.False(string.IsNullOrWhiteSpace(token));

        // Contexto distinto (monto financiado): rechazado sin quemar el token.
        var mismatch = await stack.Coordinator.ConsumeCreditOverLimitAsync(
            token!, sale.Id, CashierId, CreditContext(saleId: sale.Id, financedUsd: 101m));
        Assert.Equal(AuthorizationConsumeStatus.ContextMismatch, mismatch.Status);

        var consumed = await stack.Coordinator.ConsumeCreditOverLimitAsync(
            token!, sale.Id, CashierId, CreditContext(saleId: sale.Id));
        Assert.Equal(AuthorizationConsumeStatus.Consumed, consumed.Status);

        var again = await stack.Coordinator.ConsumeCreditOverLimitAsync(
            token!, sale.Id, CashierId, CreditContext(saleId: sale.Id));
        Assert.Equal(AuthorizationConsumeStatus.AlreadyConsumed, again.Status);

        var persisted = await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync(request => request.Id == requestId);
        Assert.NotNull(persisted.ConsumedAt);
        Assert.Equal(1, await stack.Db.AuthorizationAudits.CountAsync(audit => audit.RequestId == requestId));
        Assert.Equal(AuthorizationStatus.Approved, persisted.Status);
    }

    [Fact]
    public async Task ConsumeCreditOverLimit_TokenFromManualPriceRequest_ReturnsWrongAction()
    {
        using var stack = new CoordinatorStack();
        var sale = await stack.SeedPendingSaleAsync(CashierId);

        // Una aprobación de precio manual (acción 1) sobre la misma venta jamás habilita el
        // sobregiro: el binding de acción corre contra la fila aprobada, no contra las claims.
        var manual = await stack.Coordinator.CreateAsync(
            new RequestAuthorizationContract
            {
                SaleId = sale.Id,
                ProductId = 10,
                ProductName = "Cafe molido",
                Quantity = 2m,
                CustomUnitPriceUsd = 9.99m,
                CustomUnitPriceLocal = 1234.5m,
                Terminal = "Caja-03"
            },
            CashierId,
            "Cajero 70",
            UserRole.Cashier);
        Assert.Equal(CreateAuthorizationOutcome.Created, manual.Outcome);
        await stack.Coordinator.ResolveAsync(manual.Request!.Id, AdminId, "Admin Dos", approved: true);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;

        var result = await stack.Coordinator.ConsumeCreditOverLimitAsync(
            token, sale.Id, CashierId, CreditContext(saleId: sale.Id));

        Assert.Equal(AuthorizationConsumeStatus.WrongAction, result.Status);
        Assert.Null((await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync(request => request.Id == manual.Request.Id)).ConsumedAt);
    }

    [Fact]
    public async Task ConsumeCreditOverLimit_OutsideTokenWindow_ReturnsTokenExpired()
    {
        using var stack = new CoordinatorStack();
        var sale = await stack.SeedPendingSaleAsync(CashierId);
        var requestId = await CreateApprovedCreditRequestAsync(stack, sale.Id);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;

        var row = await stack.Db.AuthorizationRequests.SingleAsync(request => request.Id == requestId);
        row.ResolvedAt = row.ResolvedAt!.Value.AddSeconds(-61);
        await stack.Db.SaveChangesAsync();

        var result = await stack.Coordinator.ConsumeCreditOverLimitAsync(
            token, sale.Id, CashierId, CreditContext(saleId: sale.Id));

        Assert.Equal(AuthorizationConsumeStatus.TokenExpired, result.Status);
        Assert.Null((await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync(request => request.Id == requestId)).ConsumedAt);
    }

    // ---------------------------------------------------------------- controlador: contrato 403

    [Fact]
    public async Task CompleteCredit_OverLimitWithoutToken_ReturnsForbiddenContractWithActionAndContext()
    {
        var (controller, service) = CreateController(coordinator: null);
        var signal = BuildSignal(saleId: 501);
        service
            .Setup(instance => instance.CompleteCreditSaleAsync(It.IsAny<int>(), It.IsAny<CreditSaleRequest>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(signal);

        var result = await controller.CompleteCreditSaleAsync(501, new CreditSaleRequest { ExchangeRate = 50m }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Contains("Se requiere autorización", problem.Detail);
        Assert.True((bool)problem.Extensions["authorizationRequired"]!);
        Assert.Equal("CreditOverLimit", problem.Extensions["authorizationAction"]);
        var context = Assert.IsType<Dictionary<string, object?>>(problem.Extensions["authorizationContext"]);
        Assert.Equal(501, (int)context["saleId"]!);
        Assert.Equal(CustomerId, (int)context["customerId"]!);
        Assert.Equal("Maria Sobregiro", context["customerName"]);
        Assert.Equal(FinancedUSD, (decimal)context["financedUSD"]!);
        Assert.Equal(AvailableCreditUSD, (decimal)context["availableCreditUSD"]!);

        service.Verify(
            instance => instance.CompleteCreditSaleAsync(It.IsAny<int>(), It.IsAny<CreditSaleRequest>(), It.IsAny<int?>(), true, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CompleteCredit_WithValidToken_ConsumesAndRetriesEnablingOnlyOverLimit()
    {
        using var stack = new CoordinatorStack();
        var sale = await stack.SeedPendingSaleAsync(CashierId);
        var requestId = await CreateApprovedCreditRequestAsync(stack, sale.Id);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;

        var (controller, service) = CreateController(stack.Coordinator);
        service
            .Setup(instance => instance.CompleteCreditSaleAsync(sale.Id, It.IsAny<CreditSaleRequest>(), It.IsAny<int?>(), false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(BuildSignal(sale.Id));
        service
            .Setup(instance => instance.CompleteCreditSaleAsync(sale.Id, It.IsAny<CreditSaleRequest>(), It.IsAny<int?>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(4242);
        controller.Request.Headers["X-Authorization-Token"] = token;

        var result = await controller.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(4242, ok.Value);
        service.Verify(
            instance => instance.CompleteCreditSaleAsync(sale.Id, It.IsAny<CreditSaleRequest>(), It.IsAny<int?>(), true, It.IsAny<CancellationToken>()),
            Times.Once);

        var persisted = await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync(request => request.Id == requestId);
        Assert.NotNull(persisted.ConsumedAt);

        // Single-use: el reintento con el mismo token cae al contrato 403 sin volver a habilitar.
        var second = await controller.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }, CancellationToken.None);
        var forbidden = Assert.IsType<ObjectResult>(second);
        Assert.Equal(403, forbidden.StatusCode);
        service.Verify(
            instance => instance.CompleteCreditSaleAsync(sale.Id, It.IsAny<CreditSaleRequest>(), It.IsAny<int?>(), true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CompleteCredit_TokenForDifferentFinancedAmount_ReturnsForbiddenWithoutConsuming()
    {
        using var stack = new CoordinatorStack();
        var sale = await stack.SeedPendingSaleAsync(CashierId);
        var requestId = await CreateApprovedCreditRequestAsync(stack, sale.Id);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;

        var (controller, service) = CreateController(stack.Coordinator);
        // El contexto real del reintento cambió (p. ej. la venta se editó): 120 ≠ 100 aprobado.
        service
            .Setup(instance => instance.CompleteCreditSaleAsync(sale.Id, It.IsAny<CreditSaleRequest>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(BuildSignal(sale.Id, financedUsd: 120m, availableUsd: 50m));
        controller.Request.Headers["X-Authorization-Token"] = token;

        var result = await controller.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, objectResult.StatusCode);
        Assert.Null((await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync(request => request.Id == requestId)).ConsumedAt);
    }

    [Fact]
    public async Task CompleteCredit_ManuallyBlocked_IsNeverBypassedEvenWithValidToken()
    {
        using var stack = new CoordinatorStack();
        var sale = await stack.SeedPendingSaleAsync(CashierId);
        await CreateApprovedCreditRequestAsync(stack, sale.Id);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;

        var (controller, service) = CreateController(stack.Coordinator);
        service
            .Setup(instance => instance.CompleteCreditSaleAsync(sale.Id, It.IsAny<CreditSaleRequest>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("El crédito del cliente está bloqueado manualmente por la administración."));
        controller.Request.Headers["X-Authorization-Token"] = token;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }, CancellationToken.None));

        Assert.Contains("bloqueado manualmente", ex.Message);
        Assert.Null((await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync()).ConsumedAt);
        service.Verify(
            instance => instance.CompleteCreditSaleAsync(sale.Id, It.IsAny<CreditSaleRequest>(), It.IsAny<int?>(), true, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CompleteCredit_TokenOutsideWindow_ReturnsForbiddenWithoutConsuming()
    {
        using var stack = new CoordinatorStack();
        var sale = await stack.SeedPendingSaleAsync(CashierId);
        var requestId = await CreateApprovedCreditRequestAsync(stack, sale.Id);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;

        var row = await stack.Db.AuthorizationRequests.SingleAsync(request => request.Id == requestId);
        row.ResolvedAt = row.ResolvedAt!.Value.AddSeconds(-61);
        await stack.Db.SaveChangesAsync();

        var (controller, service) = CreateController(stack.Coordinator);
        service
            .Setup(instance => instance.CompleteCreditSaleAsync(sale.Id, It.IsAny<CreditSaleRequest>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(BuildSignal(sale.Id));
        controller.Request.Headers["X-Authorization-Token"] = token;

        var result = await controller.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, objectResult.StatusCode);
        Assert.Null((await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync(request => request.Id == requestId)).ConsumedAt);
    }

    // ---------------------------------------------------------------- end-to-end controlador + servicio real

    [Fact]
    public async Task CompleteCredit_EndToEndControllerWithRealService_AuthorizesOnlyTheOverLimitException()
    {
        using var stack = new CoordinatorStack();
        var customer = await SeedCustomerAsync(stack.Db, creditLimitUsd: 50m);
        var sale = await stack.SeedPendingSaleAsync(CashierId, unitPriceUsd: 100m, customerId: customer.Id);
        var salesService = CreateSalesService(stack.Db);

        var (controller, _) = CreateController(stack.Coordinator, salesService: salesService);

        // 1) Primer intento sin token: el servicio real señala el sobregiro y el controlador responde 403.
        var first = await controller.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }, CancellationToken.None);
        var firstForbidden = Assert.IsType<ObjectResult>(first);
        Assert.Equal(403, firstForbidden.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(firstForbidden.Value);
        Assert.Equal("CreditOverLimit", problem.Extensions["authorizationAction"]);
        Assert.Equal(0, await stack.Db.Receivables.CountAsync(receivable => receivable.SaleId == sale.Id));

        // 2) El cajero solicita la autorización reenviando el CONTEXTO EXACTO del 403 (lo que hará
        //    el cliente real): mismos valores que el hash ligará al token.
        var echoedContext = Assert.IsType<Dictionary<string, object?>>(problem.Extensions["authorizationContext"]);
        var created = await stack.Coordinator.CreateAsync(
            new RequestAuthorizationContract
            {
                SaleId = (int)echoedContext["saleId"]!,
                ActionType = AuthorizationActionType.CreditOverLimit,
                CustomerId = (int)echoedContext["customerId"]!,
                CustomerName = (string?)echoedContext["customerName"],
                FinancedUSD = (decimal)echoedContext["financedUSD"]!,
                AvailableCreditUSD = (decimal)echoedContext["availableCreditUSD"]!,
                Terminal = "Caja-03"
            },
            CashierId,
            "Cajero 70",
            UserRole.Cashier);
        Assert.Equal(CreateAuthorizationOutcome.Created, created.Outcome);
        var requestId = created.Request!.Id;
        var resolved = await stack.Coordinator.ResolveAsync(requestId, AdminId, "Admin Dos", approved: true);
        Assert.Equal(ResolveAuthorizationOutcome.Resolved, resolved.Outcome);
        var token = stack.Notifier.ResolvedNotifications.Single().Token!;

        // 3) Reintento con el token: consume single-use y el servicio real completa SOLO el sobregiro.
        controller.Request.Headers["X-Authorization-Token"] = token;
        var second = await controller.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(second);
        var invoiceNumber = Assert.IsType<int>(ok.Value);
        Assert.True(invoiceNumber > 0);

        var persistedSale = await stack.Db.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, persistedSale.Status);
        Assert.Equal(invoiceNumber, persistedSale.InvoiceNumber);

        var receivable = await stack.Db.Receivables.AsNoTracking().SingleAsync(candidate => candidate.SaleId == sale.Id);
        Assert.Equal(customer.Id, receivable.CustomerId);
        Assert.Equal(100.0000m, receivable.OriginalUSD);
        Assert.Equal(5000.00m, receivable.OriginalBsS);

        var request = await stack.Db.AuthorizationRequests.AsNoTracking().SingleAsync(candidate => candidate.Id == requestId);
        Assert.NotNull(request.ConsumedAt);
        Assert.Equal(AuthorizationStatus.Approved, request.Status);
        Assert.Equal(1, await stack.Db.AuthorizationAudits.CountAsync(audit => audit.RequestId == requestId));
    }

    // ---------------------------------------------------------------- comprobante de venta a crédito

    [Fact]
    public async Task CreditSaleReceipt_ServiceBuildsContextAndGeneratorProducesPdf()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var (sale, receivable) = await SeedCompletedCreditSaleAsync(context);

        var receiptService = new SalesReceiptService(context);
        var data = await receiptService.GetCreditSaleReceiptDataAsync(sale.Id);

        Assert.NotNull(data);
        Assert.True(data!.HasReceivable);
        var receipt = data.Context!;
        Assert.Equal(sale.Id, receipt.SaleId);
        Assert.Equal(sale.InvoiceNumber, receipt.InvoiceNumber);
        Assert.Equal("Cliente Credito POS", receipt.CustomerName);
        Assert.Equal(80.0000m, receipt.FinancedUSD);
        Assert.Equal(4000.00m, receipt.FinancedBsS);
        Assert.Equal(50.0000m, receipt.HistoricalRate);
        Assert.Equal(receivable.DueDateUtc, receipt.DueDateUtc);
        Assert.Equal(receivable.Id, receipt.ReceivableId);
        Assert.Single(receipt.Lines);
        var payment = Assert.Single(receipt.Payments);
        Assert.Equal(20m, payment.Amount);
        Assert.Equal(1000m, payment.AmountBsS);

        var bytes = CreditSaleReceiptPdfGenerator.BuildPdf(receipt, MoneyDisplayFormat.Venezuelan);
        Assert.NotEmpty(bytes);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        var content = Encoding.ASCII.GetString(bytes);
        Assert.Contains("VENTA A CREDITO", content);
        Assert.Contains("SALDO FINANCIADO", content);
        Assert.Contains("80,00", content);
        Assert.Contains($"#{sale.InvoiceNumber!.Value:D5}", content);
    }

    [Fact]
    public async Task CreditSaleReceipt_ServiceWithoutReceivable_ReturnsEmptyContextFlag()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var sale = await SeedPlainSaleAsync(context);
        var receiptService = new SalesReceiptService(context);

        var data = await receiptService.GetCreditSaleReceiptDataAsync(sale.Id);

        Assert.NotNull(data);
        Assert.False(data!.HasReceivable);
        Assert.Null(data.Context);
        Assert.Null(await receiptService.GetCreditSaleReceiptDataAsync(999999));
    }

    [Fact]
    public async Task GetCreditSaleReceipt_Controller_OwnershipNotFoundAndNonCreditRules()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var creditCustomer = new Customer
        {
            CedulaOrRif = "V-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Cliente Credito POS",
            IsCreditEnabled = true,
            CreditLimitUSD = 1000m,
            PaymentTermDays = 30,
            CreditStatus = CreditStatus.Active,
            IsActive = true
        };
        context.Customers.Add(creditCustomer);
        await context.SaveChangesAsync();

        var (creditSale, _) = await SeedCompletedCreditSaleAsync(context, creditCustomer, cashierId: 7);
        var plainSale = await SeedPlainSaleAsync(context, cashierId: 7);

        // Cajero dueño de la venta: descarga el PDF.
        var owner = CreateReceiptsController(context, "7", "Cashier");
        var file = Assert.IsType<FileContentResult>(await owner.GetCreditSaleReceiptAsync(creditSale.Id, CancellationToken.None));
        Assert.Equal("application/pdf", file.ContentType);
        Assert.StartsWith("Comprobante_Venta_Credito_", file.FileDownloadName);
        Assert.NotEmpty(file.FileContents!);

        // Otro cajero: 403.
        var intruder = CreateReceiptsController(context, "8", "Cashier");
        var forbidden = await intruder.GetCreditSaleReceiptAsync(creditSale.Id, CancellationToken.None);
        Assert.Equal(403, Assert.IsType<ObjectResult>(forbidden).StatusCode);

        // Elevado: bypass.
        var admin = CreateReceiptsController(context, "99", "Admin");
        Assert.IsType<FileContentResult>(await admin.GetCreditSaleReceiptAsync(creditSale.Id, CancellationToken.None));

        // Venta inexistente: 404. Venta sin CxC: 400.
        var missing = await owner.GetCreditSaleReceiptAsync(999999, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(missing);
        var notCredit = await owner.GetCreditSaleReceiptAsync(plainSale.Id, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(notCredit);
    }

    // ---------------------------------------------------------------- helpers de controlador

    private static CreditOverLimitRequiresAuthorizationException BuildSignal(
        int saleId,
        decimal financedUsd = FinancedUSD,
        decimal availableUsd = AvailableCreditUSD) =>
        new(saleId, CustomerId, "Maria Sobregiro", financedUsd, availableUsd,
            $"Crédito insuficiente: disponible ${availableUsd:F2} y el monto a financiar es ${financedUsd:F2}.");

    private static (SalesController Controller, Mock<ISalesService> Service) CreateController(
        IAuthorizationCoordinator? coordinator,
        ISalesService? salesService = null)
    {
        var serviceMock = new Mock<ISalesService>();
        serviceMock
            .Setup(instance => instance.GetSaleAuthMetadataAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("Pending", (int?)CashierId));

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(instance => instance.UserId).Returns(CashierId.ToString(CultureInfo.InvariantCulture));

        var controller = new SalesController(
            salesService ?? serviceMock.Object,
            currentUser.Object,
            idempotencyService: null,
            authorizationCoordinator: coordinator,
            checkoutCalculationService: null);
        AttachUser(controller, CashierId, "Cashier");
        return (controller, serviceMock);
    }

    private static void AttachUser(ControllerBase controller, int userId, string role)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Role, role)
        };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "mock"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
    }

    private static ReceiptsController CreateReceiptsController(SalesDbContext context, string userId, string role)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(instance => instance.UserId).Returns(userId);

        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(instance => instance.GetSettingAsync("CurrencyFormat")).ReturnsAsync("Venezuelan");

        var controller = new ReceiptsController(new SalesReceiptService(context), new SaleReceiptRenderer(), currentUser.Object, settings.Object);
        AttachUser(controller, int.Parse(userId, CultureInfo.InvariantCulture), role);
        return controller;
    }

    // ---------------------------------------------------------------- harness de servicios

    private static SalesService CreateSalesHarness(SalesDbContext context)
    {
        var inventory = new Mock<IInventoryService>();
        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer
            .Setup(drawer => drawer.GetOrCreateActiveSessionAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        return new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            cashDrawer.Object,
            new Mock<ISystemSettingsService>().Object,
            creditService: new CreditService(context));
    }

    private static SalesService CreateSalesService(SalesDbContext context)
    {
        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(instance => instance.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer
            .Setup(drawer => drawer.GetOrCreateActiveSessionAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        return new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            cashDrawer.Object,
            new Mock<ISystemSettingsService>().Object,
            creditService: new CreditService(context));
    }

    // ---------------------------------------------------------------- stack de autorizaciones (SQLite)

    private sealed class RecordingAuthorizationNotifier : IAuthorizationNotifier
    {
        public List<AuthorizationRequestDto> CreatedRequests { get; } = new();
        public List<(AuthorizationRequestDto Request, string? Token)> ResolvedNotifications { get; } = new();
        public List<AuthorizationRequestDto> ExpiredRequests { get; } = new();

        public Task NotifyRequestCreatedAsync(AuthorizationRequestDto request, CancellationToken cancellationToken = default)
        {
            CreatedRequests.Add(request);
            return Task.CompletedTask;
        }

        public Task NotifyResolvedAsync(AuthorizationRequestDto request, string? token, CancellationToken cancellationToken = default)
        {
            ResolvedNotifications.Add((request, token));
            return Task.CompletedTask;
        }

        public Task NotifyExpiredAsync(AuthorizationRequestDto request, CancellationToken cancellationToken = default)
        {
            ExpiredRequests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class CoordinatorStack : IDisposable
    {
        private readonly SqliteConnection _connection;

        public SalesDbContext Db { get; }
        public AuthorizationService Service { get; }
        public AuthorizationTokenService TokenService { get; }
        public RecordingAuthorizationNotifier Notifier { get; } = new();
        public AuthorizationCoordinator Coordinator { get; }

        public CoordinatorStack()
        {
            (Db, _connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
            Service = new AuthorizationService(Db, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            TokenService = CreateTokenService();
            Coordinator = new AuthorizationCoordinator(Service, TokenService, Notifier, Db);
        }

        public async Task<Sale> SeedPendingSaleAsync(int cashierId, decimal unitPriceUsd = 100m, int? customerId = null)
        {
            // Sale.CashierId tiene FK a Users: sin la fila SQLite rechaza la venta.
            if (!Db.Users.Local.Any(user => user.Id == cashierId) && !await Db.Users.AnyAsync(user => user.Id == cashierId))
            {
                Db.Users.Add(new User
                {
                    Id = cashierId,
                    Cedula = $"V-{cashierId:D8}",
                    Name = $"Cajero {cashierId}",
                    Username = $"usuario{cashierId}",
                    FullName = $"Cajero {cashierId}",
                    Role = UserRole.Cashier
                });
            }

            var sale = new Sale
            {
                CashierId = cashierId,
                CustomerId = customerId,
                Status = SaleStatus.Pending,
                DeliveryStatus = SaleDeliveryStatus.PendingPickup,
                PriceListType = "Retail"
            };
            sale.Items.Add(new SaleItem
            {
                ProductId = 101,
                ProductName = "Harina Pan",
                Quantity = 1m,
                UnitPrice = unitPriceUsd,
                IsCustomPrice = false
            });
            Db.Sales.Add(sale);
            await Db.SaveChangesAsync();
            return sale;
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    private static RequestAuthorizationContract BuildCreditContract(int saleId) => new()
    {
        SaleId = saleId,
        ActionType = AuthorizationActionType.CreditOverLimit,
        CustomerId = CustomerId,
        CustomerName = "Maria Sobregiro",
        FinancedUSD = FinancedUSD,
        AvailableCreditUSD = AvailableCreditUSD,
        Terminal = "Caja-03"
    };

    private static async Task<int> CreateApprovedCreditRequestAsync(CoordinatorStack stack, int saleId)
    {
        var created = await stack.Coordinator.CreateAsync(
            BuildCreditContract(saleId),
            CashierId,
            "Cajero 70",
            UserRole.Cashier);
        Assert.Equal(CreateAuthorizationOutcome.Created, created.Outcome);

        var resolved = await stack.Coordinator.ResolveAsync(created.Request!.Id, AdminId, "Admin Dos", approved: true);
        Assert.Equal(ResolveAuthorizationOutcome.Resolved, resolved.Outcome);
        return created.Request.Id;
    }

    // ---------------------------------------------------------------- semillas

    private static async Task<Customer> SeedCustomerAsync(
        SalesDbContext context,
        decimal creditLimitUsd = 1000m,
        int paymentTermDays = 30,
        int creditGraceDays = 3,
        bool isCreditEnabled = true,
        CreditStatus creditStatus = CreditStatus.Active)
    {
        var customer = new Customer
        {
            CedulaOrRif = "V-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Cliente Credito POS",
            IsCreditEnabled = isCreditEnabled,
            CreditLimitUSD = creditLimitUsd,
            PaymentTermDays = paymentTermDays,
            CreditGraceDays = creditGraceDays,
            CreditStatus = creditStatus,
            IsActive = true
        };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        return customer;
    }

    private static async Task<Sale> SeedPendingSaleAsync(
        SalesDbContext context,
        int? customerId,
        decimal unitPriceUsd,
        int? cashierId = null)
    {
        var sale = new Sale
        {
            CustomerId = customerId,
            CashierId = cashierId,
            Status = SaleStatus.Pending,
            DeliveryStatus = SaleDeliveryStatus.PendingPickup,
            PriceListType = "Retail"
        };
        sale.Items.Add(new SaleItem
        {
            ProductId = 101,
            ProductName = "Harina Pan",
            Quantity = 1m,
            UnitPrice = unitPriceUsd,
            IsCustomPrice = false
        });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();
        return sale;
    }

    private static async Task SeedOverdueReceivableAsync(SalesDbContext context, int customerId, decimal balanceUsd, int daysAgo)
    {
        var previousSale = new Sale
        {
            CustomerId = customerId,
            Status = SaleStatus.Completed,
            InvoiceNumber = 9001,
            Date = DateTime.UtcNow.AddDays(-daysAgo),
            AppliedRate = 50m,
            TotalUSD = balanceUsd,
            TotalBsS = Math.Round(balanceUsd * 50m, 2, MidpointRounding.AwayFromZero)
        };
        context.Sales.Add(previousSale);
        await context.SaveChangesAsync();

        context.Receivables.Add(new Receivable
        {
            SaleId = previousSale.Id,
            CustomerId = customerId,
            OriginalUSD = balanceUsd,
            BalanceUSD = balanceUsd,
            OriginalBsS = Math.Round(balanceUsd * 50m, 2, MidpointRounding.AwayFromZero),
            HistoricalRate = 50m,
            IssuedAtUtc = DateTime.UtcNow.AddDays(-daysAgo),
            DueDateUtc = DateTime.UtcNow.AddDays(-daysAgo),
            Status = ReceivableStatus.Vigente
        });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Venta a crédito completada: total $100 / Bs.S 5.000, abono inicial $20 / Bs.S 1.000 y saldo
    /// financiado $80 / Bs.S 4.000 a tasa de emisión 50 con vencimiento a 30 días.
    /// </summary>
    private static async Task<(Sale Sale, Receivable Receivable)> SeedCompletedCreditSaleAsync(
        SalesDbContext context,
        Customer? customer = null,
        int? cashierId = null)
    {
        if (customer == null)
        {
            customer = new Customer
            {
                CedulaOrRif = "V-" + Guid.NewGuid().ToString("N")[..8],
                Name = "Cliente Credito POS",
                IsCreditEnabled = true,
                CreditLimitUSD = 1000m,
                PaymentTermDays = 30,
                CreditStatus = CreditStatus.Active,
                IsActive = true
            };
            context.Customers.Add(customer);
            await context.SaveChangesAsync();
        }

        var issuedAtUtc = DateTime.UtcNow.AddDays(-1);
        var sale = new Sale
        {
            CustomerId = customer.Id,
            CashierId = cashierId,
            CustomerName = customer.Name,
            CustomerCedula = customer.CedulaOrRif,
            Status = SaleStatus.Completed,
            DeliveryStatus = SaleDeliveryStatus.Delivered,
            PriceListType = "Retail",
            InvoiceNumber = 9876,
            Date = issuedAtUtc,
            AppliedRate = 50m,
            Subtotal = 100m,
            TotalUSD = 100m,
            TotalBsS = 5000m,
            FinalPaidAmountBsS = 1000m
        };
        sale.Items.Add(new SaleItem
        {
            ProductId = 101,
            ProductName = "Harina Pan",
            Quantity = 1m,
            UnitPrice = 100m,
            Subtotal = 100m,
            UnitPriceBsS = 5000m,
            SubtotalBsS = 5000m,
            IsCustomPrice = false
        });
        sale.Payments.Add(new SalePayment
        {
            PaymentMethod = new PaymentMethod { Name = "Efectivo USD", IsCash = true, IsActive = true },
            Amount = 20m,
            AmountBsS = 1000m,
            ExchangeRate = 50m,
            CreatedAt = issuedAtUtc
        });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var receivable = new Receivable
        {
            SaleId = sale.Id,
            CustomerId = customer.Id,
            OriginalUSD = 80m,
            BalanceUSD = 80m,
            OriginalBsS = 4000m,
            HistoricalRate = 50m,
            IssuedAtUtc = issuedAtUtc,
            DueDateUtc = issuedAtUtc.AddDays(30),
            Status = ReceivableStatus.Vigente
        };
        context.Receivables.Add(receivable);
        await context.SaveChangesAsync();

        return (sale, receivable);
    }

    private static async Task<Sale> SeedPlainSaleAsync(SalesDbContext context, int? cashierId = null)
    {
        var sale = new Sale
        {
            CashierId = cashierId,
            Status = SaleStatus.Completed,
            DeliveryStatus = SaleDeliveryStatus.Delivered,
            PriceListType = "Retail",
            InvoiceNumber = 9877,
            Date = DateTime.UtcNow,
            AppliedRate = 50m,
            Subtotal = 10m,
            TotalUSD = 10m,
            TotalBsS = 500m,
            FinalPaidAmountBsS = 500m
        };
        sale.Items.Add(new SaleItem
        {
            ProductId = 102,
            ProductName = "Cafe molido",
            Quantity = 1m,
            UnitPrice = 10m,
            Subtotal = 10m,
            UnitPriceBsS = 500m,
            SubtotalBsS = 500m,
            IsCustomPrice = false
        });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();
        return sale;
    }
}
