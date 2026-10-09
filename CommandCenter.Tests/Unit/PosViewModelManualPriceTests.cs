using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.151 (W4, R8/design D7): disparador WPF de precio manual para productos normales. El precio
/// capturado (o derivado) llega al mismo AddItemAsync y una respuesta del gate enruta al flujo de
/// espera T10; cancelar no muta el POS y la rama de adelanto de efectivo queda intacta.
/// </summary>
public class PosViewModelManualPriceTests
{
    private static (PosViewModel Vm, Mock<ISalesService> Sales, Mock<IDialogService> Dialog) CreateViewModel()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.Setup(r => r.CurrentRate).Returns(50m);
        var mockDialog = new Mock<IDialogService>();

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        cartVm.CurrentSale = new SaleDto { Id = 1, Items = new List<SaleItemDto>() };

        var vm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            new UserSession(),
            mockDialog.Object);

        // Aisla del bus global compartido entre pruebas (patron de PosViewModelAuthorizationTests).
        WeakReferenceMessenger.Default.UnregisterAll(vm);
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);
        return (vm, mockSales, mockDialog);
    }

    private static ProductQuickInfoDto NormalProduct() => new()
    {
        Id = 42,
        Name = "Harina PAN",
        SKU = "12345670",
        IsActive = true
    };

    private static ProductQuickInfoDto CashAdvanceProduct() => new()
    {
        Id = 42,
        Name = "Harina PAN",
        SKU = "12345670",
        IsCashAdvance = true,
        ProfitPercentage = 0m,
        IsActive = true
    };

    private static SaleDto UpdatedSale() => new()
    {
        Id = 1,
        Items = new List<SaleItemDto> { new() { Id = 7, ProductId = 42, Quantity = 1m } }
    };

    private static ManualPriceDialogResult DerivedManualPrice(decimal rate = 50m)
    {
        var priceVm = new ManualPriceDialogViewModel(rate) { UnitPriceUsdText = "10" };
        priceVm.AcceptCommand.Execute(null);
        Assert.NotNull(priceVm.Result);
        return priceVm.Result!;
    }

    [Fact]
    public async Task ManualPriceAdd_WithDerivedValues_ReachesAddItemAsyncWithEnteredAndDerivedPrices()
    {
        var (vm, sales, dialog) = CreateViewModel();
        vm.SelectedSuggestion = NormalProduct();
        dialog.Setup(d => d.ShowManualPriceDialog(50m)).Returns(DerivedManualPrice());
        sales.Setup(s => s.AddItemAsync(1, 42, 1m, 50m, 10m, 500m)).ReturnsAsync(UpdatedSale());

        await vm.AddManualPriceSuggestionCommand.ExecuteAsync(null);

        sales.Verify(s => s.AddItemAsync(1, 42, 1m, 50m, 10m, 500m), Times.Once);
        Assert.Single(vm.Cart.CurrentSale!.Items);
        dialog.Verify(
            d => d.ShowAuthorizationWaitAsync(It.IsAny<AuthorizationRequestContext>(), It.IsAny<Func<string, Task>>()),
            Times.Never);
        dialog.Verify(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ManualPriceAdd_WhenAuthorizationRequired_StartsWaitFlowAndRetriesWithToken()
    {
        var (vm, sales, dialog) = CreateViewModel();
        vm.SelectedSuggestion = NormalProduct();
        dialog.Setup(d => d.ShowManualPriceDialog(50m)).Returns(DerivedManualPrice());
        sales.Setup(s => s.AddItemAsync(1, 42, 1m, 50m, 10m, 500m))
            .ThrowsAsync(new AuthorizationRequiredException(
                "Se requiere autorización remota de un administrador.", "ManualPriceOverride"));
        sales.Setup(s => s.AddItemAsync(1, 42, 1m, 50m, 10m, 500m, "token-1")).ReturnsAsync(UpdatedSale());

        AuthorizationRequestContext? captured = null;
        dialog.Setup(d => d.ShowAuthorizationWaitAsync(It.IsAny<AuthorizationRequestContext>(), It.IsAny<Func<string, Task>>()))
            .Returns<AuthorizationRequestContext, Func<string, Task>>(async (context, retry) =>
            {
                captured = context;
                await retry("token-1");
                return new AuthorizationWaitResult(AuthorizationWaitOutcome.Granted);
            });

        await vm.AddManualPriceSuggestionCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal(1, captured!.SaleId);
        Assert.Equal(42, captured.ProductId);
        Assert.Equal(10m, captured.CustomUnitPriceUsd);
        Assert.Equal(500m, captured.CustomUnitPriceLocal);

        sales.Verify(s => s.AddItemAsync(1, 42, 1m, 50m, 10m, 500m, "token-1"), Times.Once);
        Assert.Single(vm.Cart.CurrentSale!.Items);
        dialog.Verify(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ManualPriceAdd_WhenDialogCancelled_KeepsPosStateUntouched()
    {
        var (vm, sales, dialog) = CreateViewModel();
        var product = NormalProduct();
        vm.SelectedSuggestion = product;
        vm.SearchText = "Harina";
        dialog.Setup(d => d.ShowManualPriceDialog(50m)).Returns((ManualPriceDialogResult?)null);

        await vm.AddManualPriceSuggestionCommand.ExecuteAsync(null);

        sales.Verify(
            s => s.AddItemAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal?>(), It.IsAny<decimal?>()),
            Times.Never);
        Assert.Same(product, vm.SelectedSuggestion);
        Assert.Equal("Harina", vm.SearchText);
        Assert.Empty(vm.Cart.CurrentSale!.Items);
        Assert.False(vm.IsProcessing);
    }

    [Fact]
    public async Task ManualPriceAdd_WithCashAdvanceSelected_NeverOpensManualPriceDialog()
    {
        var (vm, sales, dialog) = CreateViewModel();
        vm.SelectedSuggestion = CashAdvanceProduct();

        await vm.AddManualPriceSuggestionCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowManualPriceDialog(It.IsAny<decimal>()), Times.Never);
        sales.Verify(
            s => s.AddItemAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal?>(), It.IsAny<decimal?>()),
            Times.Never);
    }

    [Fact]
    public async Task ManualPriceAdd_WithGroupHeaderSelected_NeverOpensManualPriceDialog()
    {
        var (vm, sales, dialog) = CreateViewModel();
        vm.SelectedSuggestion = new ProductQuickInfoDto { Id = 42, Name = "Refrescos", IsGroupHeader = true, IsActive = true };

        await vm.AddManualPriceSuggestionCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowManualPriceDialog(It.IsAny<decimal>()), Times.Never);
        sales.Verify(
            s => s.AddItemAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal?>(), It.IsAny<decimal?>()),
            Times.Never);
    }

    [Fact]
    public void CanAddManualPrice_OnlyForSelectableNormalSuggestion()
    {
        var (vm, _, _) = CreateViewModel();

        vm.SelectedSuggestion = null;
        Assert.False(vm.AddManualPriceSuggestionCommand.CanExecute(null));

        vm.SelectedSuggestion = new ProductQuickInfoDto { Id = -1, Name = "Product not found" };
        Assert.False(vm.AddManualPriceSuggestionCommand.CanExecute(null));

        vm.SelectedSuggestion = new ProductQuickInfoDto { Id = 42, Name = "Refrescos", IsGroupHeader = true };
        Assert.False(vm.AddManualPriceSuggestionCommand.CanExecute(null));

        vm.SelectedSuggestion = CashAdvanceProduct();
        Assert.False(vm.AddManualPriceSuggestionCommand.CanExecute(null));

        vm.SelectedSuggestion = NormalProduct();
        Assert.True(vm.AddManualPriceSuggestionCommand.CanExecute(null));
    }

    [Fact]
    public async Task CashAdvanceAdd_StillUsesCashAdvanceDialogAndComputedPrices()
    {
        var (vm, sales, dialog) = CreateViewModel();
        dialog.Setup(d => d.ShowCashAdvanceDialog()).Returns(500m);
        sales.Setup(s => s.AddItemAsync(1, 42, 1m, 50m, 10m, 500m)).ReturnsAsync(UpdatedSale());

        await vm.AddSelectedSuggestionCommand.ExecuteAsync(CashAdvanceProduct());

        sales.Verify(s => s.AddItemAsync(1, 42, 1m, 50m, 10m, 500m), Times.Once);
        dialog.Verify(d => d.ShowManualPriceDialog(It.IsAny<decimal>()), Times.Never);
        Assert.Single(vm.Cart.CurrentSale!.Items);
    }
}
