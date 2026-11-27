using System;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.Constants;
using Core.DTOs;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.211-T1 (S3): endpoints GET/PUT del perfil monetario, RBAC por CanMutateSettings y traducción
// de ArgumentException de dominio a 400 sin persistir.
public class MonetaryProfileSettingsTests
{
    private const string UndefinedModeMessage = "El modo monetario debe ser Single o Dual.";
    private const string SameCurrencyMessage = "La moneda secundaria no puede ser igual a la principal.";
    private const string SingleWithSecondaryMessage = "En modo Single no se debe configurar una moneda secundaria.";
    private const string ControlCharacterMessage = "El símbolo de la moneda no puede contener caracteres de control.";

    private static InventoryDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    private static SettingsController CreateController(InventoryDbContext db, bool canMutateSettings)
    {
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateSettings).Returns(canMutateSettings);
        return ControllerFactory.CreateSettingsController(db, mockUser.Object, new SystemSettingsService(db));
    }

    private static MonetaryProfileDto ValidDualProfile() => new()
    {
        Mode = MonetaryMode.Dual,
        PrimaryCurrencyCode = "USD",
        PrimaryCurrencySymbol = "$",
        SecondaryCurrencyCode = "VES",
        SecondaryCurrencySymbol = "Bs.S"
    };

    private static MonetaryProfileDto ValidSingleProfile() => new()
    {
        Mode = MonetaryMode.Single,
        PrimaryCurrencyCode = "USD",
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

    [Fact]
    public async Task SetMonetaryProfile_AcquiresTransitionLockBeforeReadingCurrentProfile()
    {
        var order = new List<string>();
        var guard = new Mock<IMonetaryTransitionGuard>();
        guard.Setup(g => g.AcquireTransitionLockAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                order.Add("lock");
                return Task.FromResult<IAsyncDisposable?>(new RecordingDisposable(order));
            });
        guard.Setup(g => g.HasFinancialDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var profileService = new Mock<IMonetaryProfileService>();
        profileService.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                order.Add("read");
                return ValidDualProfile();
            });
        profileService.Setup(s => s.SaveAsync(It.IsAny<MonetaryProfileDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MonetaryProfileDto requested, CancellationToken _) => requested);

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(u => u.CanMutateSettings).Returns(true);

        var controller = new SettingsController(
            Mock.Of<ISystemSettingsService>(),
            currentUser.Object,
            Mock.Of<IExchangeRateHistoryService>(),
            monetaryProfileService: profileService.Object,
            monetaryTransitionGuard: guard.Object);

        var result = await controller.SetMonetaryProfileAsync(ValidDualProfile());

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(new[] { "lock", "read" }, order.Take(2));
    }

    private sealed class RecordingDisposable(List<string> order) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            order.Add("release");
            return ValueTask.CompletedTask;
        }
    }
    // ---------------------------------------------------------------------------------------------
    // GET.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetMonetaryProfile_WhenUnconfigured_ReturnsDefaults()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: true);

        var result = await controller.GetMonetaryProfileAsync();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<MonetaryProfileDto>(ok.Value);
        Assert.Equal(MonetaryMode.Dual, dto.Mode);
        Assert.Equal("USD", dto.PrimaryCurrencyCode);
        Assert.Equal("$", dto.PrimaryCurrencySymbol);
        Assert.Equal("VES", dto.SecondaryCurrencyCode);
        Assert.Equal("Bs.S", dto.SecondaryCurrencySymbol);
        Assert.False(dto.IsConfigured);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT válido + round-trip.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetMonetaryProfile_ValidPayload_ReturnsOkPersistsAndReportsConfigured()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: true);

        var result = await controller.SetMonetaryProfileAsync(ValidDualProfile());

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<MonetaryProfileDto>(ok.Value);
        Assert.True(dto.IsConfigured);
        Assert.Equal(MonetaryMode.Dual, dto.Mode);
        Assert.Equal("USD", dto.PrimaryCurrencyCode);
        Assert.Equal("VES", dto.SecondaryCurrencyCode);

        Assert.Equal(5, await db.SystemSettings.CountAsync());
        var mode = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.Monetary.Mode)
            .Select(s => s.Value)
            .SingleAsync();
        Assert.Equal("Dual", mode);
    }

    [Fact]
    public async Task SetMonetaryProfile_ThenGet_RoundTripsConfiguredProfile()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: true);

        await controller.SetMonetaryProfileAsync(new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "eur ",
            PrimaryCurrencySymbol = " € ",
            SecondaryCurrencyCode = "gbp",
            SecondaryCurrencySymbol = " £ "
        });

        var result = await controller.GetMonetaryProfileAsync();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<MonetaryProfileDto>(ok.Value);
        Assert.True(dto.IsConfigured);
        Assert.Equal(MonetaryMode.Dual, dto.Mode);
        Assert.Equal("EUR", dto.PrimaryCurrencyCode);
        Assert.Equal("€", dto.PrimaryCurrencySymbol);
        Assert.Equal("GBP", dto.SecondaryCurrencyCode);
        Assert.Equal("£", dto.SecondaryCurrencySymbol);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT inválido -> 400 sin persistir.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetMonetaryProfile_UndefinedMode_ReturnsBadRequestAndPersistsNothing()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: true);
        var profile = ValidDualProfile();
        profile.Mode = (MonetaryMode)99;

        var result = await controller.SetMonetaryProfileAsync(profile);

        Assert.Equal(UndefinedModeMessage, GetProblemMessage(result));
        Assert.Equal(0, await db.SystemSettings.CountAsync());
    }

    [Fact]
    public async Task SetMonetaryProfile_SamePrimaryAndSecondary_ReturnsBadRequest()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: true);
        var profile = ValidDualProfile();
        profile.SecondaryCurrencyCode = "usd";

        var result = await controller.SetMonetaryProfileAsync(profile);

        Assert.Equal(SameCurrencyMessage, GetProblemMessage(result));
        Assert.Equal(0, await db.SystemSettings.CountAsync());
    }

    [Fact]
    public async Task SetMonetaryProfile_SingleWithSecondary_ReturnsBadRequest()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: true);
        var profile = ValidSingleProfile();
        profile.SecondaryCurrencyCode = "VES";

        var result = await controller.SetMonetaryProfileAsync(profile);

        Assert.Equal(SingleWithSecondaryMessage, GetProblemMessage(result));
        Assert.Equal(0, await db.SystemSettings.CountAsync());
    }

    [Fact]
    public async Task SetMonetaryProfile_ControlCharacterSymbol_ReturnsBadRequest()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: true);
        var profile = ValidDualProfile();
        profile.PrimaryCurrencySymbol = "$\u0001";

        var result = await controller.SetMonetaryProfileAsync(profile);

        Assert.Equal(ControlCharacterMessage, GetProblemMessage(result));
        Assert.Equal(0, await db.SystemSettings.CountAsync());
    }

    // ---------------------------------------------------------------------------------------------
    // RBAC.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetMonetaryProfile_WithoutCanMutateSettings_ReturnsForbiddenAndPersistsNothing()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: false);

        var result = await controller.SetMonetaryProfileAsync(ValidDualProfile());

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal(0, await db.SystemSettings.CountAsync());
    }

    [Fact]
    public async Task GetMonetaryProfile_DoesNotRequireCanMutateSettings()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db, canMutateSettings: false);

        var result = await controller.GetMonetaryProfileAsync();

        Assert.IsType<OkObjectResult>(result);
    }
}

