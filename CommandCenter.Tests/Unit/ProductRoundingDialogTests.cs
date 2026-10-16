using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Core.Helpers;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.184-T3 (S4/S5): redondeo inteligente en vivo del modal de producto. Regla USD efectiva
/// (artículo ?? global) aplicada dentro de CalculatePricing con piso = margen objetivo del canal y
/// toggle global; proyección Bs.S de efectivo en caliente; validación viva Detal/Mayor sobre
/// redondeados; persistencia de los overrides (null = heredar) y precarga desde la política global.
/// </summary>
public class ProductRoundingDialogTests
{
    // --- Redondeo USD en vivo ---

    [Fact]
    public async Task MarginTrigger_WithGlobalPsychological_RoundsPriceWithFloorAndExposesTrace()
    {
        var vm = CreateViewModel(settings: CreateSettings(RoundingRuleType.UsdPsychological).Object);
        await vm.LoadMetadataAsync();

        vm.CostPriceUSD = 10m;
        vm.ProfitMarginRetail = 20m;
        vm.RecalculatePricing("MarginRetail");

        // Matemático 12.00 → psicológico 11.99 → el piso (20% objetivo) fuerza el escalón superior.
        Assert.Equal(12.25m, vm.PriceRetailUSD);
        Assert.Equal(20m, vm.ProfitMarginRetail); // el margen objetivo se conserva como input (8.183)
        Assert.Equal(12.00m, vm.RetailMathBaseValue);
        Assert.Equal(0.25m, vm.RetailRoundingDeltaValue);
        Assert.Equal(22.5m, vm.RetailEffectiveMarginPct);
        Assert.True(vm.ShowRetailRoundingTrace);
        Assert.Equal("$12,00", vm.RetailMathBaseText);
        Assert.Equal("+$0,25", vm.RetailRoundingDeltaText);
        Assert.Equal("22.5%", vm.RetailEffectiveMarginText);
        Assert.False(vm.ShowRetailCashFriendly);
    }

    [Fact]
    public async Task ManualPriceTrigger_WithGlobalPsychological_RoundsAndReDerivesMarginFromRounded()
    {
        var vm = CreateViewModel(settings: CreateSettings(RoundingRuleType.UsdPsychological, enforceFloor: false).Object);
        await vm.LoadMetadataAsync();

        vm.CostPriceUSD = 10m;
        vm.PriceRetailUSD = 12.30m;
        vm.RecalculatePricing("PriceRetail");

        Assert.Equal(12.25m, vm.PriceRetailUSD);
        Assert.Equal(22.5m, vm.ProfitMarginRetail); // derivado del precio FINAL redondeado
        Assert.Equal(12.30m, vm.RetailMathBaseValue);
        Assert.Equal(-0.05m, vm.RetailRoundingDeltaValue);
    }

    [Fact]
    public async Task EnforceFloorOff_DoesNotPushToUpperStep()
    {
        var vm = CreateViewModel(settings: CreateSettings(RoundingRuleType.UsdPsychological, enforceFloor: false).Object);
        await vm.LoadMetadataAsync();

        vm.CostPriceUSD = 10m;
        vm.ProfitMarginRetail = 20m;
        vm.RecalculatePricing("MarginRetail");

        Assert.Equal(11.99m, vm.PriceRetailUSD);
        Assert.Equal(19.9m, vm.RetailEffectiveMarginPct);
        Assert.Equal(-0.01m, vm.RetailRoundingDeltaValue);
    }

    [Fact]
    public async Task WithoutSettingsService_ExactMathKeepsLegacyCalculationAndHidesTrace()
    {
        var vm = CreateViewModel();
        await vm.LoadMetadataAsync();

        vm.CostPriceUSD = 4.50m;
        vm.ProfitMarginRetail = 30m;
        vm.RecalculatePricing("MarginRetail");

        Assert.Equal(5.85m, vm.PriceRetailUSD);
        Assert.Equal(30m, vm.ProfitMarginRetail);
        Assert.Equal(PricingCalculator.ToBsSCeiling(5.85m, 36.50m), vm.PriceRetailBsS);
        Assert.False(vm.ShowRetailRoundingTrace);
        Assert.Null(vm.RetailMathBaseValue);
        Assert.False(vm.ShowRetailCashFriendly);
        Assert.False(vm.HasRoundingValidationWarning);
    }

    [Fact]
    public async Task GlobalExactMath_WithSettings_KeepsLegacyCalculation()
    {
        var vm = CreateViewModel(settings: CreateSettings().Object);
        await vm.LoadMetadataAsync();

        vm.CostPriceUSD = 10m;
        vm.ProfitMarginRetail = 23m;
        vm.RecalculatePricing("MarginRetail");

        Assert.Equal(12.30m, vm.PriceRetailUSD);
        Assert.False(vm.ShowRetailRoundingTrace);
        Assert.Null(vm.RetailMathBaseValue);
    }

    // --- Selectores: heredar vs override ---

    [Fact]
    public async Task InheritGlobal_AppliesGlobalPsychological_AndPersistsNullOverride()
    {
        var product = CreateProduct();
        var vm = CreateViewModel(product, CreateSettings(RoundingRuleType.UsdPsychological).Object);
        await vm.LoadMetadataAsync();

        Assert.Null(vm.SelectedRoundingRuleUsd);
        Assert.Equal(RoundingRuleType.UsdPsychological, vm.EffectiveUsdRoundingRule);
        Assert.Contains("Psicológico", vm.RoundingUsdOptions[0].Label);
        Assert.Equal(12.25m, vm.PriceRetailUSD); // 12.00 → 11.99 → piso 20% → 12.25

        bool closed = false;
        vm.RequestClose = _ => closed = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Null(vm.ResultProduct.RoundingRuleUsd);
        Assert.Null(vm.ResultProduct.RoundingRuleCashBs);
    }

    [Fact]
    public async Task ArticleOverride_ExactMath_ShieldsPriceFromGlobalPsychological()
    {
        var product = CreateProduct();
        product.RoundingRuleUsd = RoundingRuleType.ExactMath;
        product.RoundingRuleCashBs = RoundingRuleType.ExactMath;

        var vm = CreateViewModel(product, CreateSettings(RoundingRuleType.UsdPsychological).Object);
        await vm.LoadMetadataAsync();

        Assert.Equal(RoundingRuleType.ExactMath, vm.SelectedRoundingRuleUsd);
        Assert.Equal(RoundingRuleType.ExactMath, vm.EffectiveUsdRoundingRule);
        Assert.Equal(12m, vm.PriceRetailUSD);
        Assert.False(vm.ShowRetailRoundingTrace);
        Assert.Contains("Psicológico", vm.RoundingUsdOptions[0].Label); // el label de herencia muestra la global
    }

    [Fact]
    public async Task SelectorChange_ToOverrideRoundsLive_AndBackToInheritReappliesGlobal()
    {
        var vm = CreateViewModel(settings: CreateSettings().Object); // global ExactMath
        await vm.LoadMetadataAsync();

        vm.CostPriceUSD = 10m;
        vm.ProfitMarginRetail = 20m;
        vm.RecalculatePricing("MarginRetail");
        Assert.Equal(12m, vm.PriceRetailUSD);

        vm.SelectedRoundingUsdOption = vm.RoundingUsdOptions.Single(o => o.Rule == RoundingRuleType.UsdPsychological);
        Assert.Equal(RoundingRuleType.UsdPsychological, vm.SelectedRoundingRuleUsd);
        Assert.Equal(12.25m, vm.PriceRetailUSD);

        vm.SelectedRoundingUsdOption = vm.RoundingUsdOptions[0];
        Assert.Null(vm.SelectedRoundingRuleUsd);
        Assert.Equal(12m, vm.PriceRetailUSD);
        Assert.False(vm.ShowRetailRoundingTrace);
    }

    [Fact]
    public async Task Save_PersistsArticleOverrides_AndClearsThemOnInherit()
    {
        var product = CreateProduct();
        product.RoundingRuleUsd = RoundingRuleType.UsdPsychological;
        product.RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyInteger;

        var vm = CreateViewModel(product, CreateSettings().Object);
        await vm.LoadMetadataAsync();

        Assert.Equal(RoundingRuleType.UsdPsychological, vm.SelectedRoundingRuleUsd);
        Assert.Equal(RoundingRuleType.VesCashFriendlyInteger, vm.SelectedRoundingRuleCashBs);

        bool closed = false;
        vm.RequestClose = _ => closed = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Equal(RoundingRuleType.UsdPsychological, vm.ResultProduct.RoundingRuleUsd);
        Assert.Equal(RoundingRuleType.VesCashFriendlyInteger, vm.ResultProduct.RoundingRuleCashBs);
        Assert.Equal(7, vm.ResultProductId);

        // "Heredar global" limpia el override (null = hereda, semántica de reemplazo del DTO).
        vm.SelectedRoundingUsdOption = vm.RoundingUsdOptions[0];
        vm.SelectedRoundingCashOption = vm.RoundingCashOptions[0];
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(vm.ResultProduct.RoundingRuleUsd);
        Assert.Null(vm.ResultProduct.RoundingRuleCashBs);
    }

    [Fact]
    public async Task SettingsFailure_FallsBackToDefaultsSilently()
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetPricingPoliciesAsync()).ThrowsAsync(new Exception("Connection refused"));

        var vm = CreateViewModel(settings: settings.Object);
        await vm.LoadMetadataAsync();

        Assert.Equal(RoundingRuleType.ExactMath, vm.EffectiveUsdRoundingRule);
        Assert.Equal(RoundingRuleType.ExactMath, vm.EffectiveCashBsRoundingRule);
        Assert.Contains("Sin redondeo", vm.RoundingUsdOptions[0].Label);

        vm.CostPriceUSD = 10m;
        vm.ProfitMarginRetail = 20m;
        vm.RecalculatePricing("MarginRetail");
        Assert.Equal(12m, vm.PriceRetailUSD);
    }

    // --- Proyección Bs.S en caliente ---

    [Fact]
    public async Task CashRule_ProjectsHotBsS_WithoutTouchingUsdOrCanonicalBsS()
    {
        var vm = CreateViewModel(settings: CreateSettings(cash: RoundingRuleType.VesCashFriendlyStep5).Object);
        await vm.LoadMetadataAsync();

        vm.CostPriceUSD = 10m;
        vm.ProfitMarginRetail = 20m;
        vm.RecalculatePricing("MarginRetail");

        // La regla de efectivo no toca el precio USD (queda en su matemático).
        Assert.Equal(12m, vm.PriceRetailUSD);
        Assert.False(vm.ShowRetailRoundingTrace);

        // 12.00 × 36.50 = 438.00 → múltiplo de 5 más cercano = 440 (nunca persistido).
        Assert.True(vm.ShowRetailCashFriendly);
        Assert.Equal(440m, vm.RetailCashFriendlyBsS);
        Assert.Equal("Bs.S 440,00", vm.RetailCashFriendlyText);

        // El Bs.S canónico almacenado sigue siendo el techo 8.104 del precio USD.
        Assert.Equal(PricingCalculator.ToBsSCeiling(12m, 36.50m), vm.PriceRetailBsS);
    }

    // --- Validación viva Detal/Mayor sobre redondeados ---

    [Fact]
    public async Task WholesaleCollision_AfterRounding_LowersWholesaleToLowerStep()
    {
        var vm = CreateViewModel(settings: CreateSettings(RoundingRuleType.UsdPsychological).Object);
        await vm.LoadMetadataAsync();

        vm.HasWholesale = true;
        vm.CostPriceUSD = 10m;
        vm.ProfitMarginRetail = 22m;
        vm.ProfitMarginWholesale = 20m;
        vm.RecalculatePricing("Cost");

        // Detal 12.20 → 12.25; Mayor 12.00 → 12.25 (piso) → colisión → escalón inferior 11.99 > costo.
        Assert.Equal(12.25m, vm.PriceRetailUSD);
        Assert.Equal(11.99m, vm.PriceWholesaleUSD);
        Assert.Equal(-0.01m, vm.WholesaleRoundingDeltaValue);
        Assert.False(vm.HasRoundingValidationWarning);
    }

    [Fact]
    public async Task WholesaleCollision_WhenLowerStepFallsBelowCost_ShowsWarningAndKeepsPrices()
    {
        var vm = CreateViewModel(settings: CreateSettings(RoundingRuleType.UsdPsychological).Object);
        await vm.LoadMetadataAsync();

        vm.HasWholesale = true;
        vm.CostPriceUSD = 10m;
        vm.ProfitMarginRetail = 0m;
        vm.ProfitMarginWholesale = 0m;
        vm.RecalculatePricing("Cost");

        // Detal y Mayor redondean a 9.99; bajar el Mayor al escalón 9.95 cae bajo el costo (10).
        Assert.Equal(9.99m, vm.PriceRetailUSD);
        Assert.Equal(9.99m, vm.PriceWholesaleUSD);
        Assert.True(vm.HasRoundingValidationWarning);
        Assert.Contains("escalón inferior", vm.RoundingValidationMessage);
    }

    // --- Wiring ---

    [Fact]
    public async Task InventoryViewModel_EditProduct_PassesSettingsServiceIntoDialog()
    {
        var product = CreateProduct();
        var productService = CreateProductService();
        productService.Setup(s => s.GetByIdAsync(7)).ReturnsAsync(product);

        ProductDialogViewModel? captured = null;
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowProductDialog(It.IsAny<ProductDialogViewModel>()))
              .Callback<ProductDialogViewModel>(vm => captured = vm)
              .Returns(false);

        var exchangeRate = new Mock<IExchangeRateService>();
        exchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        using var inventory = new InventoryViewModel(
            productService.Object,
            exchangeRate.Object,
            new UserSession(),
            dialog.Object,
            settingsService: CreateSettings(RoundingRuleType.UsdPsychological).Object);
        WeakReferenceMessenger.Default.UnregisterAll(inventory);

        await inventory.EditProductCommand.ExecuteAsync(new ProductItemViewModel(product, exchangeRate.Object));

        Assert.NotNull(captured);
        await captured!.LoadMetadataAsync();
        Assert.Contains("Psicológico", captured.RoundingUsdOptions[0].Label);
        Assert.Equal(RoundingRuleType.UsdPsychological, captured.EffectiveUsdRoundingRule);
    }

    // --- Unificación 8.185-T4 (S1): el Bs.S efectivo mostrado sale del helper único ---

    [Fact]
    public async Task CashRule_WithToggleOn_NeverRoundsDown_EvenWhenNearestFallsBelowMath()
    {
        // Tasa 36.51: 10.00 × 36.51 = 365.10 queda entre 365 y 370 con nearest = 365 (por debajo).
        var vm = CreateViewModel(settings: CreateSettings(cash: RoundingRuleType.VesCashFriendlyStep5).Object, rate: 36.51m);
        await vm.LoadMetadataAsync();

        vm.CostPriceUSD = 10m;
        vm.ProfitMarginRetail = 0m;
        vm.RecalculatePricing("MarginRetail");

        Assert.Equal(10m, vm.PriceRetailUSD);
        // Decisión A compartida con Catálogo/POS/Carrito: toggle ON ⇒ escalón inmediato superior 370.
        Assert.True(vm.ShowRetailCashFriendly);
        Assert.Equal(370m, vm.RetailCashFriendlyBsS);
    }

    [Fact]
    public async Task CashRule_WithToggleOff_KeepsNearestStepDown()
    {
        var vm = CreateViewModel(settings: CreateSettings(cash: RoundingRuleType.VesCashFriendlyStep5, enforceFloor: false).Object, rate: 36.51m);
        await vm.LoadMetadataAsync();

        vm.CostPriceUSD = 10m;
        vm.ProfitMarginRetail = 0m;
        vm.RecalculatePricing("MarginRetail");

        Assert.True(vm.ShowRetailCashFriendly);
        Assert.Equal(365m, vm.RetailCashFriendlyBsS);
    }

    [Fact]
    public async Task InventoryViewModel_EditProduct_WithChangedCashOverride_PublishesRoundingMessage()
    {
        var product = CreateProduct();
        var productService = CreateProductService();
        productService.Setup(s => s.GetByIdAsync(7)).ReturnsAsync(product);
        productService.Setup(s => s.UpdateAsync(It.IsAny<UpdateProductDto>())).Returns(Task.CompletedTask);

        ProductDialogViewModel? captured = null;
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowProductDialog(It.IsAny<ProductDialogViewModel>()))
              .Callback<ProductDialogViewModel>(vm =>
              {
                  captured = vm;
                  vm.SelectedRoundingCashOption = vm.RoundingCashOptions.Single(o => o.Rule == RoundingRuleType.VesCashFriendlyStep5);
                  vm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
              })
              .Returns(true);

        var exchangeRate = new Mock<IExchangeRateService>();
        exchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        ProductRoundingChangedMessage? received = null;
        var listener = new object();
        WeakReferenceMessenger.Default.Register<ProductRoundingChangedMessage>(listener, (r, m) => received = m);

        try
        {
            using var inventory = new InventoryViewModel(
                productService.Object,
                exchangeRate.Object,
                new UserSession(),
                dialog.Object,
                settingsService: CreateSettings().Object);

            await inventory.EditProductCommand.ExecuteAsync(new ProductItemViewModel(product, exchangeRate.Object));

            Assert.NotNull(captured);
            Assert.NotNull(received);
            Assert.Equal(7, received!.ProductId);
            Assert.Equal(RoundingRuleType.VesCashFriendlyStep5, received.RoundingRuleCashBs);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(listener);
        }
    }

    // --- Fixture ---

    private static ProductDto CreateProduct() => new()
    {
        Id = 7,
        Name = "Harina P.A.N.",
        SKU = "H-1",
        CostPriceUSD = 10m,
        ProfitMarginRetail = 20m,
        PriceRetailUSD = 12m,
    };

    private static Mock<IProductService> CreateProductService()
    {
        var productService = new Mock<IProductService>();
        productService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto>());
        productService.Setup(s => s.GetQuickInfoAsync(It.IsAny<string>())).ReturnsAsync((ProductQuickInfoDto?)null);
        return productService;
    }

    private static Mock<ISettingsService> CreateSettings(
        RoundingRuleType usd = RoundingRuleType.ExactMath,
        RoundingRuleType cash = RoundingRuleType.ExactMath,
        bool enforceFloor = true)
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetPricingPoliciesAsync()).ReturnsAsync(new PricingPoliciesDto
        {
            UsdRoundingRule = usd,
            CashBsRoundingRule = cash,
            EnforceMinimumMarginFloor = enforceFloor,
        });
        return settings;
    }

    private static ProductDialogViewModel CreateViewModel(
        ProductDto? product = null,
        ISettingsService? settings = null,
        decimal rate = 36.50m)
    {
        var exchangeRate = new Mock<IExchangeRateService>();
        exchangeRate.Setup(e => e.CurrentRate).Returns(rate);
        return new ProductDialogViewModel(
            CreateProductService().Object,
            exchangeRate.Object,
            product,
            userSession: null,
            dialogService: null,
            dispatcherInvoker: null,
            settingsService: settings);
    }
}
