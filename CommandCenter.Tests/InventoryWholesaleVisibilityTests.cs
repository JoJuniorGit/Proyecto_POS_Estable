using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests;

public class InventoryWholesaleVisibilityTests
{
    [Fact]
    public void InventoryViewModel_InitialWholesaleState_IsHiddenByDefault()
    {
        // Arrange
        var mockProductService = new Mock<IProductService>();
        var mockRateService = new Mock<IExchangeRateService>();
        var loggedOutSession = new UserSession(); // IsLoggedIn = false

        // Act
        var vm = new InventoryViewModel(mockProductService.Object, mockRateService.Object, loggedOutSession);
        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        // Assert
        Assert.False(vm.ShowWholesale, "ShowWholesale debe ser false por defecto al inicializar el catálogo.");
        Assert.Equal("Mostrar Precios al Mayor", vm.WholesaleButtonText);
    }

    [Fact]
    public void ToggleWholesaleCommand_TogglesStateAndUpdatesButtonText()
    {
        // Arrange
        var mockProductService = new Mock<IProductService>();
        var mockRateService = new Mock<IExchangeRateService>();
        var loggedOutSession = new UserSession();
        var vm = new InventoryViewModel(mockProductService.Object, mockRateService.Object, loggedOutSession);
        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        // Act 1: Toggle on
        vm.ToggleWholesaleCommand.Execute(null);

        // Assert 1
        Assert.True(vm.ShowWholesale, "ShowWholesale debe ser true tras el primer toggle.");
        Assert.Equal("Ocultar Precios al Mayor", vm.WholesaleButtonText);

        // Act 2: Toggle off
        vm.ToggleWholesaleCommand.Execute(null);

        // Assert 2
        Assert.False(vm.ShowWholesale, "ShowWholesale debe volver a ser false tras el segundo toggle.");
        Assert.Equal("Mostrar Precios al Mayor", vm.WholesaleButtonText);
    }

    [Fact]
    public void SelectedCurrency_UpdatesPriceHeadersReactively()
    {
        // Arrange
        var mockProductService = new Mock<IProductService>();
        var mockRateService = new Mock<IExchangeRateService>();
        var loggedOutSession = new UserSession();
        var vm = new InventoryViewModel(mockProductService.Object, mockRateService.Object, loggedOutSession);
        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        // Assert default headers use the profile local code
        var localCode = CurrencyDisplay.LocalCurrencyCode;
        var primaryCode = CurrencyDisplay.PrimaryCurrencyCode;
        Assert.Equal(localCode, vm.SelectedCurrency);
        Assert.Equal($"Precio Detal ({localCode})", vm.RetailPriceHeader);
        Assert.Equal($"Precio Mayor ({localCode})", vm.WholesalePriceHeader);

        // Act: switch to the profile primary currency
        vm.SelectedCurrency = primaryCode;

        // Assert updated headers in the primary code
        Assert.Equal(primaryCode, vm.SelectedCurrency);
        Assert.Equal($"Precio Detal ({primaryCode})", vm.RetailPriceHeader);
        Assert.Equal($"Precio Mayor ({primaryCode})", vm.WholesalePriceHeader);
    }
}
