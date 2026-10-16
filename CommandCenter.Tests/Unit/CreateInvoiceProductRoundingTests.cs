using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Common;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;
using ClientSupplierInvoiceService = Desktop.Client.Services.ISupplierInvoiceService;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.184-T4 (S7): selector de regla a nivel resolución en el modal "Resolver producto desde
/// factura" (Heredar de la factura / Sin redondeo / Psicológico USD), inicializado desde la
/// política USD global (las reglas VES —que operan sobre Bs.S— se mapean a Sin redondeo) y
/// propagado en el LinkInvoiceProductRequestDto en AMBOS flujos: resolución primaria
/// (CreateProductAsync) y re-vínculo "Actualizar" (UpdateLinkedProductAsync). null = hereda la
/// regla de la factura al confirmar.
/// </summary>
public class CreateInvoiceProductRoundingTests
{
    // --------------------------------------------------------------------------------------------
    // Selector del modal: opciones, default desde la global y selección del usuario.
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void RoundingOptions_OfferInheritExactMathAndPsychological()
    {
        var vm = CreateDialog();

        Assert.Equal(
            new RoundingRuleType?[] { null, RoundingRuleType.ExactMath, RoundingRuleType.UsdPsychological },
            vm.RoundingOptions.Select(option => option.Rule).ToArray());
    }

    [Theory]
    [InlineData(RoundingRuleType.UsdPsychological, RoundingRuleType.UsdPsychological)]
    [InlineData(RoundingRuleType.ExactMath, RoundingRuleType.ExactMath)]
    [InlineData(RoundingRuleType.VesCashFriendlyInteger, RoundingRuleType.ExactMath)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep5, RoundingRuleType.ExactMath)]
    [InlineData(RoundingRuleType.VesCashFriendlyStep10, RoundingRuleType.ExactMath)]
    public void Constructor_InitializesSelectorFromGlobalUsdRule(RoundingRuleType globalRule, RoundingRuleType expected)
    {
        var vm = CreateDialog(globalRule);

        Assert.Equal(expected, vm.SelectedRoundingRule);
        Assert.Equal(expected, vm.SelectedRoundingRuleOption?.Rule);
    }

    [Fact]
    public void Constructor_DefaultsToExactMathWhenGlobalRuleIsNotProvided()
    {
        var vm = CreateDialog();

        Assert.Equal(RoundingRuleType.ExactMath, vm.SelectedRoundingRule);
    }

    [Fact]
    public void InheritOption_IsAvailableAndKeepsNullRule()
    {
        var vm = CreateDialog(RoundingRuleType.UsdPsychological);

        vm.SelectedRoundingRuleOption = vm.RoundingOptions.Single(option => option.Rule is null);

        Assert.Null(vm.SelectedRoundingRule);
    }

    // --------------------------------------------------------------------------------------------
    // Flujo de resolución primaria: el selector viaja en el LinkInvoiceProductRequestDto.
    // --------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateProductCommand_LinkMode_SendsUserSelectedRoundingRuleOnRequest()
    {
        var (service, capturedLink, staged, catalogProduct) = ArrangeResolutionFlow();
        var dialog = CreateDialogService(vm =>
        {
            vm.SelectedProduct = catalogProduct;
            vm.SelectedRoundingRuleOption = vm.RoundingOptions.Single(option => option.Rule == RoundingRuleType.UsdPsychological);
            vm.SaveCommand.Execute(null);
        });
        var viewModel = CreateViewModel(service.Object, dialog.Object, CreateSettings(RoundingRuleType.ExactMath));
        await viewModel.PricingPoliciesLoadTask;
        viewModel.LoadStagedInvoice(staged);

        await viewModel.CreateProductCommand.ExecuteAsync(viewModel.Lines[0]);

        Assert.Equal(RoundingRuleType.UsdPsychological, capturedLink.Value?.RoundingRule);
    }

    [Fact]
    public async Task CreateProductCommand_LinkMode_WithInheritSelection_SendsNullRule()
    {
        var (service, capturedLink, staged, catalogProduct) = ArrangeResolutionFlow();
        var dialog = CreateDialogService(vm =>
        {
            vm.SelectedProduct = catalogProduct;
            vm.SelectedRoundingRuleOption = vm.RoundingOptions.Single(option => option.Rule is null);
            vm.SaveCommand.Execute(null);
        });
        var viewModel = CreateViewModel(service.Object, dialog.Object, CreateSettings(RoundingRuleType.UsdPsychological));
        await viewModel.PricingPoliciesLoadTask;
        viewModel.LoadStagedInvoice(staged);

        await viewModel.CreateProductCommand.ExecuteAsync(viewModel.Lines[0]);

        Assert.NotNull(capturedLink.Value);
        Assert.Null(capturedLink.Value!.RoundingRule);
    }

    [Fact]
    public async Task CreateProductCommand_LinkMode_WithoutTouchingSelector_SendsGlobalRule()
    {
        var (service, capturedLink, staged, catalogProduct) = ArrangeResolutionFlow();
        var dialog = CreateDialogService(vm =>
        {
            Assert.Equal(RoundingRuleType.UsdPsychological, vm.SelectedRoundingRule);
            vm.SelectedProduct = catalogProduct;
            vm.SaveCommand.Execute(null);
        });
        var viewModel = CreateViewModel(service.Object, dialog.Object, CreateSettings(RoundingRuleType.UsdPsychological));
        await viewModel.PricingPoliciesLoadTask;
        viewModel.LoadStagedInvoice(staged);

        await viewModel.CreateProductCommand.ExecuteAsync(viewModel.Lines[0]);

        Assert.Equal(RoundingRuleType.UsdPsychological, capturedLink.Value?.RoundingRule);
    }

    [Fact]
    public async Task CreateProductCommand_LinkMode_MapsVesGlobalRuleToExactMath()
    {
        var (service, capturedLink, staged, catalogProduct) = ArrangeResolutionFlow();
        var dialog = CreateDialogService(vm =>
        {
            Assert.Equal(RoundingRuleType.ExactMath, vm.SelectedRoundingRule);
            vm.SelectedProduct = catalogProduct;
            vm.SaveCommand.Execute(null);
        });
        var viewModel = CreateViewModel(service.Object, dialog.Object, CreateSettings(RoundingRuleType.VesCashFriendlyStep5));
        await viewModel.PricingPoliciesLoadTask;
        viewModel.LoadStagedInvoice(staged);

        await viewModel.CreateProductCommand.ExecuteAsync(viewModel.Lines[0]);

        Assert.Equal(RoundingRuleType.ExactMath, capturedLink.Value?.RoundingRule);
    }

    // --------------------------------------------------------------------------------------------
    // Flujo "Actualizar" (re-vínculo): el selector también viaja.
    // --------------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateLinkedProductCommand_SendsUserSelectedRoundingRuleOnRequest()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var catalogProduct = CreateCatalogProduct();
        var staged = CreateInvoice(CreateLine(1, resolvedProductId: catalogProduct.Id, status: "Update"));
        var refreshed = CreateInvoice(CreateLine(1, resolvedProductId: catalogProduct.Id, status: "Update"));

        var capturedLink = new LinkCapture();
        service
            .Setup(client => client.GetCatalogProductsByIdsAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { catalogProduct });
        service
            .Setup(client => client.LinkProductToLineAsync(staged.Id, 1, It.IsAny<LinkInvoiceProductRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback<int, int, LinkInvoiceProductRequestDto, CancellationToken>((_, _, request, _) => capturedLink.Value = request)
            .ReturnsAsync(refreshed);

        var dialog = CreateDialogService(vm =>
        {
            Assert.True(vm.IsUpdateMode);
            vm.SelectedRoundingRuleOption = vm.RoundingOptions.Single(option => option.Rule == RoundingRuleType.UsdPsychological);
            vm.SaveCommand.Execute(null);
        });
        var viewModel = CreateViewModel(service.Object, dialog.Object, CreateSettings(RoundingRuleType.ExactMath));
        await viewModel.PricingPoliciesLoadTask;
        viewModel.LoadStagedInvoice(staged);

        var row = Assert.Single(viewModel.Lines);
        Assert.True(viewModel.UpdateLinkedProductCommand.CanExecute(row));

        await viewModel.UpdateLinkedProductCommand.ExecuteAsync(row);

        Assert.NotNull(capturedLink.Value);
        Assert.Equal(RoundingRuleType.UsdPsychological, capturedLink.Value!.RoundingRule);
    }

    // --------------------------------------------------------------------------------------------
    // Andamiaje.
    // --------------------------------------------------------------------------------------------

    private sealed class LinkCapture
    {
        public LinkInvoiceProductRequestDto? Value { get; set; }
    }

    private static (Mock<ClientSupplierInvoiceService> Service, LinkCapture CapturedLink, SupplierInvoiceDetailDto Staged, ProductDto CatalogProduct) ArrangeResolutionFlow()
    {
        var captured = new LinkCapture();
        var catalogProduct = CreateCatalogProduct();
        var staged = CreateInvoice(CreateLine(1, resolvedProductId: null, status: "New"));
        var refreshed = CreateInvoice(CreateLine(1, resolvedProductId: catalogProduct.Id, status: "Update"));

        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        service
            .Setup(client => client.SearchCatalogProductsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { catalogProduct });
        service
            .Setup(client => client.LinkProductToLineAsync(staged.Id, 1, It.IsAny<LinkInvoiceProductRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback<int, int, LinkInvoiceProductRequestDto, CancellationToken>((_, _, request, _) => captured.Value = request)
            .ReturnsAsync(refreshed);

        return (service, captured, staged, catalogProduct);
    }

    private static Mock<IDialogService> CreateDialogService(Action<CreateInvoiceProductDialogViewModel> onShown)
    {
        var dialog = new Mock<IDialogService>();
        dialog
            .Setup(d => d.ShowCreateInvoiceProductDialog(It.IsAny<CreateInvoiceProductDialogViewModel>()))
            .Callback(onShown)
            .Returns(true);
        return dialog;
    }

    private static CreateInvoiceProductDialogViewModel CreateDialog(RoundingRuleType globalUsdRoundingRule = RoundingRuleType.ExactMath)
        => new(new SupplierInvoiceLineViewModel(CreateLine(1)), isBsSCurrency: false, globalUsdRoundingRule: globalUsdRoundingRule);

    private static Mock<ISettingsService> CreateSettings(RoundingRuleType usdRule)
    {
        var settings = new Mock<ISettingsService>();
        settings
            .Setup(service => service.GetPricingPoliciesAsync())
            .ReturnsAsync(new PricingPoliciesDto { UsdRoundingRule = usdRule });
        return settings;
    }

    private static SupplierInvoiceViewModel CreateViewModel(
        ClientSupplierInvoiceService invoiceService,
        IDialogService dialogService,
        Mock<ISettingsService>? settingsService = null)
    {
        var userSession = new UserSession();
        userSession.SetUser(new UserDto { Id = 1, Name = "Manager", Role = UserRole.Manager, Cedula = "V-1", IsActive = true });
        var exchangeRates = new Mock<IExchangeRateService>();
        exchangeRates.Setup(service => service.GetCurrentRateAsync()).ReturnsAsync((36.5m, (DateTime?)null));
        return new SupplierInvoiceViewModel(
            invoiceService,
            userSession,
            dialogService,
            Mock.Of<IFilePickerDialog>(),
            exchangeRates.Object,
            settingsService?.Object);
    }

    private static SupplierInvoiceDetailDto CreateInvoice(params SupplierInvoiceLineDto[] lines) =>
        new(42, 7, "Draft", CurrencyCodes.Usd, 1m, lines);

    private static SupplierInvoiceLineDto CreateLine(
        int id,
        decimal unitCost = 10m,
        string status = "Update",
        int? resolvedProductId = null) => new(
            id,
            "SUP-1",
            "12345",
            "Coffee",
            2m,
            unitCost,
            unitCost,
            status,
            resolvedProductId,
            8m,
            20m,
            10m,
            5m,
            20m,
            10m,
            12m,
            11m,
            true,
            "Barcode",
            null,
            null,
            null);

    private static ProductDto CreateCatalogProduct() => new()
    {
        Id = 45,
        SKU = "7591234000011",
        Name = "Harina P.A.N. 1kg",
        CostPriceUSD = 8.20m,
        ProfitMarginRetail = 30m,
        ProfitMarginWholesale = 15m,
        StockQuantity = 24m
    };
}
