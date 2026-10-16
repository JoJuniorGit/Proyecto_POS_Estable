using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Constants;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.185-T2 (S5/S7/S8): al agregar una línea, SalesService resuelve la regla de efectivo efectiva
/// (override del producto ?? default global de Pricing), la persiste como snapshot de la línea y
/// deriva el Bs.S efectivo con la política geométrica (ExactMath ⇒ techo canónico 8.104 intacto).
/// </summary>
public class SaleItemRoundingRuleTests
{
    private const int ProductId = 42;
    private const decimal ExchangeRate = 842.21m;

    private static SaleProductInfoDto Product(decimal priceUsd, RoundingRuleType? cashRule = null) => new()
    {
        Id = ProductId,
        Name = "Producto redondeo",
        IsActive = true,
        IsDeleted = false,
        PriceUSD = priceUsd,
        PriceRetailUSD = priceUsd,
        PriceWholesaleUSD = 0m,
        PriceBsS = PricingCalculator.ToBsSCeiling(priceUsd, ExchangeRate),
        HasWholesale = false,
        MinWholesaleQuantity = 6m,
        IsFractional = false,
        UnitOfMeasure = UnitOfMeasureType.Und,
        CostPriceUSD = null,
        RoundingRuleCashBs = cashRule
    };

    private static ISystemSettingsService MockSettings(string? globalCashRule, string? enforceFloorSetting)
    {
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(SettingKeys.Pricing.DefaultCashBsRoundingRule)).ReturnsAsync(globalCashRule);
        settings.Setup(s => s.GetSettingAsync(SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor)).ReturnsAsync(enforceFloorSetting);
        return settings.Object;
    }

    private static async Task<(SalesService Service, SalesDbContext Context, SaleDto Sale)> CreateAsync(
        SaleProductInfoDto product,
        ISystemSettingsService settingsService)
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Customers.Add(new Customer { Id = 1, Name = "Consumidor Final", CedulaOrRif = "V-00000000", IsDefault = true });
        await context.SaveChangesAsync();

        var inventory = new Mock<IInventoryService>();
        inventory.Setup(i => i.GetSaleProductByIdAsync(ProductId, It.IsAny<CancellationToken>())).ReturnsAsync(product);
        inventory.Setup(i => i.GetSaleProductsByIdsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { product });

        var service = new SalesService(context, inventory.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), settingsService);
        var sale = await service.StartSaleAsync(1);
        return (service, context, sale);
    }

    [Fact]
    public async Task AddItemAsync_WithGlobalCashRule_PersistsResolvedRuleAndEffectiveBsS()
    {
        var (service, context, sale) = await CreateAsync(
            Product(1.30m),
            MockSettings(nameof(RoundingRuleType.VesCashFriendlyStep10), "True"));

        var dto = await service.AddItemAsync(sale.Id, ProductId, 1m, ExchangeRate);

        // 1.30 × 842.21 = 1094.873 → nearest paso 10 = 1090 (por debajo) → decisión A sube a 1100.
        var persisted = await context.SaleItems.AsNoTracking().SingleAsync(i => i.SaleId == sale.Id);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, persisted.RoundingRuleCashBs);
        Assert.Equal(1100m, persisted.UnitPriceBsS);
        Assert.Equal(1100m, persisted.SubtotalBsS);

        var itemDto = Assert.Single(dto.Items);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, itemDto.RoundingRuleCashBs);
        Assert.Equal(1100m, itemDto.UnitPriceBsS);
        Assert.Equal(1100m, dto.TotalBsS);
    }

    [Fact]
    public async Task AddItemAsync_WithProductOverride_OverridesTheGlobalRule()
    {
        var (service, context, sale) = await CreateAsync(
            Product(1.30m, cashRule: RoundingRuleType.VesCashFriendlyStep5),
            MockSettings(nameof(RoundingRuleType.VesCashFriendlyStep10), "True"));

        var dto = await service.AddItemAsync(sale.Id, ProductId, 1m, ExchangeRate);

        // nearest paso 5 = 1095 (0.127 por encima) → nunca hacia abajo lo deja igual.
        var persisted = await context.SaleItems.AsNoTracking().SingleAsync(i => i.SaleId == sale.Id);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep5, persisted.RoundingRuleCashBs);
        Assert.Equal(1095m, persisted.UnitPriceBsS);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep5, Assert.Single(dto.Items).RoundingRuleCashBs);
    }

    [Fact]
    public async Task AddItemAsync_WithNeverRoundDownDisabled_KeepsTheNearestStep()
    {
        var (service, context, sale) = await CreateAsync(
            Product(1.30m),
            MockSettings(nameof(RoundingRuleType.VesCashFriendlyStep10), "False"));

        await service.AddItemAsync(sale.Id, ProductId, 1m, ExchangeRate);

        var persisted = await context.SaleItems.AsNoTracking().SingleAsync(i => i.SaleId == sale.Id);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, persisted.RoundingRuleCashBs);
        Assert.Equal(1090m, persisted.UnitPriceBsS);
    }

    [Fact]
    public async Task AddItemAsync_WithExactMathDefault_KeepsTheCanonicalCeiling()
    {
        var (service, context, sale) = await CreateAsync(Product(1.30m), MockSettings(globalCashRule: null, enforceFloorSetting: null));

        var dto = await service.AddItemAsync(sale.Id, ProductId, 1m, ExchangeRate);

        var persisted = await context.SaleItems.AsNoTracking().SingleAsync(i => i.SaleId == sale.Id);
        Assert.Equal(RoundingRuleType.ExactMath, persisted.RoundingRuleCashBs);
        Assert.Equal(1094.88m, persisted.UnitPriceBsS); // techo 8.104: Ceiling(1094.873 × 100) / 100
        Assert.Equal(1094.88m, dto.TotalBsS);
    }

    [Fact]
    public async Task AddItemAsync_WithCustomUsdPrice_KeepsCurrentCeilingBehavior()
    {
        var (service, context, sale) = await CreateAsync(
            Product(1.30m),
            MockSettings(nameof(RoundingRuleType.VesCashFriendlyStep10), "True"));

        var dto = await service.AddItemAsync(sale.Id, ProductId, 1m, ExchangeRate, customUnitPriceUsd: 2.00m, isPriceOverrideAuthorized: true);

        // Línea manual: comportamiento actual intacto (techo sobre el precio manual = 1684.42, sin
        // escalera, que daría 1690); la regla resuelta queda igualmente snapshot de la línea.
        var persisted = await context.SaleItems.AsNoTracking().SingleAsync(i => i.SaleId == sale.Id);
        Assert.True(persisted.IsCustomPrice);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, persisted.RoundingRuleCashBs);
        Assert.Equal(1684.42m, persisted.UnitPriceBsS);
        Assert.Equal(1684.42m, Assert.Single(dto.Items).UnitPriceBsS);
    }

    [Fact]
    public async Task AddItemAsync_WithNullSettingsService_StaysDefaultSafe()
    {
        // Composición defensiva real del repo (BatchPaymentTests pasa null! como ISystemSettingsService).
        var (service, context, sale) = await CreateAsync(Product(1.30m), null!);

        await service.AddItemAsync(sale.Id, ProductId, 1m, ExchangeRate);

        var persisted = await context.SaleItems.AsNoTracking().SingleAsync(i => i.SaleId == sale.Id);
        Assert.Equal(RoundingRuleType.ExactMath, persisted.RoundingRuleCashBs);
        Assert.Equal(1094.88m, persisted.UnitPriceBsS);
    }

    [Fact]
    public async Task AddItemAsync_MergingAnExistingItem_KeepsTheResolvedRuleAndRecomputesEffectiveBsS()
    {
        var (service, context, sale) = await CreateAsync(
            Product(1.30m),
            MockSettings(nameof(RoundingRuleType.VesCashFriendlyStep10), "True"));

        await service.AddItemAsync(sale.Id, ProductId, 1m, ExchangeRate);
        var dto = await service.AddItemAsync(sale.Id, ProductId, 1m, ExchangeRate);

        var persisted = await context.SaleItems.AsNoTracking().SingleAsync(i => i.SaleId == sale.Id);
        Assert.Equal(2m, persisted.Quantity);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, persisted.RoundingRuleCashBs);
        Assert.Equal(1100m, persisted.UnitPriceBsS);
        Assert.Equal(2200m, persisted.SubtotalBsS);
        Assert.Equal(2m, Assert.Single(dto.Items).Quantity);
    }
}
