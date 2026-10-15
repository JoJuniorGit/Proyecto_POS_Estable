using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.184-T4 (S6): la política global (regla USD + toggle de piso) pre-inicializa los selectores de
/// la edición masiva al abrir el diálogo. Sin políticas (o con la lectura caída) rigen los defaults
/// ExactMath / sin piso — comportamiento 8.181 byte-idéntico — y el usuario puede cambiarlos igual.
/// </summary>
public class BulkPriceEditPricingPoliciesTests
{
    private static BulkPriceSelection Selection() =>
        new(new List<int> { 1, 2 }, null, SelectAllFiltered: false, SelectedCount: 2);

    private static PricingPoliciesDto GlobalPolicies() => new()
    {
        UsdRoundingRule = RoundingRuleType.UsdPsychological,
        CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep5,
        EnforceMinimumMarginFloor = true
    };

    [Fact]
    public void Constructor_WithPricingPolicies_SeedsChannelRulesAndFloorToggle()
    {
        var vm = new BulkPriceEditDialogViewModel(
            Mock.Of<IProductService>(),
            Mock.Of<IDialogService>(),
            Selection(),
            GlobalPolicies());

        Assert.Equal(RoundingRuleType.UsdPsychological, vm.RetailRoundingRule);
        Assert.Equal(RoundingRuleType.UsdPsychological, vm.WholesaleRoundingRule);
        Assert.True(vm.EnforceMinimumMarginFloor);
        // La preselección no inventa una vista previa.
        Assert.False(vm.HasPreview);
    }

    [Fact]
    public void Constructor_WithoutPricingPolicies_KeepsBaselineDefaults()
    {
        var vm = new BulkPriceEditDialogViewModel(
            Mock.Of<IProductService>(),
            Mock.Of<IDialogService>(),
            Selection());

        Assert.Equal(RoundingRuleType.ExactMath, vm.RetailRoundingRule);
        Assert.Equal(RoundingRuleType.ExactMath, vm.WholesaleRoundingRule);
        Assert.False(vm.EnforceMinimumMarginFloor);
        Assert.False(vm.HasPreview);
    }

    [Fact]
    public async Task OpenBulkPriceEdit_LoadsGlobalPoliciesBeforeOpeningDialog()
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(service => service.GetPricingPoliciesAsync()).ReturnsAsync(GlobalPolicies());
        var (vm, dialog, _) = await ArrangeCatalogAsync(settings.Object);

        vm.Products[0].IsSelected = true;
        BulkPriceEditDialogViewModel? opened = null;
        dialog
            .Setup(d => d.ShowBulkPriceEditDialog(It.IsAny<BulkPriceEditDialogViewModel>()))
            .Callback<BulkPriceEditDialogViewModel>(dialogVm => opened = dialogVm)
            .Returns(false);

        await vm.OpenBulkPriceEditCommand.ExecuteAsync(null);

        Assert.NotNull(opened);
        Assert.Equal(RoundingRuleType.UsdPsychological, opened!.RetailRoundingRule);
        Assert.Equal(RoundingRuleType.UsdPsychological, opened.WholesaleRoundingRule);
        Assert.True(opened.EnforceMinimumMarginFloor);
        settings.Verify(service => service.GetPricingPoliciesAsync(), Times.Once);
    }

    [Fact]
    public async Task OpenBulkPriceEdit_WhenPolicyLoadFails_OpensDialogWithDefaults()
    {
        var settings = new Mock<ISettingsService>();
        settings
            .Setup(service => service.GetPricingPoliciesAsync())
            .ThrowsAsync(new InvalidOperationException("Connection refused"));
        var (vm, dialog, _) = await ArrangeCatalogAsync(settings.Object);

        vm.Products[0].IsSelected = true;
        BulkPriceEditDialogViewModel? opened = null;
        dialog
            .Setup(d => d.ShowBulkPriceEditDialog(It.IsAny<BulkPriceEditDialogViewModel>()))
            .Callback<BulkPriceEditDialogViewModel>(dialogVm => opened = dialogVm)
            .Returns(false);

        await vm.OpenBulkPriceEditCommand.ExecuteAsync(null);

        Assert.NotNull(opened);
        Assert.Equal(RoundingRuleType.ExactMath, opened!.RetailRoundingRule);
        Assert.Equal(RoundingRuleType.ExactMath, opened.WholesaleRoundingRule);
        Assert.False(opened.EnforceMinimumMarginFloor);
    }

    [Fact]
    public async Task OpenBulkPriceEdit_WithoutSettingsService_OpensDialogWithDefaults()
    {
        var (vm, dialog, _) = await ArrangeCatalogAsync(settingsService: null);

        vm.Products[0].IsSelected = true;
        BulkPriceEditDialogViewModel? opened = null;
        dialog
            .Setup(d => d.ShowBulkPriceEditDialog(It.IsAny<BulkPriceEditDialogViewModel>()))
            .Callback<BulkPriceEditDialogViewModel>(dialogVm => opened = dialogVm)
            .Returns(false);

        await vm.OpenBulkPriceEditCommand.ExecuteAsync(null);

        Assert.NotNull(opened);
        Assert.Equal(RoundingRuleType.ExactMath, opened!.RetailRoundingRule);
        Assert.False(opened.EnforceMinimumMarginFloor);
    }

    private static async Task<(InventoryViewModel ViewModel, Mock<IDialogService> Dialog, Mock<IProductService> ProductService)> ArrangeCatalogAsync(
        ISettingsService? settingsService)
    {
        var productService = new Mock<IProductService>();
        productService
            .Setup(s => s.GetPagedAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int?>()))
            .ReturnsAsync(new PagedResultDto<ProductDto>
            {
                Items = new List<ProductDto>
                {
                    new() { Id = 1, Name = "Producto 1", SKU = "SKU-1", PriceUSD = 1m, PriceBsS = 36.5m, IsActive = true },
                    new() { Id = 2, Name = "Producto 2", SKU = "SKU-2", PriceUSD = 2m, PriceBsS = 73m, IsActive = true }
                },
                TotalCount = 2,
                HasMore = false
            });

        var exchangeRateService = new Mock<IExchangeRateService>();
        exchangeRateService.Setup(s => s.CurrentRate).Returns(36.5m);
        exchangeRateService
            .Setup(s => s.GetCurrentRateAsync())
            .ReturnsAsync((36.5m, (DateTime?)DateTime.UtcNow));

        var dialog = new Mock<IDialogService>();

        var vm = new InventoryViewModel(
            productService.Object,
            exchangeRateService.Object,
            userSession: null,
            dialog.Object,
            settingsService: settingsService);
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        await WaitUntilAsync(() => vm.Products.Count == 2, TimeSpan.FromSeconds(5));
        return (vm, dialog, productService);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "La condición esperada no se cumplió dentro del plazo.");
    }
}
