using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Hubs;
using Backend.API.Services;
using Core.Interfaces;
using Core.Services;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Sales.Module.Data;
using Sales.Module.Interfaces;
using Sales.Module.Services;

namespace CommandCenter.Tests.Builders;

public static class ControllerFactory
{
    public static CashDrawerController CreateCashDrawerController(
        ICashDrawerService cashDrawerService,
        ISystemSettingsService settingsService,
        SalesDbContext db,
        ICurrentUserService currentUserService,
        InventoryDbContext inventoryContext,
        CashAdvanceCoordinator cashAdvanceCoordinator,
        IUserService? userService = null,
        IMonetaryProfileService? monetaryProfileService = null)
        => new(
            cashDrawerService,
            settingsService,
            new InventoryService(inventoryContext),
            userService ?? new UserService(db),
            currentUserService,
            new TimeZoneProvider(settingsService),
            cashAdvanceCoordinator,
            monetaryProfileService: monetaryProfileService);

    public static ExchangeRateController CreateExchangeRateController(
        InventoryDbContext context,
        ICurrentUserService currentUserService,
        IMemoryCache? cache = null)
        => new(
            new ExchangeRateHistoryService(context),
            null!,
            new TimeZoneProvider(new SystemSettingsService(context)),
            currentUserService,
            cache);

    public static SettingsController CreateSettingsController(
        InventoryDbContext context,
        ICurrentUserService currentUserService,
        ISystemSettingsService settingsService,
        IHubContext<ExchangeRateHub>? hubContext = null,
        IMonetaryTransitionGuard? transitionGuard = null,
        IMonetaryPaymentMethodSeeder? paymentMethodSeeder = null)
        => new(
            settingsService,
            currentUserService,
            new ExchangeRateHistoryService(context),
            new ExchangeRateWriteService(context, new InventoryService(context), hubContext: hubContext),
            new TimeZoneProvider(settingsService),
            hubContext,
            monetaryProfileService: new MonetaryProfileService(settingsService),
            currencyRateService: new CurrencyRateService(context),
            monetaryTransitionGuard: transitionGuard ?? EmptyInstallTransitionGuard.Instance,
            monetaryPaymentMethodSeeder: paymentMethodSeeder);

    /// <summary>
    /// 8.211-T3c3a: existing settings tests run against empty in-memory installs, where the
    /// production transition guard reports no financial data. The wiring-failure (null guard)
    /// branch is covered by a dedicated test that builds the controller directly.
    /// </summary>
    private sealed class EmptyInstallTransitionGuard : IMonetaryTransitionGuard
    {
        public static readonly EmptyInstallTransitionGuard Instance = new();

        private EmptyInstallTransitionGuard()
        {
        }

        public Task<bool> HasFinancialDataAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    public static UsersController CreateUsersController(
        SalesDbContext db,
        IPasswordPolicyService? passwordPolicyService = null,
        ISecurityStampValidator? stampValidator = null)
        => new(
            new UserService(db, passwordPolicyService),
            passwordPolicyService,
            stampValidator);
}
