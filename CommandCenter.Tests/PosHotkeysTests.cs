using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests;

public class PosHotkeysTests
{
    [Fact]
    public void CancelOrClearCommand_ClearsSearchTextAndSuggestions()
    {
        // Arrange
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        var loggedOutSession = new UserSession(); // IsLoggedIn = false

        var vm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            loggedOutSession);

        vm.SearchText = "Harina PAN";
        vm.Suggestions.Add(new ProductQuickInfoDto { Id = 1, Name = "Harina PAN", SKU = "123" });
        vm.HasSuggestions = true;

        // Act
        vm.CancelOrClearCommand.Execute(null);

        // Assert
        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Empty(vm.Suggestions);
        Assert.False(vm.HasSuggestions);
    }

    [Fact]
    public async Task TogglePriceListCommand_TogglesBetweenRetailAndWholesale()
    {
        // Arrange
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        var loggedOutSession = new UserSession();

        var initialSale = new SaleDto
        {
            Id = 101,
            PriceListType = "Retail",
            Status = "Pending",
            Items = new List<SaleItemDto>()
        };

        var wholesaleSale = new SaleDto
        {
            Id = 101,
            PriceListType = "Wholesale",
            Status = "Pending",
            Items = new List<SaleItemDto>()
        };

        mockSales.Setup(s => s.UpdatePriceListAsync(101, "Wholesale")).ReturnsAsync(wholesaleSale);

        cartVm.CurrentSale = initialSale;

        var vm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            loggedOutSession);

        // Act
        await vm.TogglePriceListCommand.ExecuteAsync(null);

        // Assert
        mockSales.Verify(s => s.UpdatePriceListAsync(101, "Wholesale"), Times.Once);
        Assert.Equal("Wholesale", cartVm.PriceListType);
    }

    [Fact]
    public async Task SyncExchangeRateCommand_InvokesExchangeRateService()
    {
        // Arrange
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.Setup(r => r.SyncBcvAsync()).ReturnsAsync((36.5m, DateTime.UtcNow));
        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        var loggedOutSession = new UserSession();

        var vm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            loggedOutSession);

        // Act
        await vm.SyncExchangeRateCommand.ExecuteAsync(null);

        // Assert
        mockRate.Verify(r => r.SyncBcvAsync(), Times.Once);
    }
}
