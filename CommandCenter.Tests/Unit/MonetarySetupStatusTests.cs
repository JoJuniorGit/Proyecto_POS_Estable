using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Services;
using CommandCenter.Tests.Builders;
using Core.Constants;
using Core.DTOs;
using Core.Interfaces;
using Core.Services;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.211-T3c3a (S11): fresh-install onboarding status and the transition guard that blocks base
// changes once financial data exists. Symbol-only edits stay allowed.
public class MonetarySetupStatusTests
{
    private const string BaseLockMessage = "No se puede cambiar el modo ni la moneda base porque ya existen datos financieros. La moneda base solo puede cambiarse en una instalación nueva o mediante una migración explícita.";
    private const string VerificationMessage = "No se pudo verificar si la instalación tiene datos financieros; el cambio de moneda base está bloqueado.";

    private static InventoryDbContext CreateInMemoryDb()
        => new(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    private static SettingsController CreateController(
        InventoryDbContext db,
        bool canMutateSettings,
        bool hasFinancialData,
        IMonetaryPaymentMethodSeeder? paymentMethodSeeder = null)
    {
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateSettings).Returns(canMutateSettings);

        var guard = new Mock<IMonetaryTransitionGuard>();
        guard.Setup(g => g.HasFinancialDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(hasFinancialData);

        return ControllerFactory.CreateSettingsController(
            db,
            mockUser.Object,
            new SystemSettingsService(db),
            transitionGuard: guard.Object,
            paymentMethodSeeder: paymentMethodSeeder);
    }

    // The production guard is always registered by DI; this helper covers the wiring-failure branch.
    private static SettingsController CreateControllerWithoutGuard(
        InventoryDbContext db,
        bool canMutateSettings,
        IMonetaryPaymentMethodSeeder? paymentMethodSeeder = null)
    {
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateSettings).Returns(canMutateSettings);

        var settingsService = new SystemSettingsService(db);
        return new SettingsController(
            settingsService,
            mockUser.Object,
            new ExchangeRateHistoryService(db),
            new ExchangeRateWriteService(db, new InventoryService(db)),
            new TimeZoneProvider(settingsService),
            null,
            null,
            new MonetaryProfileService(settingsService),
            new CurrencyRateService(db),
            null,
            paymentMethodSeeder);
    }

    private static MonetaryProfileDto DefaultDualProfile() => new()
    {
        Mode = MonetaryMode.Dual,
        PrimaryCurrencyCode = "USD",
        PrimaryCurrencySymbol = "$",
        SecondaryCurrencyCode = "VES",
        SecondaryCurrencySymbol = "Bs.S"
    };

    private static MonetaryProfileDto SingleCopProfile() => new()
    {
        Mode = MonetaryMode.Single,
        PrimaryCurrencyCode = "COP",
        PrimaryCurrencySymbol = "$",
        SecondaryCurrencyCode = null,
        SecondaryCurrencySymbol = null
    };

    private static string GetProblemMessage(ActionResult result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        return Assert.IsType<string>(problem.Extensions["message"]);
    }

    // ---------------------------------------------------------------------------------------------
    // GET /api/settings/monetary-setup-status.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetMonetarySetupStatus_OnFreshInstall_ReturnsDefaultsAndAllowsConfiguration()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: true, hasFinancialData: false);

        var result = await controller.GetMonetarySetupStatusAsync();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<MonetarySetupStatusDto>(ok.Value);
        Assert.False(dto.IsConfigured);
        Assert.True(dto.CanConfigureMonetaryProfile);
        Assert.Equal(MonetaryMode.Dual, dto.Profile.Mode);
        Assert.Equal("USD", dto.Profile.PrimaryCurrencyCode);
        Assert.Equal("$", dto.Profile.PrimaryCurrencySymbol);
        Assert.Equal("VES", dto.Profile.SecondaryCurrencyCode);
        Assert.Equal("Bs.S", dto.Profile.SecondaryCurrencySymbol);
    }

    [Fact]
    public async Task GetMonetarySetupStatus_WhenProfileConfigured_ReportsConfigured()
    {
        using var db = CreateInMemoryDb();
        var settingsService = new SystemSettingsService(db);
        await new MonetaryProfileService(settingsService).SaveAsync(DefaultDualProfile());
        var controller = CreateController(db, canMutateSettings: true, hasFinancialData: false);

        var result = await controller.GetMonetarySetupStatusAsync();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<MonetarySetupStatusDto>(ok.Value);
        Assert.True(dto.IsConfigured);
        Assert.True(dto.CanConfigureMonetaryProfile);
    }

    [Fact]
    public async Task GetMonetarySetupStatus_WhenFinancialDataExists_DisallowsConfiguration()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: true, hasFinancialData: true);

        var result = await controller.GetMonetarySetupStatusAsync();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<MonetarySetupStatusDto>(ok.Value);
        Assert.False(dto.CanConfigureMonetaryProfile);
    }

    [Fact]
    public async Task GetMonetarySetupStatus_DoesNotRequireCanMutateSettings()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: false, hasFinancialData: false);

        var result = await controller.GetMonetarySetupStatusAsync();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetMonetarySetupStatus_WithoutGuard_ReportsConfigurationAsUnavailable()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateControllerWithoutGuard(db, canMutateSettings: true);

        var result = await controller.GetMonetarySetupStatusAsync();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<MonetarySetupStatusDto>(ok.Value);
        Assert.False(dto.CanConfigureMonetaryProfile);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT with a usable guard.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetMonetaryProfile_SingleCopOnEmptyInstall_PersistsAndReportsConfigured()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: true, hasFinancialData: false);

        var result = await controller.SetMonetaryProfileAsync(SingleCopProfile());

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<MonetaryProfileDto>(ok.Value);
        Assert.True(dto.IsConfigured);
        Assert.Equal(MonetaryMode.Single, dto.Mode);
        Assert.Equal("COP", dto.PrimaryCurrencyCode);

        Assert.Equal(3, await db.SystemSettings.CountAsync());
        var mode = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.Monetary.Mode)
            .Select(s => s.Value)
            .SingleAsync();
        Assert.Equal("Single", mode);
        var primaryCode = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.Monetary.PrimaryCurrencyCode)
            .Select(s => s.Value)
            .SingleAsync();
        Assert.Equal("COP", primaryCode);
    }

    [Fact]
    public async Task SetMonetaryProfile_FreshSingleCop_CallsSeederWithSavedProfile()
    {
        using var db = CreateInMemoryDb();
        var seeder = new Mock<IMonetaryPaymentMethodSeeder>();
        MonetaryProfileDto? seededProfile = null;
        seeder.Setup(s => s.EnsureForProfileAsync(It.IsAny<MonetaryProfileDto>(), It.IsAny<CancellationToken>()))
            .Callback<MonetaryProfileDto, CancellationToken>((profile, _) => seededProfile = profile)
            .Returns(Task.CompletedTask);

        var controller = CreateController(db, canMutateSettings: true, hasFinancialData: false, seeder.Object);

        var result = await controller.SetMonetaryProfileAsync(SingleCopProfile());

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(seededProfile);
        Assert.True(seededProfile!.IsConfigured);
        Assert.Equal(MonetaryMode.Single, seededProfile.Mode);
        Assert.Equal("COP", seededProfile.PrimaryCurrencyCode);
        seeder.Verify(
            s => s.EnsureForProfileAsync(It.IsAny<MonetaryProfileDto>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SetMonetaryProfile_SymbolOnlyChangeWithFinancialData_DoesNotCallSeeder()
    {
        using var db = CreateInMemoryDb();
        var settingsService = new SystemSettingsService(db);
        await new MonetaryProfileService(settingsService).SaveAsync(DefaultDualProfile());

        var seeder = new Mock<IMonetaryPaymentMethodSeeder>();
        var controller = CreateController(db, canMutateSettings: true, hasFinancialData: true, seeder.Object);
        var profile = DefaultDualProfile();
        profile.PrimaryCurrencySymbol = "US$";

        var result = await controller.SetMonetaryProfileAsync(profile);

        Assert.IsType<OkObjectResult>(result);
        seeder.Verify(
            s => s.EnsureForProfileAsync(It.IsAny<MonetaryProfileDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SetMonetaryProfile_SymbolOnlyChangeOnFreshInstall_CallsSeeder()
    {
        using var db = CreateInMemoryDb();
        var seeder = new Mock<IMonetaryPaymentMethodSeeder>();
        var controller = CreateController(db, canMutateSettings: true, hasFinancialData: false, seeder.Object);
        var profile = DefaultDualProfile();
        profile.PrimaryCurrencySymbol = "US$";

        var result = await controller.SetMonetaryProfileAsync(profile);

        Assert.IsType<OkObjectResult>(result);
        seeder.Verify(
            s => s.EnsureForProfileAsync(
                It.Is<MonetaryProfileDto>(p => p.Mode == MonetaryMode.Dual && p.PrimaryCurrencySymbol == "US$"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SetMonetaryProfile_BaseChangeWithFinancialData_ReturnsBaseLockMessageAndPersistsNothing()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: true, hasFinancialData: true);

        var result = await controller.SetMonetaryProfileAsync(SingleCopProfile());

        Assert.Equal(BaseLockMessage, GetProblemMessage(result));
        Assert.Equal(0, await db.SystemSettings.CountAsync());
    }

    [Fact]
    public async Task SetMonetaryProfile_PrimaryCodeOnlyChangeWithFinancialData_ReturnsBaseLock()
    {
        using var db = CreateInMemoryDb();
        var settingsService = new SystemSettingsService(db);
        await new MonetaryProfileService(settingsService).SaveAsync(DefaultDualProfile());

        var controller = CreateController(db, canMutateSettings: true, hasFinancialData: true);
        var requested = DefaultDualProfile();
        requested.PrimaryCurrencyCode = "EUR";

        var result = await controller.SetMonetaryProfileAsync(requested);

        Assert.Equal(BaseLockMessage, GetProblemMessage(result));
        var stored = await new MonetaryProfileService(settingsService).GetAsync();
        Assert.Equal("USD", stored.PrimaryCurrencyCode);
    }

    [Fact]
    public async Task SetMonetaryProfile_SecondaryCodeOnlyChangeWithFinancialData_ReturnsBaseLock()
    {
        using var db = CreateInMemoryDb();
        var settingsService = new SystemSettingsService(db);
        await new MonetaryProfileService(settingsService).SaveAsync(DefaultDualProfile());

        var controller = CreateController(db, canMutateSettings: true, hasFinancialData: true);
        var requested = DefaultDualProfile();
        requested.SecondaryCurrencyCode = "COP";
        requested.SecondaryCurrencySymbol = "$";

        var result = await controller.SetMonetaryProfileAsync(requested);

        Assert.Equal(BaseLockMessage, GetProblemMessage(result));
        var stored = await new MonetaryProfileService(settingsService).GetAsync();
        Assert.Equal("VES", stored.SecondaryCurrencyCode);
    }

    [Fact]
    public async Task SetMonetaryProfile_LowercaseVesWithFinancialData_IsTreatedAsSamePair()
    {
        using var db = CreateInMemoryDb();
        var settingsService = new SystemSettingsService(db);
        await new MonetaryProfileService(settingsService).SaveAsync(DefaultDualProfile());

        var controller = CreateController(db, canMutateSettings: true, hasFinancialData: true);
        var requested = DefaultDualProfile();
        requested.SecondaryCurrencyCode = "ves";
        requested.SecondaryCurrencySymbol = "Bs.";

        var result = await controller.SetMonetaryProfileAsync(requested);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<MonetaryProfileDto>(ok.Value);
        Assert.Equal("VES", dto.SecondaryCurrencyCode);
        Assert.Equal("Bs.", dto.SecondaryCurrencySymbol);
    }

    [Fact]
    public async Task SetMonetaryProfile_SymbolOnlyChangeWithFinancialData_Persists()
    {
        using var db = CreateInMemoryDb();
        var settingsService = new SystemSettingsService(db);
        await new MonetaryProfileService(settingsService).SaveAsync(DefaultDualProfile());

        var controller = CreateController(db, canMutateSettings: true, hasFinancialData: true);
        var profile = DefaultDualProfile();
        profile.PrimaryCurrencySymbol = "US$";
        profile.SecondaryCurrencySymbol = "Bs.";

        var result = await controller.SetMonetaryProfileAsync(profile);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<MonetaryProfileDto>(ok.Value);
        Assert.Equal("US$", dto.PrimaryCurrencySymbol);
        Assert.Equal("Bs.", dto.SecondaryCurrencySymbol);

        var storedSymbols = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.Monetary.PrimaryCurrencySymbol
                || s.Key == SettingKeys.Monetary.SecondaryCurrencySymbol)
            .ToDictionaryAsync(s => s.Key, s => s.Value);
        Assert.Equal("US$", storedSymbols[SettingKeys.Monetary.PrimaryCurrencySymbol]);
        Assert.Equal("Bs.", storedSymbols[SettingKeys.Monetary.SecondaryCurrencySymbol]);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT without a usable guard / without permission.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetMonetaryProfile_WithNullGuardAndBaseChange_ReturnsVerificationMessageAndPersistsNothing()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateControllerWithoutGuard(db, canMutateSettings: true);

        var result = await controller.SetMonetaryProfileAsync(SingleCopProfile());

        Assert.Equal(VerificationMessage, GetProblemMessage(result));
        Assert.Equal(0, await db.SystemSettings.CountAsync());
    }

    [Fact]
    public async Task SetMonetaryProfile_SymbolOnlyChangeWithoutGuard_DoesNotCallSeeder()
    {
        using var db = CreateInMemoryDb();
        var seeder = new Mock<IMonetaryPaymentMethodSeeder>();
        var controller = CreateControllerWithoutGuard(db, canMutateSettings: true, seeder.Object);
        var profile = DefaultDualProfile();
        profile.PrimaryCurrencySymbol = "US$";

        var result = await controller.SetMonetaryProfileAsync(profile);

        Assert.IsType<OkObjectResult>(result);
        seeder.Verify(
            s => s.EnsureForProfileAsync(It.IsAny<MonetaryProfileDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SetMonetaryProfile_WithoutCanMutateSettings_ReturnsForbiddenAndPersistsNothing()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: false, hasFinancialData: false);

        var result = await controller.SetMonetaryProfileAsync(SingleCopProfile());

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal(0, await db.SystemSettings.CountAsync());
    }
}


