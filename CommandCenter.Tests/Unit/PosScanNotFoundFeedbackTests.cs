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

// 8.143: el escaneo por wedge del POS debe dar feedback explícito cuando el código no
// corresponde a ningún producto (antes era un no-op silencioso).
public class PosScanNotFoundFeedbackTests
{
    private static (PosViewModel Vm, Mock<IProductService> Products, Mock<IDialogService> Dialog, Mock<ISalesService> Sales) CreateViewModel()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.Setup(r => r.CurrentRate).Returns(50m);
        var mockDialog = new Mock<IDialogService>();

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        // Venta ya iniciada: evita el lazy-start por red (el mock no lo resolvería).
        cartVm.CurrentSale = new SaleDto { Id = 1, Items = new List<SaleItemDto>() };

        var vm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            new UserSession(),
            mockDialog.Object);

        // Aisla del bus global compartido entre pruebas (patrón de PosSearchFailureSurfaceTests).
        WeakReferenceMessenger.Default.UnregisterAll(vm);
        return (vm, mockProducts, mockDialog, mockSales);
    }

    [Fact]
    public async Task AddProductByCodeAsync_WhenCodeNotFound_ShowsProductNotFoundWarning()
    {
        var (vm, mockProducts, mockDialog, _) = CreateViewModel();
        mockProducts.Setup(p => p.GetSuggestionsAsync("12345670", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProductQuickInfoDto>());

        await vm.AddProductByCodeAsync("12345670");

        mockDialog.Verify(
            d => d.ShowWarning("Producto no encontrado", It.Is<string>(m => m.Contains("12345670"))),
            Times.Once);
    }

    [Fact]
    public async Task AddProductByCodeAsync_WhenCodeFound_AddsItemWithoutWarning()
    {
        var (vm, mockProducts, mockDialog, mockSales) = CreateViewModel();
        var product = new ProductQuickInfoDto
        {
            Id = 42,
            SKU = "12345670",
            Name = "Harina PAN",
            PriceUSD = 10m,
            PriceBsS = 500m,
            IsActive = true
        };
        mockProducts.Setup(p => p.GetSuggestionsAsync("12345670", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProductQuickInfoDto> { product });
        mockSales.Setup(s => s.AddItemAsync(1, 42, 1m, 50m, null, null))
            .ReturnsAsync(new SaleDto
            {
                Id = 1,
                Items = new List<SaleItemDto> { new SaleItemDto { Id = 7, ProductId = 42, Quantity = 1m } }
            });

        await vm.AddProductByCodeAsync("12345670");

        mockSales.Verify(s => s.AddItemAsync(1, 42, 1m, 50m, null, null), Times.Once);
        Assert.Single(vm.RecentScannedProducts);
        mockDialog.Verify(d => d.ShowWarning(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
