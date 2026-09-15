using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class PosSearchFailureSurfaceTests
{
    [Fact]
    public async Task ExecuteSearchAsync_WhenProductServiceFails_SurfacesConnectionErrorInsteadOfNotFound()
    {
        var (vm, mockProducts) = CreateViewModel();
        mockProducts.Setup(s => s.GetSuggestionsAsync("XYZ", true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        vm.SearchText = "XYZ";

        await WaitForAsync(() => vm.Suggestions.Count > 0);

        var item = Assert.Single(vm.Suggestions);
        Assert.Contains("Error", item.Name);
        Assert.NotEqual("Product not found", item.Name);
        mockProducts.Verify(s => s.GetSuggestionsAsync("XYZ", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteSearchAsync_WhenGenuinelyNotFound_ShowsNotFoundPlaceholder()
    {
        var (vm, mockProducts) = CreateViewModel();
        mockProducts.Setup(s => s.GetSuggestionsAsync("NOPE", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProductQuickInfoDto>());

        vm.SearchText = "NOPE";

        await WaitForAsync(() => vm.Suggestions.Count > 0);

        var item = Assert.Single(vm.Suggestions);
        Assert.Equal("Product not found", item.Name);
        Assert.Equal(-1, item.Id);
        mockProducts.Verify(s => s.GetSuggestionsAsync("NOPE", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveScannedCodeAsync_WhenGenuinelyNotFound_ReturnsNull()
    {
        var (vm, mockProducts) = CreateViewModel();
        mockProducts.Setup(s => s.GetQuickInfoAsync("12345670"))
            .ReturnsAsync((ProductQuickInfoDto?)null);

        var result = await vm.ResolveScannedCodeAsync("12345670");

        Assert.Null(result);
        mockProducts.Verify(s => s.GetQuickInfoAsync("12345670"), Times.Once);
    }

    [Fact]
    public async Task ResolveScannedCodeAsync_WhenInfrastructureFails_ReturnsDistinguishableErrorItem()
    {
        var (vm, mockProducts) = CreateViewModel();
        mockProducts.Setup(s => s.GetQuickInfoAsync("12345670"))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var result = await vm.ResolveScannedCodeAsync("12345670");

        Assert.NotNull(result);
        Assert.Equal(-2, result!.Id);
        Assert.Contains("Error", result.Name);
        Assert.NotEqual("Product not found", result.Name);
        mockProducts.Verify(s => s.GetQuickInfoAsync("12345670"), Times.Once);
    }

    [Fact]
    public async Task ResolveScannedCodeAsync_WhenCodeIsNotAValidBarcode_ReturnsNullWithoutServiceCall()
    {
        var (vm, mockProducts) = CreateViewModel();

        Assert.Null(await vm.ResolveScannedCodeAsync("http://host/scan?code=1"));
        Assert.Null(await vm.ResolveScannedCodeAsync("  "));

        mockProducts.Verify(s => s.GetQuickInfoAsync(It.IsAny<string>()), Times.Never);
    }

    private static (PosViewModel Vm, Mock<IProductService> Products) CreateViewModel()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        var vm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            new UserSession());
        return (vm, mockProducts);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(25);
        }
    }
}
