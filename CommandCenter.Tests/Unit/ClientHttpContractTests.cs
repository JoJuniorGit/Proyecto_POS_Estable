using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Backend.API.Controllers;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;
using static CommandCenter.Tests.Unit.ClientHttpContractGuard;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Guardas de costura cliente/servidor para los call sites HTTP de los servicios y ViewModels del
/// cliente WPF (SEAM-01). Por cada llamada: (a) la ruta y el metodo HTTP deben coincidir con la accion
/// del controlador que la sirve; el literal del test se conserva por legibilidad y ademas se contrasta
/// mecanicamente contra la plantilla derivada de los atributos [Route]/HttpGet/HttpPost/... reales del
/// controlador, de modo que una deriva de ruta o de metodo en el backend sin actualizar al cliente pone
/// la guarda en rojo; y (b) toda clave de query enviada debe ser bindeable por esa accion
/// ([FromQuery]/[FromRoute], parametros simples o placeholders de ruta). Complementa a
/// ProductServiceQueryContractTests (contrato de query de GetPagedAsync y guardas de shape/
/// entity-boundary); aqui quedan ancladas tambien rutas y query del resto del catalogo. Nace del mismo
/// modo de falla que el bug 8.142 (cliente envia "status", backend bindea
/// "statusFilter": filtro ignorado en silencio, sin error y sin test rojo).
/// Excepciones documentadas (no se falsean guardas):
/// - SubnetScannerService: sondea hosts LAN arbitrarios; no esta atado a un controlador.
/// - ExchangeRateService (SignalR): el handshake de "hubs/exchange-rate" lo sirve un Hub, no una accion MVC.
/// - ProductImportService.GenerateTemplateAsync/ReadHeadersAsync/ParseFileWithMappingAsync: no emiten HTTP.
/// VersionCheckService SI queda cubierto: "api/system/version-check" pertenece a VersionCheckController.
/// PairingQrViewModel.InitializeAsync SI queda cubierto: "api/pairing/info" pertenece a PairingController.
/// </summary>
public class ClientHttpContractTests
{
    // ── SalesService (Desktop.Client) ──────────────────────────────────────

    [Fact]
    public async Task Sales_GetSaleAsync_TargetsSalesControllerRoute()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.GetSaleAsync(42));

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/sales/42", typeof(SalesController), nameof(SalesController.GetSaleAsync));
    }

    [Fact]
    public async Task Sales_GetReceiptAsync_TargetsReceiptsControllerRoute()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.GetReceiptAsync(42));

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/sales/42/receipt", typeof(ReceiptsController), nameof(ReceiptsController.GetReceiptAsync));
    }

    [Fact]
    public async Task Sales_StartSaleAsync_SendsOnlyBindableCashierId()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.StartSaleAsync(7));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/sales/start", typeof(SalesController), nameof(SalesController.StartSaleAsync), "cashierId");
    }

    [Fact]
    public async Task Sales_AddItemAsync_TargetsItemsRoute()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.AddItemAsync(42, 5, 2m, 36.5m));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/sales/42/items", typeof(SalesController), nameof(SalesController.AddItemAsync));
    }

    [Fact]
    public async Task Sales_RemoveItemAsync_SendsOnlyBindableExchangeRate()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.RemoveItemAsync(42, 9, 36.5m));

        AssertRequest(Assert.Single(requests), HttpMethod.Delete, "/api/sales/42/items/9", typeof(SalesController), nameof(SalesController.RemoveItemAsync), "exchangeRate");
    }

    [Fact]
    public async Task Sales_UpdateItemQuantityAsync_TargetsItemRoute()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.UpdateItemQuantityAsync(42, 9, 3m, 36.5m));

        AssertRequest(Assert.Single(requests), HttpMethod.Put, "/api/sales/42/items/9", typeof(SalesController), nameof(SalesController.UpdateItemQuantityAsync));
    }

    [Fact]
    public async Task Sales_UpdateExchangeRateAsync_SendsOnlyBindableExchangeRate()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.UpdateExchangeRateAsync(42, 36.5m));

        AssertRequest(Assert.Single(requests), HttpMethod.Put, "/api/sales/42/exchange-rate", typeof(SalesController), nameof(SalesController.UpdateExchangeRateAsync), "exchangeRate");
    }

    [Fact]
    public async Task Sales_UpdatePriceListAsync_TargetsPriceListRoute()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.UpdatePriceListAsync(42, "Wholesale"));

        AssertRequest(Assert.Single(requests), HttpMethod.Put, "/api/sales/42/price-list", typeof(SalesController), nameof(SalesController.UpdatePriceListAsync));
    }

    [Fact]
    public async Task Sales_CompleteSaleAsync_TargetsCompleteRoute()
    {
        var requests = await CaptureAsync<SalesService>(
            c => new SalesService(c),
            s => s.CompleteSaleAsync(42, 36.5m, new[] { new Desktop.Client.Services.SalePaymentDto(1, 10m, 365m, null) }, idempotencyKey: "abc123"),
            payload: "123");

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/sales/42/complete", typeof(SalesController), nameof(SalesController.CompleteSaleAsync));
    }

    [Fact]
    public async Task Sales_GetSalesHistoryAsync_SendsOnlyBindableHistoryFilters()
    {
        var requests = await CaptureAsync<SalesService>(
            c => new SalesService(c),
            s => s.GetSalesHistoryAsync(
                3,
                12,
                new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc),
                "Ana"));

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/sales/history", typeof(SalesController), nameof(SalesController.GetHistoryAsync), "page", "pageSize", "startDate", "endDate", "search");
    }

    [Fact]
    public async Task Sales_GetSaleHistoryDetailAsync_TargetsHistoryDetailRoute()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.GetSaleHistoryDetailAsync(42));

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/sales/42/history-detail", typeof(SalesController), nameof(SalesController.GetHistoryDetailAsync));
    }

    [Fact]
    public async Task Sales_HoldSaleAsync_TargetsHoldRoute()
    {
        var requests = await CaptureAsync<SalesService>(
            c => new SalesService(c),
            s => s.HoldSaleAsync(42, new HoldSaleRequestDto { CustomerId = 3, ExchangeRate = 36.5m }));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/sales/42/hold", typeof(SalesController), nameof(SalesController.HoldSaleAsync));
    }

    [Fact]
    public async Task Sales_AddPaymentToHoldSaleAsync_TargetsPaymentsRoute()
    {
        var requests = await CaptureAsync<SalesService>(
            c => new SalesService(c),
            s => s.AddPaymentToHoldSaleAsync(42, new AddPaymentRequestDto { PaymentMethodId = 1, AmountBsS = 365m, ExchangeRate = 36.5m }));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/sales/42/payments", typeof(SalesController), nameof(SalesController.AddPaymentAsync));
    }

    [Fact]
    public async Task Sales_ClaimSaleAsync_TargetsClaimRoute()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.ClaimSaleAsync(42, "Editing"));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/sales/42/claim", typeof(SalesController), nameof(SalesController.ClaimSaleAsync));
    }

    [Fact]
    public async Task Sales_ReleaseSaleAsync_SendsOnlyBindableForce()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.ReleaseSaleAsync(42, force: true));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/sales/42/release", typeof(SalesController), nameof(SalesController.ReleaseSaleAsync), "force");
    }

    [Fact]
    public async Task Sales_GetPendingSalesPagedAsync_SendsOnlyBindablePaging()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.GetPendingSalesPagedAsync(50, 10), payload: "[]");

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/sales/pending", typeof(SalesController), nameof(SalesController.GetPendingSalesAsync), "limit", "offset");
    }

    [Fact]
    public async Task Sales_GetCustomersAsync_SendsOnlyBindableCustomerFilters()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.GetCustomersAsync("Ana", 2, 15, recentOnly: true));

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/sales/customers", typeof(SalesController), nameof(SalesController.GetCustomersAsync), "query", "page", "pageSize", "recentOnly");
    }

    [Fact]
    public async Task Sales_CreateCustomerAsync_PostsToCustomersRoute()
    {
        var requests = await CaptureAsync<SalesService>(
            c => new SalesService(c),
            s => s.CreateCustomerAsync(new CreateCustomerDto { CedulaOrRif = "V-11111111", Name = "Pedro" }));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/sales/customers", typeof(SalesController), nameof(SalesController.CreateCustomerAsync));
    }

    [Fact]
    public async Task Sales_UpdateCustomerAsync_PutsToCustomerIdRoute()
    {
        var requests = await CaptureAsync<SalesService>(
            c => new SalesService(c),
            s => s.UpdateCustomerAsync(11, new UpdateCustomerDto { CedulaOrRif = "V-11111111", Name = "Pedro Editado" }));

        AssertRequest(Assert.Single(requests), HttpMethod.Put, "/api/sales/customers/11", typeof(SalesController), nameof(SalesController.UpdateCustomerAsync));
    }

    [Fact]
    public async Task Sales_DeleteCustomerAsync_DeletesCustomerIdRoute()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.DeleteCustomerAsync(11));

        AssertRequest(Assert.Single(requests), HttpMethod.Delete, "/api/sales/customers/11", typeof(SalesController), nameof(SalesController.DeleteCustomerAsync));
    }

    [Fact]
    public async Task Sales_GetDefaultCustomerAsync_TargetsDefaultCustomerRoute()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.GetDefaultCustomerAsync());

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/sales/customers/default", typeof(SalesController), nameof(SalesController.GetDefaultCustomerAsync));
    }

    [Fact]
    public async Task Sales_UpdateSaleCustomerAsync_TargetsSaleCustomerRoute()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.UpdateSaleCustomerAsync(42, 11));

        AssertRequest(Assert.Single(requests), HttpMethod.Put, "/api/sales/42/customer", typeof(SalesController), nameof(SalesController.UpdateSaleCustomerAsync));
    }

    [Fact]
    public async Task Sales_GetPendingPickupsPagedAsync_SendsOnlyBindablePaging()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.GetPendingPickupsPagedAsync(50, 10), payload: "[]");

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/sales/pending-pickups", typeof(SalesController), nameof(SalesController.GetPendingPickupsAsync), "limit", "offset");
    }

    [Fact]
    public async Task Sales_ConfirmPickupAsync_TargetsConfirmPickupRoute()
    {
        var requests = await CaptureAsync<SalesService>(c => new SalesService(c), s => s.ConfirmPickupAsync(42));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/sales/42/confirm-pickup", typeof(SalesController), nameof(SalesController.ConfirmPickupAsync));
    }

    [Fact]
    public async Task Sales_UpdateSaleItemsAsync_PutsToSaleItemsRoute()
    {
        var requests = await CaptureAsync<SalesService>(
            c => new SalesService(c),
            s => s.UpdateSaleItemsAsync(42, new[] { new UpdateSaleItemDto { SaleItemId = 1, ProductId = 5, Quantity = 2m, UnitPrice = 6.17m } }, 36.5m));

        AssertRequest(Assert.Single(requests), HttpMethod.Put, "/api/sales/42/items", typeof(SalesController), nameof(SalesController.UpdateSaleItemsAsync));
    }

    // ── ExchangeRateService ────────────────────────────────────────────────

    [Fact]
    public async Task ExchangeRate_GetCurrentRateAsync_TargetsTodayRoute()
    {
        await AssertExchangeRateCallAsync(
            s => s.GetCurrentRateAsync(),
            HttpMethod.Get,
            "/api/exchange-rate/today",
            nameof(ExchangeRateController.GetTodayAsync));
    }

    [Fact]
    public async Task ExchangeRate_SaveRateAsync_PostsToRatesRoute()
    {
        await AssertExchangeRateCallAsync(
            s => s.SaveRateAsync(36.5m),
            HttpMethod.Post,
            "/api/exchange-rate",
            nameof(ExchangeRateController.UpsertRateAsync));
    }

    [Fact]
    public async Task ExchangeRate_GetHistoryAsync_TargetsHistoryRoute()
    {
        await AssertExchangeRateCallAsync(
            s => s.GetHistoryAsync(),
            HttpMethod.Get,
            "/api/exchange-rate/history",
            nameof(ExchangeRateController.GetHistoryAsync));
    }

    [Fact]
    public async Task ExchangeRate_SyncBcvAsync_TargetsSyncBcvRoute()
    {
        await AssertExchangeRateCallAsync(
            s => s.SyncBcvAsync(),
            HttpMethod.Post,
            "/api/exchange-rate/sync-bcv",
            nameof(ExchangeRateController.SyncBcvAsync));
    }

    // ── UserService ────────────────────────────────────────────────────────

    [Fact]
    public async Task User_LoginAsync_PostsToLoginRoute()
    {
        var requests = await CaptureAsync<UserService>(c => new UserService(c), s => s.LoginAsync("V-1", "clave"));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/auth/login", typeof(AuthController), nameof(AuthController.LoginAsync));
    }

    [Fact]
    public async Task User_CheckSessionStatusAsync_GetsMeRoute()
    {
        var requests = await CaptureAsync<UserService>(c => new UserService(c), s => s.CheckSessionStatusAsync());

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/auth/me", typeof(AuthController), nameof(AuthController.GetMeAsync));
    }

    [Fact]
    public async Task User_ChangePasswordAsync_PostsToChangePasswordRoute()
    {
        var requests = await CaptureAsync<UserService>(c => new UserService(c), s => s.ChangePasswordAsync("V-1", "vieja", "nueva"));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/auth/change-password", typeof(AuthController), nameof(AuthController.ChangePasswordAsync));
    }

    [Fact]
    public async Task User_GetUsersAsync_GetsUsersRoute()
    {
        var requests = await CaptureAsync<UserService>(c => new UserService(c), s => s.GetUsersAsync(), payload: "[]");

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/users", typeof(UsersController), nameof(UsersController.GetUsersAsync));
    }

    [Fact]
    public async Task User_CreateUserAsync_PostsToUsersRoute()
    {
        var requests = await CaptureAsync<UserService>(
            c => new UserService(c),
            s => s.CreateUserAsync(new CreateUserDto { Cedula = "V-22222222", Name = "Nuevo" }));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/users", typeof(UsersController), nameof(UsersController.CreateUserAsync));
    }

    [Fact]
    public async Task User_UpdateUserAsync_PutsToUserIdRoute()
    {
        var requests = await CaptureAsync<UserService>(
            c => new UserService(c),
            s => s.UpdateUserAsync(5, new UpdateUserDto { Cedula = "V-22222222", Name = "Editado" }));

        AssertRequest(Assert.Single(requests), HttpMethod.Put, "/api/users/5", typeof(UsersController), nameof(UsersController.UpdateUserAsync));
    }

    [Fact]
    public async Task User_SoftDeleteUserAsync_DeletesUserIdRoute()
    {
        var requests = await CaptureAsync<UserService>(c => new UserService(c), s => s.SoftDeleteUserAsync(5));

        AssertRequest(Assert.Single(requests), HttpMethod.Delete, "/api/users/5", typeof(UsersController), nameof(UsersController.SoftDeleteUserAsync));
    }

    [Fact]
    public async Task User_ReactivateUserAsync_PostsToReactivateRoute()
    {
        var requests = await CaptureAsync<UserService>(c => new UserService(c), s => s.ReactivateUserAsync(5));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/users/5/reactivate", typeof(UsersController), nameof(UsersController.ReactivateUserAsync));
    }

    [Fact]
    public async Task User_PermanentDeleteUserAsync_DeletesPermanentRoute()
    {
        var requests = await CaptureAsync<UserService>(c => new UserService(c), s => s.PermanentDeleteUserAsync(5));

        AssertRequest(Assert.Single(requests), HttpMethod.Delete, "/api/users/5/permanent", typeof(UsersController), nameof(UsersController.HardDeleteUserAsync));
    }

    // ── CashDrawerService ──────────────────────────────────────────────────

    [Fact]
    public async Task CashDrawer_GetActiveSessionAsync_GetsActiveSessionRoute()
    {
        var requests = await CaptureAsync<CashDrawerService>(c => new CashDrawerService(c), s => s.GetActiveSessionAsync());

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/cashdrawer/active-session", typeof(CashDrawerController), nameof(CashDrawerController.GetActiveSessionAsync));
    }

    [Fact]
    public async Task CashDrawer_OpenSessionAsync_PostsToOpenRoute()
    {
        var requests = await CaptureAsync<CashDrawerService>(c => new CashDrawerService(c), s => s.OpenSessionAsync(150.25m, 36.5m));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/cashdrawer/open", typeof(CashDrawerController), nameof(CashDrawerController.OpenSessionAsync));
    }

    [Fact]
    public async Task CashDrawer_CloseSessionAsync_PostsToCloseRoute()
    {
        var requests = await CaptureAsync<CashDrawerService>(c => new CashDrawerService(c), s => s.CloseSessionAsync(200m, 36.5m));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/cashdrawer/close", typeof(CashDrawerController), nameof(CashDrawerController.CloseSessionAsync));
    }

    [Fact]
    public async Task CashDrawer_GetCurrentBalanceLocalAsync_SendsOnlyBindableSessionId()
    {
        var requests = await CaptureAsync<CashDrawerService>(c => new CashDrawerService(c), s => s.GetCurrentBalanceLocalAsync(5), payload: "15.5");

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/cashdrawer/current-balance", typeof(CashDrawerController), nameof(CashDrawerController.GetCurrentBalanceAsync), "sessionId");
    }

    [Fact]
    public async Task CashDrawer_GetHistoryAsync_SendsOnlyBindableLimit()
    {
        var requests = await CaptureAsync<CashDrawerService>(c => new CashDrawerService(c), s => s.GetHistoryAsync(120), payload: "[]");

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/cashdrawer/history", typeof(CashDrawerController), nameof(CashDrawerController.GetHistoryAsync), "limit");
    }

    [Fact]
    public async Task CashDrawer_AddTransactionAsync_PostsToTransactionRoute()
    {
        var requests = await CaptureAsync<CashDrawerService>(
            c => new CashDrawerService(c),
            s => s.AddTransactionAsync(5, 10m, CashTransactionType.Income, CashTransactionSource.ManualAdjustment, "Ajuste", 36.5m));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/cashdrawer/transaction", typeof(CashDrawerController), nameof(CashDrawerController.AddTransactionAsync));
    }

    [Fact]
    public async Task CashDrawer_GetAdvanceCommissionAsync_SendsOnlyBindableIsTransfer()
    {
        var requests = await CaptureAsync<CashDrawerService>(c => new CashDrawerService(c), s => s.GetAdvanceCommissionAsync(true));

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/cashdrawer/advance-commission", typeof(CashDrawerController), nameof(CashDrawerController.GetAdvanceCommissionAsync), "isTransfer");
    }

    [Fact]
    public async Task CashDrawer_ProcessCashAdvanceAsync_PostsToCashAdvanceRoute()
    {
        var requests = await CaptureAsync<CashDrawerService>(
            c => new CashDrawerService(c),
            s => s.ProcessCashAdvanceAsync(5, 100m, 2, "Pago Movil", false, 36.5m));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/cashdrawer/cash-advance", typeof(CashDrawerController), nameof(CashDrawerController.ProcessCashAdvanceAsync));
    }

    // ── SettingsService ────────────────────────────────────────────────────

    [Fact]
    public async Task Settings_GetTimeZoneAsync_GetsTimeZoneRoute()
    {
        var requests = await CaptureAsync<SettingsService>(c => new SettingsService(c), s => s.GetTimeZoneAsync());

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/settings/timezone", typeof(SettingsController), nameof(SettingsController.GetTimeZoneAsync));
    }

    [Fact]
    public async Task Settings_SetTimeZoneAsync_PostsToTimeZoneRoute()
    {
        var requests = await CaptureAsync<SettingsService>(c => new SettingsService(c), s => s.SetTimeZoneAsync("America/Caracas"));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/settings/timezone", typeof(SettingsController), nameof(SettingsController.SetTimeZoneAsync));
    }

    [Fact]
    public async Task Settings_GetCurrencyFormatAsync_GetsCurrencyFormatRoute()
    {
        var requests = await CaptureAsync<SettingsService>(c => new SettingsService(c), s => s.GetCurrencyFormatAsync());

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/settings/currency-format", typeof(SettingsController), nameof(SettingsController.GetCurrencyFormatAsync));
    }

    [Fact]
    public async Task Settings_SetCurrencyFormatAsync_PutsToCurrencyFormatRoute()
    {
        var requests = await CaptureAsync<SettingsService>(c => new SettingsService(c), s => s.SetCurrencyFormatAsync("International"));

        AssertRequest(Assert.Single(requests), HttpMethod.Put, "/api/settings/currency-format", typeof(SettingsController), nameof(SettingsController.SetCurrencyFormatAsync));
    }

    [Fact]
    public async Task Settings_GetAllowNegativeStockAsync_GetsAllowNegativeStockRoute()
    {
        var requests = await CaptureAsync<SettingsService>(c => new SettingsService(c), s => s.GetAllowNegativeStockAsync());

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/settings/allow-negative-stock", typeof(SettingsController), nameof(SettingsController.GetAllowNegativeStockAsync));
    }

    [Fact]
    public async Task Settings_SetAllowNegativeStockAsync_PutsToAllowNegativeStockRoute()
    {
        var requests = await CaptureAsync<SettingsService>(c => new SettingsService(c), s => s.SetAllowNegativeStockAsync(true));

        AssertRequest(Assert.Single(requests), HttpMethod.Put, "/api/settings/allow-negative-stock", typeof(SettingsController), nameof(SettingsController.SetAllowNegativeStockAsync));
    }

    [Fact]
    public async Task Settings_RestartSystemAsync_PostsToAdministrationRestartRoute()
    {
        var requests = await CaptureAsync<SettingsService>(c => new SettingsService(c), s => s.RestartSystemAsync());

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/administration/restart", typeof(AdministrationController), nameof(AdministrationController.RestartSystem));
    }

    // ── PaymentService ─────────────────────────────────────────────────────

    [Fact]
    public async Task Payment_GetActiveMethodsAsync_GetsActiveRoute()
    {
        var requests = await CaptureAsync<PaymentService>(c => new PaymentService(c), s => s.GetActiveMethodsAsync(), payload: "[]");

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/PaymentMethods/active", typeof(PaymentMethodsController), nameof(PaymentMethodsController.GetActiveMethodsAsync));
    }

    [Fact]
    public async Task Payment_GetAllMethodsAsync_GetsPaymentMethodsRoute()
    {
        var requests = await CaptureAsync<PaymentService>(c => new PaymentService(c), s => s.GetAllMethodsAsync(), payload: "[]");

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/PaymentMethods", typeof(PaymentMethodsController), nameof(PaymentMethodsController.GetAllMethodsAsync));
    }

    [Fact]
    public async Task Payment_CreateAsync_PostsToPaymentMethodsRoute()
    {
        var requests = await CaptureAsync<PaymentService>(
            c => new PaymentService(c),
            s => s.CreateAsync(new PaymentMethodDto { Id = 3, Name = "Transferencia" }));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/PaymentMethods", typeof(PaymentMethodsController), nameof(PaymentMethodsController.CreateMethodAsync));
    }

    [Fact]
    public async Task Payment_UpdateAsync_PutsToPaymentMethodIdRoute()
    {
        var requests = await CaptureAsync<PaymentService>(
            c => new PaymentService(c),
            s => s.UpdateAsync(new PaymentMethodDto { Id = 3, Name = "Transferencia" }));

        AssertRequest(Assert.Single(requests), HttpMethod.Put, "/api/PaymentMethods/3", typeof(PaymentMethodsController), nameof(PaymentMethodsController.UpdateMethodAsync));
    }

    [Fact]
    public async Task Payment_DeleteAsync_DeletesPaymentMethodIdRoute()
    {
        var requests = await CaptureAsync<PaymentService>(c => new PaymentService(c), s => s.DeleteAsync(3));

        AssertRequest(Assert.Single(requests), HttpMethod.Delete, "/api/PaymentMethods/3", typeof(PaymentMethodsController), nameof(PaymentMethodsController.DeleteMethodAsync));
    }

    // ── DailyClosureClientService ──────────────────────────────────────────

    [Fact]
    public async Task DailyClosure_GetExpectedTotalsAsync_SendsOnlyBindableDateUtc()
    {
        var requests = await CaptureAsync<DailyClosureClientService>(
            c => new DailyClosureClientService(c),
            s => s.GetExpectedTotalsAsync(new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc)),
            payload: "[]");

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/dailyclosure/expected-totals", typeof(DailyClosureController), nameof(DailyClosureController.GetExpectedTotalsAsync), "dateUtc");
    }

    [Fact]
    public async Task DailyClosure_CreateClosureAsync_PostsToDailyClosureRoute()
    {
        var requests = await CaptureAsync<DailyClosureClientService>(
            c => new DailyClosureClientService(c),
            s => s.CreateClosureAsync(new CreateClosureRequest { ClosureDate = new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc) }));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/dailyclosure", typeof(DailyClosureController), nameof(DailyClosureController.CreateClosureAsync));
    }

    // ── HealthPollingService ───────────────────────────────────────────────

    [Fact]
    public async Task Health_PollLoop_GetsHealthControllerRoute()
    {
        var handler = new CapturingHandler();
        using var client = CreateClient(handler);
        using var service = new HealthPollingService(client, pollInterval: TimeSpan.FromMilliseconds(10));

        service.StartPolling();
        await handler.WaitForCountAsync(1);

        AssertRequest(Assert.Single(handler.Requests), HttpMethod.Get, "/health", typeof(HealthController), nameof(HealthController.CheckHealthAsync));
    }

    // ── ProductService ─────────────────────────────────────────────────────

    [Fact]
    public async Task Product_GetByIdAsync_GetsProductIdRoute()
    {
        var requests = await CaptureAsync<ProductService>(c => new ProductService(c), s => s.GetByIdAsync(42));

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/products/42", typeof(ProductsController), nameof(ProductsController.GetByIdAsync));
    }

    [Fact]
    public async Task Product_CreateAsync_PostsToProductsRoute()
    {
        var requests = await CaptureAsync<ProductService>(
            c => new ProductService(c),
            s => s.CreateAsync(new CreateProductDto { Name = "Arroz", SKU = "ARROZ-1" }));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/products", typeof(ProductsController), nameof(ProductsController.CreateAsync));
    }

    [Fact]
    public async Task Product_UpdateAsync_PutsToProductIdRoute()
    {
        var requests = await CaptureAsync<ProductService>(
            c => new ProductService(c),
            s => s.UpdateAsync(new UpdateProductDto { Id = 42, Name = "Arroz editado" }));

        AssertRequest(Assert.Single(requests), HttpMethod.Put, "/api/products/42", typeof(ProductsController), nameof(ProductsController.UpdateAsync));
    }

    [Fact]
    public async Task Product_SetStatusAsync_PutsToStatusRoute()
    {
        var requests = await CaptureAsync<ProductService>(c => new ProductService(c), s => s.SetStatusAsync(42, isActive: true, isDeleted: false));

        AssertRequest(Assert.Single(requests), HttpMethod.Put, "/api/products/42/status", typeof(ProductsController), nameof(ProductsController.SetStatusAsync));
    }

    [Fact]
    public async Task Product_DeleteAsync_SendsOnlyBindableHardDelete()
    {
        var requests = await CaptureAsync<ProductService>(c => new ProductService(c), s => s.DeleteAsync(42, hardDelete: true));

        AssertRequest(Assert.Single(requests), HttpMethod.Delete, "/api/products/42", typeof(ProductsController), nameof(ProductsController.DeleteAsync), "hardDelete");
    }

    [Fact]
    public async Task Product_RestoreAsync_PostsToRestoreRoute()
    {
        var requests = await CaptureAsync<ProductService>(c => new ProductService(c), s => s.RestoreAsync(42));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/products/42/restore", typeof(ProductsController), nameof(ProductsController.RestoreAsync));
    }

    [Fact]
    public async Task Product_AdjustStockAsync_PostsToAdjustStockRoute()
    {
        var requests = await CaptureAsync<ProductService>(c => new ProductService(c), s => s.AdjustStockAsync(42, -3m, "Merma"));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/products/42/adjust-stock", typeof(ProductsController), nameof(ProductsController.AdjustStockAsync));
    }

    [Fact]
    public async Task Product_GetQuickInfoAsync_GetsQuickCheckRoute()
    {
        var requests = await CaptureAsync<ProductService>(c => new ProductService(c), s => s.GetQuickInfoAsync("ARROZ-1"));

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/products/quick-check/ARROZ-1", typeof(ProductsController), nameof(ProductsController.GetQuickInfoAsync));
    }

    [Fact]
    public async Task Product_GetSuggestionsAsync_SendsOnlyBindableSuggestionFilters()
    {
        var requests = await CaptureAsync<ProductService>(
            c => new ProductService(c),
            s => s.GetSuggestionsAsync("arroz", activeOnly: true, CancellationToken.None),
            payload: "[]");

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/products/suggestions", typeof(ProductsController), nameof(ProductsController.GetSuggestionsAsync), "filter", "activeOnly");
    }

    [Fact]
    public async Task Product_GetPagedAsync_SendsOnlyBindableCatalogFilters()
    {
        var requests = await CaptureAsync<ProductService>(
            c => new ProductService(c),
            s => s.GetPagedAsync("arroz", 2, 25, statusFilter: "active", sortBy: "name", isDescending: true));

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/products", typeof(ProductsController), nameof(ProductsController.GetAllAsync), "filter", "page", "pageSize", "statusFilter", "sortBy", "isDescending");
    }

    [Fact]
    public async Task Product_GetVariantsAsync_GetsVariantsRoute()
    {
        var requests = await CaptureAsync<ProductService>(c => new ProductService(c), s => s.GetVariantsAsync(42), payload: "[]");

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/products/42/variants", typeof(ProductsController), nameof(ProductsController.GetVariantsAsync));
    }

    [Fact]
    public async Task Product_GetParentsAsync_GetsParentsRoute()
    {
        var requests = await CaptureAsync<ProductService>(c => new ProductService(c), s => s.GetParentsAsync(), payload: "[]");

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/products/parents", typeof(ProductsController), nameof(ProductsController.GetParentsAsync));
    }

    [Fact]
    public async Task Product_GetCandidateVariantsPagedAsync_SendsOnlyBindablePagingAndFilter()
    {
        var requests = await CaptureAsync<ProductService>(
            c => new ProductService(c),
            s => s.GetCandidateVariantsPagedAsync(42, "arroz", 2, 25, CancellationToken.None));

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/products/42/candidate-variants", typeof(ProductsController), nameof(ProductsController.GetCandidateVariantsAsync), "filter", "page", "pageSize");
    }

    [Fact]
    public async Task Product_LinkVariantsBatchAsync_PostsToLinkVariantsRoute()
    {
        var requests = await CaptureAsync<ProductService>(
            c => new ProductService(c),
            s => s.LinkVariantsBatchAsync(42, new List<int> { 1, 2 }, CancellationToken.None),
            payload: "[]");

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/products/42/link-variants", typeof(ProductsController), nameof(ProductsController.LinkVariantsBatchAsync));
    }

    [Fact]
    public async Task Product_UnlinkVariantAsync_PostsToUnlinkVariantRoute()
    {
        var requests = await CaptureAsync<ProductService>(
            c => new ProductService(c),
            s => s.UnlinkVariantAsync(42, 7, CancellationToken.None));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/products/42/unlink-variant/7", typeof(ProductsController), nameof(ProductsController.UnlinkVariantAsync));
    }

    // ── ProductImportService ───────────────────────────────────────────────

    [Fact]
    public async Task ProductImport_CommitImportAsync_PostsToBulkImportRoute()
    {
        var requests = await CaptureAsync<ProductImportService>(
            c => new ProductImportService(c),
            s => s.CommitImportAsync(Array.Empty<ProductImportDto>(), overwriteMerge: true));

        AssertRequest(Assert.Single(requests), HttpMethod.Post, "/api/products/bulk-import", typeof(ProductsController), nameof(ProductsController.BulkImportAsync));
    }

    [Fact]
    public async Task ProductImport_ExportProductsToFileAsync_SendsOnlyBindableExportFilters()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"client-http-contract-{Guid.NewGuid():N}.csv");
        try
        {
            var requests = await CaptureAsync<ProductImportService>(
                c => new ProductImportService(c),
                s => s.ExportProductsToFileAsync(destination, activeOnly: false, filter: "arroz"));

            AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/products/export", typeof(ProductsController), nameof(ProductsController.ExportProductsAsync), "format", "activeOnly", "filter");
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    // ── VersionCheckService ────────────────────────────────────────────────

    [Fact]
    public async Task VersionCheck_CheckVersionAsync_GetsVersionCheckControllerRoute()
    {
        var requests = await CaptureAsync<VersionCheckService>(c => new VersionCheckService(c), s => s.CheckVersionAsync());

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/system/version-check", typeof(VersionCheckController), nameof(VersionCheckController.CheckVersion));
    }

    // ── PairingQrViewModel ─────────────────────────────────────────────────

    [Fact]
    public async Task Pairing_InitializeAsync_GetsPairingInfoRoute()
    {
        var requests = await CaptureAsync<PairingQrViewModel>(
            c => new PairingQrViewModel(c),
            vm => vm.InitializeAsync());

        AssertRequest(Assert.Single(requests), HttpMethod.Get, "/api/pairing/info", typeof(PairingController), nameof(PairingController.GetPairingInfo));
    }

    private static async Task AssertExchangeRateCallAsync(
        Func<ExchangeRateService, Task> invoke,
        HttpMethod method,
        string path,
        string actionName,
        params string[] expectedQueryKeys)
    {
        var handler = new CapturingHandler();
        using var client = CreateClient(handler);
        var service = new ExchangeRateService(client);
        try
        {
            // El constructor dispara un GET inicial a api/exchange-rate/today (InitializeAsync);
            // se espera a que aterrice y se limpia para capturar solo la llamada bajo prueba.
            await handler.WaitForCountAsync(1);
            handler.Clear();

            await invoke(service);

            AssertRequest(Assert.Single(handler.Requests), method, path, typeof(ExchangeRateController), actionName, expectedQueryKeys);
        }
        finally
        {
            await service.DisposeAsync();
        }
    }
}

/// <summary>
/// Guardas de forma de respuesta: un payload camelCase representativo (valores distintos a los
/// defaults de cada DTO) debe deserializarse en los campos esperados. Si el DTO del cliente deja de
/// coincidir con el contrato del backend, la deserializacion queda vacia o en default sin lanzar error.
/// </summary>
public class ClientHttpContractShapeTests
{
    [Fact]
    public async Task Sales_GetSaleAsync_DeserializesCamelCaseSalePayload()
    {
        const string payload = "{\"id\":77,\"invoiceNumber\":1001,\"status\":\"OnHold\",\"totalUsd\":12.34,\"appliedRate\":36.5,\"cashierName\":\"Carla\",\"items\":[{\"id\":3,\"productId\":5,\"productName\":\"Arroz\",\"quantity\":2,\"unitPrice\":6.17,\"subtotal\":12.34}],\"payments\":[]}";

        var sale = await RunAsync(c => new SalesService(c), s => s.GetSaleAsync(77), payload);

        Assert.Equal(77, sale.Id);
        Assert.Equal(1001, sale.InvoiceNumber);
        Assert.Equal("OnHold", sale.Status);
        Assert.Equal(12.34m, sale.TotalUSD);
        Assert.Equal(36.5m, sale.AppliedRate);
        Assert.Equal("Carla", sale.CashierName);
        var item = Assert.Single(sale.Items);
        Assert.Equal(5, item.ProductId);
        Assert.Equal("Arroz", item.ProductName);
        Assert.Equal(2m, item.Quantity);
        Assert.Equal(6.17m, item.UnitPrice);
    }

    [Fact]
    public async Task Sales_GetSalesHistoryAsync_DeserializesCamelCaseHistoryPayload()
    {
        const string payload = "{\"items\":[{\"id\":9,\"invoiceNumber\":1001,\"totalUsd\":5.5,\"totalBsS\":200.75,\"status\":\"Completed\",\"customerName\":\"Ana\"}],\"totalCount\":9}";

        var (items, totalCount) = await RunAsync<SalesService, (IEnumerable<SaleHistoryDto> Items, int TotalCount)>(
            c => new SalesService(c),
            s => s.GetSalesHistoryAsync(1, 20),
            payload);

        var item = Assert.Single(items);
        Assert.Equal(9, item.Id);
        Assert.Equal(1001, item.InvoiceNumber);
        Assert.Equal(5.5m, item.TotalUSD);
        Assert.Equal("Completed", item.Status);
        Assert.Equal(9, totalCount);
    }

    [Fact]
    public async Task Sales_GetCustomersAsync_DeserializesCamelCasePagedCustomerPayload()
    {
        const string payload = "{\"items\":[{\"id\":11,\"name\":\"Pedro\",\"cedulaOrRif\":\"V-11111111\",\"phone\":\"04120000000\"}],\"totalCount\":4,\"page\":1,\"pageSize\":20}";

        var (items, totalCount) = await RunAsync<SalesService, (IEnumerable<CustomerDto> Items, int TotalCount)>(
            c => new SalesService(c),
            s => s.GetCustomersAsync(page: 3, pageSize: 25),
            payload);

        var customer = Assert.Single(items);
        Assert.Equal(11, customer.Id);
        Assert.Equal("Pedro", customer.Name);
        Assert.Equal("V-11111111", customer.CedulaOrRif);
        Assert.Equal(4, totalCount);
    }

    [Fact]
    public async Task ExchangeRate_GetCurrentRateAsync_DeserializesValueAndUpdatedAt()
    {
        const string payload = "{\"value\":36.75,\"updatedAt\":\"2026-09-19T10:00:00Z\"}";

        var handler = new CapturingHandler(payload);
        using var client = CreateClient(handler);
        var service = new ExchangeRateService(client);
        try
        {
            await handler.WaitForCountAsync(1);

            var (rate, lastUpdated) = await service.GetCurrentRateAsync();

            Assert.Equal(36.75m, rate);
            Assert.Equal(new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc), lastUpdated);
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task User_LoginAsync_DeserializesCamelCaseLoginPayload()
    {
        const string payload = "{\"user\":{\"id\":5,\"cedula\":\"V-12345678\",\"name\":\"Luisa\",\"role\":3,\"isActive\":true},\"token\":\"tok-abc\",\"requiresPasswordChange\":false}";

        var login = (await RunAsync(c => new UserService(c), s => s.LoginAsync("V-1", "clave"), payload))!;

        Assert.Equal("tok-abc", login.Token);
        var user = login.User!;
        Assert.Equal(5, user.Id);
        Assert.Equal("Luisa", user.Name);
        Assert.Equal(UserRole.Admin, user.Role);
    }

    [Fact]
    public async Task User_GetUsersAsync_DeserializesCamelCaseUserList()
    {
        const string payload = "[{\"id\":7,\"cedula\":\"V-77777777\",\"name\":\"Marta\",\"isActive\":true}]";

        var users = await RunAsync(c => new UserService(c), s => s.GetUsersAsync(), payload);

        var user = Assert.Single(users);
        Assert.Equal(7, user.Id);
        Assert.Equal("Marta", user.Name);
        Assert.Equal("V-77777777", user.Cedula);
    }

    [Fact]
    public async Task CashDrawer_GetActiveSessionAsync_DeserializesCamelCaseSessionPayload()
    {
        const string payload = "{\"id\":33,\"openingBalanceLocal\":150.25,\"openingExchangeRate\":36.5,\"closingBalanceLocal\":null,\"transactions\":[{\"id\":1,\"amountLocal\":20.5,\"description\":\"Venta POS\",\"amountUsd\":0.56}]}";

        var session = await RunAsync(c => new CashDrawerService(c), s => s.GetActiveSessionAsync(), payload);

        Assert.NotNull(session);
        Assert.Equal(33, session!.Id);
        Assert.Equal(150.25m, session.OpeningBalanceLocal);
        Assert.Equal(36.5m, session.OpeningExchangeRate);
        var transaction = Assert.Single(session.Transactions);
        Assert.Equal(1, transaction.Id);
        Assert.Equal(20.5m, transaction.AmountLocal);
        Assert.Equal("Venta POS", transaction.Description);
    }

    [Fact]
    public async Task Settings_GetTimeZoneAsync_DeserializesCamelCaseTimeZonePayload()
    {
        const string payload = "{\"id\":\"America/Caracas\"}";

        var timeZone = await RunAsync(c => new SettingsService(c), s => s.GetTimeZoneAsync(), payload);

        Assert.Equal("America/Caracas", timeZone);
    }

    [Fact]
    public async Task Payment_GetActiveMethodsAsync_DeserializesCamelCaseMethodList()
    {
        const string payload = "[{\"id\":3,\"name\":\"Pago Movil\",\"isActive\":true,\"requiresReference\":true,\"isCash\":false,\"currency\":\"Bs.S\",\"displayOrder\":2}]";

        var methods = await RunAsync(c => new PaymentService(c), s => s.GetActiveMethodsAsync(), payload);

        var method = Assert.Single(methods);
        Assert.Equal(3, method.Id);
        Assert.Equal("Pago Movil", method.Name);
        Assert.True(method.RequiresReference);
        Assert.Equal("Bs.S", method.Currency);
        Assert.Equal(2, method.DisplayOrder);
    }

    [Fact]
    public async Task DailyClosure_GetExpectedTotalsAsync_DeserializesCamelCaseTotalsPayload()
    {
        const string payload = "[{\"paymentMethodId\":4,\"paymentMethodName\":\"Efectivo USD\",\"expectedAmountBsS\":321.5}]";

        var totals = await RunAsync(
            c => new DailyClosureClientService(c),
            s => s.GetExpectedTotalsAsync(new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc)),
            payload);

        var total = Assert.Single(totals);
        Assert.Equal(4, total.PaymentMethodId);
        Assert.Equal("Efectivo USD", total.PaymentMethodName);
        Assert.Equal(321.5m, total.ExpectedAmountBsS);
    }

    [Fact]
    public async Task ProductImport_CommitImportAsync_DeserializesAddedAndUpdated()
    {
        const string payload = "{\"added\":7,\"updated\":3}";

        var (added, updated) = await RunAsync<ProductImportService, (int added, int updated)>(
            c => new ProductImportService(c),
            s => s.CommitImportAsync(Array.Empty<ProductImportDto>(), overwriteMerge: true),
            payload);

        Assert.Equal(7, added);
        Assert.Equal(3, updated);
    }

    [Fact]
    public async Task VersionCheck_CheckVersionAsync_DeserializesCompatibilityFields()
    {
        const string payload = "{\"isClientCompatible\":false,\"minimumClientVersion\":\"2.5.0\",\"serverVersion\":\"2.6.1\",\"updateServerUrl\":\"https://updates.example.com/client\"}";

        var result = await RunAsync(c => new VersionCheckService(c), s => s.CheckVersionAsync(), payload);

        Assert.False(result.IsCompatible);
        Assert.Equal("2.5.0", result.MinimumClientVersion);
        Assert.Equal("2.6.1", result.ServerVersion);
        Assert.Equal("https://updates.example.com/client", result.UpdateServerUrl);
    }
}

/// <summary>
/// Utilidades compartidas de las guardas SEAM-01: handler que captura la peticion real, ejecucion del
/// servicio contra ese handler y asercion de ruta/metodo/query contra la accion del controlador.
/// </summary>
internal static class ClientHttpContractGuard
{
    public static HttpClient CreateClient(CapturingHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://localhost:5000/") };

    public static async Task<IReadOnlyList<CapturedRequest>> CaptureAsync<TService>(
        Func<HttpClient, TService> factory,
        Func<TService, Task> invoke,
        string payload = "{}")
    {
        var handler = new CapturingHandler(payload);
        using var client = CreateClient(handler);
        var service = factory(client);
        await invoke(service);
        return handler.Requests;
    }

    public static async Task<TResult> RunAsync<TService, TResult>(
        Func<HttpClient, TService> factory,
        Func<TService, Task<TResult>> invoke,
        string payload = "{}")
    {
        var handler = new CapturingHandler(payload);
        using var client = CreateClient(handler);
        var service = factory(client);
        return await invoke(service);
    }

    public static void AssertRequest(
        CapturedRequest request,
        HttpMethod expectedMethod,
        string expectedPath,
        Type controller,
        string actionName,
        params string[] expectedQueryKeys)
    {
        Assert.Equal(expectedMethod.Method, request.Method.Method);
        Assert.Equal(expectedPath.ToLowerInvariant(), request.Uri.AbsolutePath.ToLowerInvariant());

        AssertRouteTemplate(request, controller, actionName);

        var sentKeys = ParseQueryKeys(request.Uri);
        var bindable = BindableNames(controller, actionName);

        Assert.All(sentKeys, key => Assert.True(
            bindable.Contains(key),
            $"El cliente envia '{key}', que {controller.Name}.{actionName} NO bindea (validos: {string.Join(", ", bindable.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))})."));

        var expected = expectedQueryKeys.Select(k => k.ToLowerInvariant()).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var actual = sentKeys.Select(k => k.ToLowerInvariant()).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Contrasta mecanicamente metodo y ruta contra los atributos reales de la accion: el metodo debe
    /// estar declarado (HttpGet/HttpPost/HttpPut/HttpDelete/HttpPatch) y la ruta debe casar con la
    /// plantilla derivada del [Route] de clase (resolviendo el token [controller] al nombre del
    /// controlador sin el sufijo "Controller") mas la plantilla del atributo de accion. Cada
    /// '{placeholder}' casa un segmento completo, de modo que las constraints ({id:int}) no se escriben
    /// a mano. Si una ruta del backend deriva (p. ej. "start" -> "begin") sin actualizar al cliente, esta
    /// guarda se pone roja aunque el literal esperado del test siga intacto.
    /// </summary>
    private static void AssertRouteTemplate(CapturedRequest request, Type controller, string actionName)
    {
        var action = FindActionMethod(controller, actionName);
        var methodAttributes = action.GetCustomAttributes<HttpMethodAttribute>(inherit: true).ToList();

        Assert.True(
            methodAttributes.Count > 0,
            $"{controller.Name}.{actionName} no declara atributo HTTP (HttpGet/HttpPost/HttpPut/HttpDelete/HttpPatch): no hay plantilla de metodo/ruta que verificar.");

        var declaredMethods = methodAttributes
            .SelectMany(a => a.HttpMethods)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToArray();

        var matchingAttributes = methodAttributes
            .Where(a => a.HttpMethods.Contains(request.Method.Method, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            matchingAttributes.Count > 0,
            $"El cliente envio {request.Method.Method} '{request.Uri.AbsolutePath}', pero {controller.Name}.{actionName} solo declara: {string.Join(", ", declaredMethods)}.");

        var classRoute = controller.GetCustomAttributes<RouteAttribute>(inherit: true).FirstOrDefault()?.Template ?? string.Empty;
        var expectedTemplates = matchingAttributes
            .Select(a => CombineRouteTemplate(controller, classRoute, a.Template))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Una accion puede declarar varias rutas (p. ej. HealthController.CheckHealthAsync sirve
        // "health" y "api/health"): basta con que la ruta enviada case con una de ellas.
        var matchedTemplate = expectedTemplates.FirstOrDefault(template =>
            Regex.IsMatch(request.Uri.AbsolutePath, RouteTemplateToRegex(template), RegexOptions.IgnoreCase));

        Assert.True(
            matchedTemplate != null,
            $"El cliente envio {request.Method.Method} '{request.Uri.AbsolutePath}', pero {controller.Name}.{actionName} espera " +
            $"la plantilla '{string.Join("' o '", expectedTemplates)}' (patron: {string.Join(" | ", expectedTemplates.Select(RouteTemplateToRegex))}).");
    }

    private static string CombineRouteTemplate(Type controller, string classRouteTemplate, string? actionTemplate)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(classRouteTemplate))
        {
            parts.Add(ResolveRouteTokens(controller, classRouteTemplate.Trim('/')));
        }

        if (!string.IsNullOrWhiteSpace(actionTemplate))
        {
            parts.Add(ResolveRouteTokens(controller, actionTemplate!.Trim('/')));
        }

        return string.Join('/', parts);
    }

    private static string ResolveRouteTokens(Type controller, string template)
    {
        var controllerName = controller.Name;
        if (controllerName.EndsWith("Controller", StringComparison.Ordinal))
        {
            controllerName = controllerName[..^"Controller".Length];
        }

        return template.Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase);
    }

    private static string RouteTemplateToRegex(string routeTemplate)
    {
        var pattern = new StringBuilder("^");

        foreach (var segment in routeTemplate.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            pattern.Append('/');

            if (segment.Length > 2 && segment.StartsWith('{') && segment.EndsWith('}'))
            {
                // Placeholder con o sin constraint ({id}, {saleId:int}): casa un unico segmento,
                // igual que el routing de ASP.NET.
                pattern.Append("[^/]+");
            }
            else
            {
                pattern.Append(Regex.Escape(segment));
            }
        }

        pattern.Append("/?$");
        return pattern.ToString();
    }

    private static HashSet<string> BindableNames(Type controller, string actionName)
    {
        var action = FindActionMethod(controller, actionName);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in action.GetParameters())
        {
            if (parameter.GetCustomAttribute<FromQueryAttribute>() is { } fromQuery)
            {
                names.Add(fromQuery.Name ?? parameter.Name!);
                continue;
            }

            if (parameter.GetCustomAttribute<FromRouteAttribute>() is { } fromRoute)
            {
                names.Add(fromRoute.Name ?? parameter.Name!);
                continue;
            }

            if (parameter.GetCustomAttribute<FromBodyAttribute>() != null ||
                parameter.GetCustomAttribute<FromHeaderAttribute>() != null ||
                parameter.GetCustomAttribute<FromServicesAttribute>() != null ||
                parameter.GetCustomAttribute<FromFormAttribute>() != null)
            {
                continue;
            }

            if (IsSimple(parameter.ParameterType))
            {
                names.Add(parameter.Name!);
            }
        }

        foreach (var httpMethod in action.GetCustomAttributes<HttpMethodAttribute>())
        {
            if (string.IsNullOrWhiteSpace(httpMethod.Template))
            {
                continue;
            }

            foreach (var segment in httpMethod.Template.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment.Length > 2 && segment.StartsWith('{') && segment.EndsWith('}'))
                {
                    names.Add(segment.Trim('{', '}').Split(':')[0]);
                }
            }
        }

        return names;
    }

    private static MethodInfo FindActionMethod(Type controller, string actionName)
    {
        var matches = controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == actionName && m.GetCustomAttribute<NonActionAttribute>(inherit: true) == null)
            .ToList();

        Assert.True(matches.Count == 1, $"No se encontro una accion unica {controller.Name}.{actionName} (candidatas: {matches.Count}).");
        return matches[0];
    }

    private static List<string> ParseQueryKeys(Uri uri)
    {
        return uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=')[0])
            .ToList();
    }

    private static bool IsSimple(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive
            || underlying.IsEnum
            || underlying == typeof(string)
            || underlying == typeof(decimal)
            || underlying == typeof(DateTime)
            || underlying == typeof(DateOnly)
            || underlying == typeof(TimeOnly)
            || underlying == typeof(Guid);
    }

    public sealed record CapturedRequest(HttpMethod Method, Uri Uri);

    public sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly object _sync = new();
        private readonly List<CapturedRequest> _requests = new();
        private readonly string _payload;

        public CapturingHandler(string payload = "{}") => _payload = payload;

        public IReadOnlyList<CapturedRequest> Requests
        {
            get
            {
                lock (_sync) return _requests.ToList();
            }
        }

        public void Clear()
        {
            lock (_sync) _requests.Clear();
        }

        public async Task WaitForCountAsync(int count, int timeoutMs = 5000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;

            while (true)
            {
                lock (_sync)
                {
                    if (_requests.Count >= count)
                    {
                        return;
                    }
                }

                if (Environment.TickCount64 > deadline)
                {
                    throw new TimeoutException($"Se esperaban al menos {count} peticiones y se capturaron menos dentro de {timeoutMs} ms.");
                }

                await Task.Delay(10, CancellationToken.None);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _requests.Add(new CapturedRequest(request.Method, request.RequestUri!));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_payload, Encoding.UTF8, "application/json")
            });
        }
    }
}
