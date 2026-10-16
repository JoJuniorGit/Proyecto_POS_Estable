using System;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.Constants;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.184-T1 (S1): políticas globales de precios y redondeo en SettingsController.
// Cubre: GET con defaults (ExactMath / ExactMath / true) cuando las claves faltan o son ilegibles;
// GET con valores válidos almacenados (enum por NOMBRE + bool); PUT válido persistido por NOMBRE y
// con DTO de vuelta; PUT con enum indefinido -> 400 sin persistir; RBAC -> 403 sin CanMutateSettings.
public class SettingsPricingPoliciesTests
{
    private static InventoryDbContext CreateInMemoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new InventoryDbContext(options);
    }

    private static SettingsController CreateController(InventoryDbContext db, bool canMutateSettings)
    {
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateSettings).Returns(canMutateSettings);
        return ControllerFactory.CreateSettingsController(db, mockUser.Object, new SystemSettingsService(db));
    }

    private static async Task SeedSettingAsync(InventoryDbContext db, string key, string value)
    {
        db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, LastUpdated = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------------------------------------
    // GET.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetPricingPolicies_WhenNotConfigured_ReturnsExactMathDefaultsWithFloorEnabled()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var controller = CreateController(db, canMutateSettings: true);

        var result = await controller.GetPricingPoliciesAsync();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<PricingPoliciesDto>(ok.Value);
        Assert.Equal(RoundingRuleType.ExactMath, dto.UsdRoundingRule);
        Assert.Equal(RoundingRuleType.ExactMath, dto.CashBsRoundingRule);
        Assert.True(dto.EnforceMinimumMarginFloor);
    }

    [Fact]
    public async Task GetPricingPolicies_WhenConfigured_ReturnsStoredValues()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        await SeedSettingAsync(db, SettingKeys.Pricing.DefaultUsdRoundingRule, "UsdPsychological");
        await SeedSettingAsync(db, SettingKeys.Pricing.DefaultCashBsRoundingRule, "VesCashFriendlyStep5");
        await SeedSettingAsync(db, SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor, "False");
        var controller = CreateController(db, canMutateSettings: true);

        var result = await controller.GetPricingPoliciesAsync();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<PricingPoliciesDto>(ok.Value);
        Assert.Equal(RoundingRuleType.UsdPsychological, dto.UsdRoundingRule);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep5, dto.CashBsRoundingRule);
        Assert.False(dto.EnforceMinimumMarginFloor);
    }

    [Theory]
    [InlineData("Bogus")] // TryParse falla
    [InlineData("99")]    // TryParse OK pero Enum.IsDefined falla
    [InlineData("")]
    public async Task GetPricingPolicies_WithIllegibleStoredValues_FallsBackToDefaults(string stored)
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        await SeedSettingAsync(db, SettingKeys.Pricing.DefaultUsdRoundingRule, stored);
        await SeedSettingAsync(db, SettingKeys.Pricing.DefaultCashBsRoundingRule, stored);
        await SeedSettingAsync(db, SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor, "not-a-bool");
        var controller = CreateController(db, canMutateSettings: true);

        var result = await controller.GetPricingPoliciesAsync();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<PricingPoliciesDto>(ok.Value);
        Assert.Equal(RoundingRuleType.ExactMath, dto.UsdRoundingRule);
        Assert.Equal(RoundingRuleType.ExactMath, dto.CashBsRoundingRule);
        Assert.True(dto.EnforceMinimumMarginFloor);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetPricingPolicies_ValidPayload_PersistsEnumNamesAndBoolAndReturnsDto()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var controller = CreateController(db, canMutateSettings: true);

        var result = await controller.SetPricingPoliciesAsync(new PricingPoliciesDto
        {
            UsdRoundingRule = RoundingRuleType.UsdPsychological,
            CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep10,
            EnforceMinimumMarginFloor = false
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<PricingPoliciesDto>(ok.Value);
        Assert.Equal(RoundingRuleType.UsdPsychological, dto.UsdRoundingRule);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, dto.CashBsRoundingRule);
        Assert.False(dto.EnforceMinimumMarginFloor);

        // Storage por NOMBRE de enum y bool canónico ("True"/"False").
        Assert.Equal("UsdPsychological", await db.SystemSettings
            .Where(s => s.Key == SettingKeys.Pricing.DefaultUsdRoundingRule)
            .Select(s => s.Value).SingleAsync());
        Assert.Equal("VesCashFriendlyStep10", await db.SystemSettings
            .Where(s => s.Key == SettingKeys.Pricing.DefaultCashBsRoundingRule)
            .Select(s => s.Value).SingleAsync());
        Assert.Equal("False", await db.SystemSettings
            .Where(s => s.Key == SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor)
            .Select(s => s.Value).SingleAsync());
    }

    [Fact]
    public async Task SetPricingPolicies_ThenGet_RoundTripsThroughStorage()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var controller = CreateController(db, canMutateSettings: true);

        await controller.SetPricingPoliciesAsync(new PricingPoliciesDto
        {
            UsdRoundingRule = RoundingRuleType.ExactMath,
            CashBsRoundingRule = RoundingRuleType.VesCashFriendlyInteger,
            EnforceMinimumMarginFloor = true
        });

        var result = await controller.GetPricingPoliciesAsync();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<PricingPoliciesDto>(ok.Value);
        Assert.Equal(RoundingRuleType.ExactMath, dto.UsdRoundingRule);
        Assert.Equal(RoundingRuleType.VesCashFriendlyInteger, dto.CashBsRoundingRule);
        Assert.True(dto.EnforceMinimumMarginFloor);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    public async Task SetPricingPolicies_UndefinedUsdRule_ReturnsBadRequestAndPersistsNothing(int invalidRule)
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var controller = CreateController(db, canMutateSettings: true);

        var result = await controller.SetPricingPoliciesAsync(new PricingPoliciesDto
        {
            UsdRoundingRule = (RoundingRuleType)invalidRule,
            CashBsRoundingRule = RoundingRuleType.ExactMath,
            EnforceMinimumMarginFloor = true
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(await db.SystemSettings.AnyAsync());
    }

    [Fact]
    public async Task SetPricingPolicies_UndefinedCashRule_ReturnsBadRequestAndPersistsNothing()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var controller = CreateController(db, canMutateSettings: true);

        var result = await controller.SetPricingPoliciesAsync(new PricingPoliciesDto
        {
            UsdRoundingRule = RoundingRuleType.UsdPsychological,
            CashBsRoundingRule = (RoundingRuleType)42,
            EnforceMinimumMarginFloor = true
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(await db.SystemSettings.AnyAsync());
    }

    [Fact]
    public async Task SetPricingPolicies_WithoutCanMutateSettings_ReturnsForbiddenAndPersistsNothing()
    {
        using var db = CreateInMemoryDb(Guid.NewGuid().ToString());
        var controller = CreateController(db, canMutateSettings: false);

        var result = await controller.SetPricingPoliciesAsync(new PricingPoliciesDto
        {
            UsdRoundingRule = RoundingRuleType.UsdPsychological,
            CashBsRoundingRule = RoundingRuleType.ExactMath,
            EnforceMinimumMarginFloor = true
        });

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.False(await db.SystemSettings.AnyAsync());
    }
}
