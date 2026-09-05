using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class InventoryCatalogRefreshTests
{
    private readonly Mock<IProductService> _productServiceMock = new();
    private readonly Mock<IExchangeRateService> _exchangeRateServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();

    private InventoryViewModel CreateViewModel()
    {
        _exchangeRateServiceMock.Setup(s => s.CurrentRate).Returns(50.00m);
        _exchangeRateServiceMock.Setup(s => s.GetCurrentRateAsync())
                                .ReturnsAsync((50.00m, DateTime.UtcNow));

        return new InventoryViewModel(
            _productServiceMock.Object,
            _exchangeRateServiceMock.Object,
            userSession: null,
            dialog_service: _dialogServiceMock.Object);
    }

    [Fact]
    public async Task RefreshCommand_WhenInvoked_FetchesExchangeRateAndReloadsProducts()
    {
        // Arrange
        var pagedResult = new PagedResultDto<ProductDto>
        {
            Items = new List<ProductDto>
            {
                new ProductDto { Id = 1, Name = "Arroz Mary", SKU = "ARR-001", PriceUSD = 1.20m, PriceBsS = 60.00m, IsActive = true },
                new ProductDto { Id = 2, Name = "Harina PAN", SKU = "HAR-002", PriceUSD = 1.10m, PriceBsS = 55.00m, IsActive = true }
            },
            TotalCount = 2,
            HasMore = false
        };

        _productServiceMock.Setup(p => p.GetPagedAsync(
            It.IsAny<string?>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagedResult);

        var vm = CreateViewModel();

        // Act
        await vm.RefreshCommand.ExecuteAsync(null);

        // Assert
        _exchangeRateServiceMock.Verify(s => s.GetCurrentRateAsync(), Times.AtLeastOnce);
        _productServiceMock.Verify(p => p.GetPagedAsync(
            null,
            1,
            25,
            "active",
            "name",
            false,
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        Assert.Equal(2, vm.Products.Count);
        Assert.False(vm.IsRefreshing);
    }

    [Fact]
    public async Task RefreshCommand_PreservesCurrentPageAndSearchText()
    {
        // Arrange
        var pagedResult = new PagedResultDto<ProductDto>
        {
            Items = new List<ProductDto>
            {
                new ProductDto { Id = 50, Name = "Cafe Fama de America", SKU = "CAF-050", PriceUSD = 2.50m, PriceBsS = 125.00m, IsActive = true }
            },
            TotalCount = 50,
            HasMore = false
        };

        _productServiceMock.Setup(p => p.GetPagedAsync(
            "Cafe",
            It.IsAny<int>(),
            25,
            "active",
            "name",
            false,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagedResult);

        var vm = CreateViewModel();

        // Set SearchText and wait for debounce search to settle
        typeof(InventoryViewModel)
            .GetProperty(nameof(InventoryViewModel.SearchText))?
            .SetValue(vm, "Cafe");
        await Task.Delay(150);
        while (vm.IsSearching)
        {
            await Task.Delay(20);
        }

        // Explicitly set CurrentPage to 2 (e.g. user navigated to page 2 of search results)
        vm.CurrentPage = 2;
        _productServiceMock.Invocations.Clear();

        // Act
        await vm.RefreshCommand.ExecuteAsync(null);

        // Assert
        _productServiceMock.Verify(p => p.GetPagedAsync(
            "Cafe",
            2,
            25,
            "active",
            "name",
            false,
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.False(vm.IsRefreshing);
    }

    [Fact]
    public async Task RefreshCommand_HandlesExceptionsGracefully_AndResetsIsRefreshing()
    {
        // Arrange
        _productServiceMock.Setup(p => p.GetPagedAsync(
            It.IsAny<string?>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("Connection to Backend timed out"));

        var vm = CreateViewModel();
        _dialogServiceMock.Invocations.Clear();

        // Act - should not crash or propagate unhandled exception
        await vm.RefreshCommand.ExecuteAsync(null);

        // Assert
        Assert.False(vm.IsRefreshing);
        _dialogServiceMock.Verify(d => d.ShowError(
            It.Is<string>(s => s.Contains("Error")),
            It.Is<string>(s => s.Contains("Connection to Backend timed out"))), Times.AtLeastOnce);
    }
}
