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
        CashAdvanceCoordinator cashAdvanceCoordinator)
        => new(
            cashDrawerService,
            settingsService,
            new InventoryService(inventoryContext),
            new UserService(db),
            currentUserService,
            new TimeZoneProvider(settingsService),
            cashAdvanceCoordinator);

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
        IHubContext<ExchangeRateHub>? hubContext = null)
        => new(
            settingsService,
            currentUserService,
            new ExchangeRateHistoryService(context),
            new ExchangeRateWriteService(context, new InventoryService(context), hubContext: hubContext),
            new TimeZoneProvider(settingsService),
            hubContext);

    public static UsersController CreateUsersController(
        SalesDbContext db,
        IPasswordPolicyService? passwordPolicyService = null,
        ISecurityStampValidator? stampValidator = null)
        => new(
            new UserService(db, passwordPolicyService),
            passwordPolicyService,
            stampValidator);
}
