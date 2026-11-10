using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.Constants;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.186-T1 (S1/S3/S6): proyección server-side del precio efectivo Bs.S (Detal y Mayor) en las
/// superficies de lectura que alimentan la web — catálogo paginado, sugerencias, quick-check y
/// variantes — con el MISMO motor que el POS (PricingRoundingPolicy, sin matemática en JS), una
/// sola lectura de tasa/regla/toggle por request y supervivencia al masking de Cashier.
/// </summary>
public class WebEffectivePriceProjectionTests
{
    private const decimal Rate = 36.50m;
    private const string ExchangeRateCacheKey = "bcv_rate_today";

    // Moq: los parámetros out requieren un delegado explícito para inyectar el valor cacheado.
    public delegate bool TryGetValueCallback(object key, out object? value);

    private static Product CreateProduct(
        int id,
        string sku,
        decimal retailUsd,
        decimal wholesaleUsd = 0m,
        bool hasWholesale = false,
        decimal priceBsS = 0m,
        RoundingRuleType? cashRule = null) => new()
    {
        Id = id,
        SKU = sku,
        Name = $"Producto {sku}",
        PriceUSD = retailUsd,
        PriceRetailUSD = retailUsd,
        PriceWholesaleUSD = wholesaleUsd,
        HasWholesale = hasWholesale,
        PriceBsS = priceBsS,
        CostPriceUSD = 1m,
        ProfitMarginRetail = 20m,
        ProfitPercentage = 20m,
        MinWholesaleQuantity = 6m,
        IsActive = true,
        RoundingRuleCashBs = cashRule
    };

    private static async Task SeedRateAsync(InventoryDbContext context, decimal rate)
    {
        context.ExchangeRateHistory.Add(new ExchangeRateHistory
        {
            Date = TimeZoneHelper.GetVenezuelaDate(),
            Rate = rate,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private static Mock<ISystemSettingsService> SettingsMock(string? cashRule = null, string? enforceFloor = null)
    {
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(SettingKeys.Pricing.DefaultCashBsRoundingRule)).ReturnsAsync(cashRule);
        settings.Setup(s => s.GetSettingAsync(SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor)).ReturnsAsync(enforceFloor);
        return settings;
    }

    private static InventoryService CreateService(
        InventoryDbContext context,
        ISystemSettingsService? settings = null,
        IMemoryCache? cache = null)
    {
        var user = new Mock<ICurrentUserService>();
        user.Setup(u => u.CanMutateCatalog).Returns(false);
        user.Setup(u => u.UserId).Returns("web-effective-price-tests");
        return new InventoryService(context, user.Object, cache, settings);
    }

    // ---------------------------------------------------------------------------------------------
    // (a) Sin regla cash (ExactMath/global nulo): efectivo == canónico de hoy, byte-idéntico.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PagedCatalog_ExactMathFallback_EffectiveEqualsCanonicalCeilingToday()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        await SeedRateAsync(context, 842.21m);
        // PriceBsS deliberadamente "viejo": la proyección deriva de USD × tasa vigente, no del snapshot.
        context.Products.Add(CreateProduct(1, "EFF-A1", retailUsd: 0.81m, priceBsS: 999m));
        await context.SaveChangesAsync();
        var service = CreateService(context, SettingsMock(cashRule: null).Object);

        var page = await service.GetProductsPagedAsync(null, 1, 50);

        var item = Assert.Single(page.Items);
        decimal canonicalToday = PricingCalculator.ToBsSCeiling(0.81m, 842.21m);
        Assert.Equal(682.20m, canonicalToday);
        Assert.Equal(canonicalToday, item.EffectivePriceBsS);
        // Misma semántica que el helper único del cliente WPF (S3): paridad byte a byte.
        Assert.Equal(
            Desktop.Client.Helpers.EffectivePriceVesHelper.EffectiveBsS(0.81m, 842.21m, cashRule: null, neverRoundDown: true),
            item.EffectivePriceBsS);
        Assert.Equal(999m, item.PriceBsS); // el canónico persistido no se pisa (aditivo)
        Assert.Equal(0m, item.EffectiveWholesalePriceBsS);
    }

    [Fact]
    public async Task QuickInfo_GlobalExactMath_EffectiveEqualsCanonicalCeilingToday()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        await SeedRateAsync(context, Rate);
        context.Products.Add(CreateProduct(1, "EFF-A2", retailUsd: 12m, priceBsS: 1m));
        await context.SaveChangesAsync();
        var service = CreateService(context, SettingsMock(cashRule: nameof(RoundingRuleType.ExactMath)).Object);

        var info = await service.GetProductQuickInfoAsync("EFF-A2", useCache: false, CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal(438m, PricingCalculator.ToBsSCeiling(12m, Rate));
        Assert.Equal(438m, info!.EffectivePriceBsS);
        Assert.Equal(0m, info.EffectiveWholesalePriceBsS);
    }

    // ---------------------------------------------------------------------------------------------
    // (b) Regla cash por artículo (Ent/×5/×10): el efectivo es EXACTAMENTE el resultado del kernel.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Suggestions_ArticleIntegerOverride_ProjectsKernelResultExactly()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        await SeedRateAsync(context, Rate);
        context.Products.Add(CreateProduct(1, "EFF-B1", retailUsd: 12m, cashRule: RoundingRuleType.VesCashFriendlyInteger));
        await context.SaveChangesAsync();
        var service = CreateService(context, SettingsMock(cashRule: nameof(RoundingRuleType.ExactMath)).Object);

        var result = await service.GetSuggestionsAsync("EFF-B1", activeOnly: true, CancellationToken.None);

        var dto = Assert.Single(result);
        var kernel = PricingRoundingPolicy.ProjectCashFriendlyBsS(12m, Rate, RoundingRuleType.VesCashFriendlyInteger, neverRoundDown: true);
        Assert.NotNull(kernel);
        Assert.Equal(438m, kernel!.Value.RoundedValue);
        Assert.Equal(kernel.Value.RoundedValue, dto.EffectivePriceBsS);
    }

    [Fact]
    public async Task QuickInfo_ArticleStep5Override_ProjectsKernelResultExactly()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        await SeedRateAsync(context, Rate);
        context.Products.Add(CreateProduct(1, "EFF-B2", retailUsd: 12m, cashRule: RoundingRuleType.VesCashFriendlyStep5));
        await context.SaveChangesAsync();
        var service = CreateService(context, SettingsMock(cashRule: nameof(RoundingRuleType.ExactMath)).Object);

        var info = await service.GetProductQuickInfoAsync("EFF-B2", useCache: false, CancellationToken.None);

        Assert.NotNull(info);
        var kernel = PricingRoundingPolicy.ProjectCashFriendlyBsS(12m, Rate, RoundingRuleType.VesCashFriendlyStep5, neverRoundDown: true);
        Assert.NotNull(kernel);
        Assert.Equal(440m, kernel!.Value.RoundedValue); // 438 ⇒ techo a múltiplo de 5
        Assert.Equal(kernel.Value.RoundedValue, info!.EffectivePriceBsS);
    }

    [Fact]
    public async Task PagedCatalog_ArticleStep10Override_ToggleOn_CeilsToTheNextStep()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        await SeedRateAsync(context, Rate);
        context.Products.Add(CreateProduct(1, "EFF-B3", retailUsd: 12m, cashRule: RoundingRuleType.VesCashFriendlyStep10));
        await context.SaveChangesAsync();
        // Sin valor explícito del toggle rige el default true (contrato 8.184/8.185).
        var service = CreateService(context, SettingsMock(cashRule: nameof(RoundingRuleType.ExactMath)).Object);

        var page = await service.GetProductsPagedAsync(null, 1, 50);

        var item = Assert.Single(page.Items);
        var kernel = PricingRoundingPolicy.ProjectCashFriendlyBsS(12m, Rate, RoundingRuleType.VesCashFriendlyStep10, neverRoundDown: true);
        Assert.NotNull(kernel);
        Assert.Equal(440m, kernel!.Value.RoundedValue); // 438 ⇒ techo a múltiplo de 10
        Assert.Equal(kernel.Value.RoundedValue, item.EffectivePriceBsS);
    }

    [Fact]
    public async Task PagedCatalog_ArticleStep10Override_ToggleOff_UsesNearestStep()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        await SeedRateAsync(context, Rate);
        context.Products.Add(CreateProduct(1, "EFF-B4", retailUsd: 1.20m, cashRule: RoundingRuleType.VesCashFriendlyStep10));
        await context.SaveChangesAsync();
        var service = CreateService(context, SettingsMock(cashRule: nameof(RoundingRuleType.ExactMath), enforceFloor: "false").Object);

        var page = await service.GetProductsPagedAsync(null, 1, 50);

        var item = Assert.Single(page.Items);
        var kernel = PricingRoundingPolicy.ProjectCashFriendlyBsS(1.20m, Rate, RoundingRuleType.VesCashFriendlyStep10, neverRoundDown: false);
        Assert.NotNull(kernel);
        Assert.Equal(40m, kernel!.Value.RoundedValue); // 43,80 ⇒ múltiplo de 10 más cercano
        Assert.Equal(kernel.Value.RoundedValue, item.EffectivePriceBsS);
    }

    [Fact]
    public async Task PagedCatalog_NoOverride_UsesGlobalDefaultRuleForTheProjection()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        await SeedRateAsync(context, Rate);
        context.Products.Add(CreateProduct(1, "EFF-B5", retailUsd: 12m));
        await context.SaveChangesAsync();
        var service = CreateService(context, SettingsMock(cashRule: nameof(RoundingRuleType.VesCashFriendlyStep10)).Object);

        var page = await service.GetProductsPagedAsync(null, 1, 50);

        var item = Assert.Single(page.Items);
        Assert.Equal(440m, item.EffectivePriceBsS);
    }

    // ---------------------------------------------------------------------------------------------
    // (c) Mayor: base PriceWholesaleUSD con la misma regla; 0 cuando el canal no aplica.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PagedCatalog_WholesaleEffective_ProjectsWholesaleBaseAndZeroWhenNotApplicable()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        await SeedRateAsync(context, Rate);
        context.Products.Add(CreateProduct(1, "EFF-C1", retailUsd: 12m, wholesaleUsd: 11m, hasWholesale: true, cashRule: RoundingRuleType.VesCashFriendlyStep10));
        context.Products.Add(CreateProduct(2, "EFF-C2", retailUsd: 12m, wholesaleUsd: 0m, hasWholesale: true, cashRule: RoundingRuleType.VesCashFriendlyStep10));
        context.Products.Add(CreateProduct(3, "EFF-C3", retailUsd: 12m, wholesaleUsd: 11m, hasWholesale: false, cashRule: RoundingRuleType.VesCashFriendlyStep10));
        await context.SaveChangesAsync();
        var service = CreateService(context, SettingsMock(cashRule: null).Object);

        var page = await service.GetProductsPagedAsync(null, 1, 50);

        var withWholesale = page.Items.Single(i => i.SKU == "EFF-C1");
        Assert.Equal(440m, withWholesale.EffectivePriceBsS);
        var kernel = PricingRoundingPolicy.ProjectCashFriendlyBsS(11m, Rate, RoundingRuleType.VesCashFriendlyStep10, neverRoundDown: true);
        Assert.NotNull(kernel);
        Assert.Equal(410m, kernel!.Value.RoundedValue); // 401,50 ⇒ techo a múltiplo de 10
        Assert.Equal(kernel.Value.RoundedValue, withWholesale.EffectiveWholesalePriceBsS);

        Assert.Equal(0m, page.Items.Single(i => i.SKU == "EFF-C2").EffectiveWholesalePriceBsS);
        Assert.Equal(0m, page.Items.Single(i => i.SKU == "EFF-C3").EffectiveWholesalePriceBsS);
    }

    // ---------------------------------------------------------------------------------------------
    // Superficie de variantes usada por la web (VariantSelector): mismos campos, regla resuelta.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task VariantsAndParents_ProjectEffectiveFields()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        await SeedRateAsync(context, Rate);
        var parent = CreateProduct(1, "EFF-P1", retailUsd: 12m);
        parent.IsGroupHeader = true;
        context.Products.Add(parent);
        var variant = CreateProduct(2, "EFF-V1", retailUsd: 12m, wholesaleUsd: 11m, hasWholesale: true, cashRule: RoundingRuleType.VesCashFriendlyStep5);
        variant.ParentProductId = parent.Id;
        context.Products.Add(variant);
        await context.SaveChangesAsync();
        var service = CreateService(context, SettingsMock(cashRule: nameof(RoundingRuleType.ExactMath)).Object);

        var variants = await service.GetVariantOptionsAsync(parent.Id);

        var dto = Assert.Single(variants);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep5, dto.RoundingRuleCashBs); // regla cruda proyectada
        Assert.Equal(440m, dto.EffectivePriceBsS);
        Assert.Equal(405m, dto.EffectiveWholesalePriceBsS); // 401,50 ⇒ techo a múltiplo de 5

        var parents = await service.GetParentProductsAsync();
        var parentDto = Assert.Single(parents);
        Assert.Equal(438m, parentDto.EffectivePriceBsS);
        Assert.Equal(0m, parentDto.EffectiveWholesalePriceBsS);
    }

    // ---------------------------------------------------------------------------------------------
    // (d) Masking de Cashier: los campos efectivos no son sensibles y sobreviven; costo sigue oculto.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ProductsController_GetAll_AsCashier_KeepsEffectivePriceFieldsAndMasksCosts()
    {
        var dto = new ProductDto
        {
            Id = 1,
            Name = "Producto SKU-1",
            SKU = "SKU-1",
            PriceUSD = 12m,
            PriceRetailUSD = 12m,
            PriceWholesaleUSD = 11m,
            PriceBsS = 438m,
            EffectivePriceBsS = 440m,
            EffectiveWholesalePriceBsS = 410m,
            HasWholesale = true,
            RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyStep10,
            CostPriceUSD = 6m,
            Cost = 6m,
            ProfitMarginRetail = 50m,
            ProfitMarginWholesale = 45m,
            ProfitPercentage = 100m
        };
        var paged = new PagedResultDto<ProductDto>(new List<ProductDto> { dto }, totalCount: 1);
        var mockService = new Mock<IInventoryService>();
        mockService
            .Setup(s => s.GetProductsPagedAsync(null, 1, 50, null, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(paged);
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(false);

        var controller = new ProductsController(mockService.Object, Mock.Of<IProductManagementService>(), mockUser.Object);
        var result = await controller.GetAllAsync();

        var payload = Assert.IsType<PagedResultDto<ProductDto>>(result.Value);
        var item = Assert.Single(payload.Items);
        Assert.Equal(440m, item.EffectivePriceBsS);
        Assert.Equal(410m, item.EffectiveWholesalePriceBsS);
        Assert.Equal(0m, item.CostPriceUSD);
        Assert.Equal(0m, item.Cost);
        Assert.Equal(0m, item.ProfitMarginRetail);
        Assert.Equal(0m, item.ProfitPercentage);
    }

    [Fact]
    public async Task ProductsController_GetQuickInfo_AsCashier_KeepsEffectivePriceFields()
    {
        var info = new ProductQuickInfoDto
        {
            Id = 7,
            SKU = "SKU-7",
            Name = "Producto SKU-7",
            PriceUSD = 12m,
            PriceRetailUSD = 12m,
            PriceWholesaleUSD = 11m,
            PriceBsS = 438m,
            EffectivePriceBsS = 440m,
            EffectiveWholesalePriceBsS = 405m,
            HasWholesale = true,
            RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyStep5,
            ProfitPercentage = 100m
        };
        var mockService = new Mock<IInventoryService>();
        mockService
            .Setup(s => s.GetProductQuickInfoAsync("SKU-7", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(info);
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(false);

        var controller = new ProductsController(mockService.Object, Mock.Of<IProductManagementService>(), mockUser.Object);
        var result = await controller.GetQuickInfoAsync("SKU-7");

        var dto = Assert.IsType<ProductQuickInfoDto>(result.Value);
        Assert.Equal(440m, dto.EffectivePriceBsS);
        Assert.Equal(405m, dto.EffectiveWholesalePriceBsS);
        Assert.Equal(0m, dto.ProfitPercentage);
    }

    // ---------------------------------------------------------------------------------------------
    // (e) Una sola lectura de tasa/regla/toggle por request (sin lecturas por ítem).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PagedCatalog_WithMultipleCashItems_ReadsRateRuleAndToggleOncePerRequest()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        context.Products.Add(CreateProduct(1, "EFF-E1", retailUsd: 12m, cashRule: RoundingRuleType.VesCashFriendlyStep10));
        context.Products.Add(CreateProduct(2, "EFF-E2", retailUsd: 12m, cashRule: RoundingRuleType.VesCashFriendlyStep10));
        context.Products.Add(CreateProduct(3, "EFF-E3", retailUsd: 12m, cashRule: RoundingRuleType.VesCashFriendlyStep10));
        await context.SaveChangesAsync();

        var settings = SettingsMock(cashRule: nameof(RoundingRuleType.VesCashFriendlyStep10), enforceFloor: "true");
        var rateCache = new Mock<IMemoryCache>();
        rateCache
            .Setup(c => c.TryGetValue(ExchangeRateCacheKey, out It.Ref<object?>.IsAny))
            .Returns(new TryGetValueCallback((object key, out object? value) =>
            {
                value = Rate;
                return true;
            }));

        var service = CreateService(context, settings.Object, rateCache.Object);

        var page = await service.GetProductsPagedAsync(null, 1, 50);

        Assert.Equal(3, page.Items.Count());
        Assert.All(page.Items, item => Assert.Equal(440m, item.EffectivePriceBsS));
        settings.Verify(s => s.GetSettingAsync(SettingKeys.Pricing.DefaultCashBsRoundingRule), Times.Once);
        settings.Verify(s => s.GetSettingAsync(SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor), Times.Once);
        // La tasa se resuelve una vez para toda la página (la caché recibe una única consulta).
        rateCache.Verify(c => c.TryGetValue(ExchangeRateCacheKey, out It.Ref<object?>.IsAny), Times.Once);
    }
}
