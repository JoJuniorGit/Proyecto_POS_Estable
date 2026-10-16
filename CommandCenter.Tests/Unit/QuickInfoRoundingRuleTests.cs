using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Constants;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.185-T2 (S1/S2/S6): las quick-info del catálogo/POS resuelven la regla de efectivo
/// (override del artículo ?? default global de Pricing) sin exponer costo ni margen.
/// </summary>
public class QuickInfoRoundingRuleTests
{
    private static Product CreateProduct(int id, string sku, RoundingRuleType? cashRule = null) => new()
    {
        Id = id,
        SKU = sku,
        Name = $"Producto {sku}",
        PriceUSD = 1.30m,
        PriceRetailUSD = 1.30m,
        PriceBsS = 1094.88m,
        CostPriceUSD = 0.50m,
        ProfitMarginRetail = 160m,
        ProfitPercentage = 160m,
        HasWholesale = false,
        MinWholesaleQuantity = 6m,
        IsActive = true,
        RoundingRuleCashBs = cashRule
    };

    private static ISystemSettingsService MockSettingsWithGlobalRule(string? rule)
    {
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(SettingKeys.Pricing.DefaultCashBsRoundingRule)).ReturnsAsync(rule);
        return settings.Object;
    }

    private static InventoryService CreateService(InventoryDbContext context, ISystemSettingsService? settingsService)
    {
        var user = new Mock<ICurrentUserService>();
        user.Setup(u => u.CanMutateCatalog).Returns(false);
        return settingsService == null
            ? new InventoryService(context, user.Object)
            : new InventoryService(context, user.Object, cache: null, settingsService);
    }

    [Fact]
    public async Task GetSuggestionsAsync_ExactSku_WithoutOverride_ResolvesTheGlobalDefaultRule()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        context.Products.Add(CreateProduct(1, "TEST-01"));
        await context.SaveChangesAsync();
        var service = CreateService(context, MockSettingsWithGlobalRule(nameof(RoundingRuleType.VesCashFriendlyStep5)));

        var result = await service.GetSuggestionsAsync("TEST-01", activeOnly: true, token: CancellationToken.None);

        var dto = Assert.Single(result);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep5, dto.RoundingRuleCashBs);
    }

    [Fact]
    public async Task GetSuggestionsAsync_TextSearch_WithoutOverride_ResolvesTheGlobalDefaultRule()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        context.Products.Add(CreateProduct(1, "TEST-01"));
        await context.SaveChangesAsync();
        var service = CreateService(context, MockSettingsWithGlobalRule(nameof(RoundingRuleType.VesCashFriendlyStep10)));

        var result = await service.GetSuggestionsAsync("Producto", activeOnly: true, token: CancellationToken.None);

        var dto = Assert.Single(result);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, dto.RoundingRuleCashBs);
    }

    [Fact]
    public async Task GetSuggestionsAsync_WithOverride_KeepsTheProductRuleOverTheGlobalDefault()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        context.Products.Add(CreateProduct(1, "TEST-01", cashRule: RoundingRuleType.VesCashFriendlyInteger));
        await context.SaveChangesAsync();
        var service = CreateService(context, MockSettingsWithGlobalRule(nameof(RoundingRuleType.VesCashFriendlyStep5)));

        var result = await service.GetSuggestionsAsync("TEST-01", activeOnly: true, token: CancellationToken.None);

        var dto = Assert.Single(result);
        Assert.Equal(RoundingRuleType.VesCashFriendlyInteger, dto.RoundingRuleCashBs);
    }

    [Fact]
    public async Task GetSuggestionsAsync_WithoutGlobalSetting_FallsBackToExactMath()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        context.Products.Add(CreateProduct(1, "TEST-01"));
        await context.SaveChangesAsync();
        var service = CreateService(context, MockSettingsWithGlobalRule(rule: null));

        var result = await service.GetSuggestionsAsync("TEST-01", activeOnly: true, token: CancellationToken.None);

        var dto = Assert.Single(result);
        Assert.Equal(RoundingRuleType.ExactMath, dto.RoundingRuleCashBs);
    }

    [Fact]
    public async Task GetSuggestionsAsync_WithoutSettingsAccessor_FallsBackToExactMath()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        context.Products.Add(CreateProduct(1, "TEST-01"));
        await context.SaveChangesAsync();
        var service = CreateService(context, settingsService: null);

        var result = await service.GetSuggestionsAsync("TEST-01", activeOnly: true, token: CancellationToken.None);

        var dto = Assert.Single(result);
        Assert.Equal(RoundingRuleType.ExactMath, dto.RoundingRuleCashBs);
    }

    [Fact]
    public async Task GetProductQuickInfoAsync_QuickCheck_WithoutOverride_ResolvesTheGlobalDefaultRule()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        context.Products.Add(CreateProduct(1, "TEST-01"));
        await context.SaveChangesAsync();
        var service = CreateService(context, MockSettingsWithGlobalRule(nameof(RoundingRuleType.VesCashFriendlyStep5)));

        var info = await service.GetProductQuickInfoAsync("TEST-01", useCache: false, cancellationToken: CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep5, info!.RoundingRuleCashBs);
    }

    [Fact]
    public void ProductQuickInfoDto_StillExposesNoCostOrMarginMembers()
    {
        // S6: la resolución de la regla no amplía la superficie sensible del DTO de ventas.
        var sensitive = typeof(ProductQuickInfoDto)
            .GetProperties()
            .Select(p => p.Name)
            .Where(name => name.Contains("Cost", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Margin", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(sensitive);
    }
}
