using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests;

public class InventoryPaginationTests
{
    private readonly Mock<IProductService> _productServiceMock = new();
    private readonly Mock<IExchangeRateService> _exchangeRateServiceMock = new();

    private InventoryViewModel CreateViewModel()
    {
        _exchangeRateServiceMock.Setup(s => s.CurrentRate).Returns(36.5m);
        _exchangeRateServiceMock.Setup(s => s.GetCurrentRateAsync()).ReturnsAsync((36.5m, (DateTime?)DateTime.UtcNow));
        return new InventoryViewModel(_productServiceMock.Object, _exchangeRateServiceMock.Object);
    }

    [Fact]
    public void PageNumbers_EmptyWhenTotalPagesZero()
    {
        var vm = CreateViewModel();
        vm.TotalCount = 0;
        vm.TotalPages = 0;
        vm.CurrentPage = 1;

        vm.UpdatePageNumbers();

        Assert.Empty(vm.PageNumbers);
        Assert.Equal("Página 0 de 0 (0 productos)", vm.PageSummary);
        Assert.Equal("0", vm.TargetPageInput);
        Assert.False(vm.CanGoFirst);
        Assert.False(vm.CanGoPrevious);
        Assert.False(vm.CanGoNext);
        Assert.False(vm.CanGoLast);
    }

    [Fact]
    public void PageNumbers_SinglePageWhenTotalPagesOne()
    {
        var vm = CreateViewModel();
        vm.TotalCount = 12;
        vm.TotalPages = 1;
        vm.CurrentPage = 1;

        vm.UpdatePageNumbers();

        Assert.Single(vm.PageNumbers);
        Assert.Equal(1, vm.PageNumbers[0].PageNumber);
        Assert.True(vm.PageNumbers[0].IsActive);
        Assert.Equal("1", vm.TargetPageInput);
        Assert.False(vm.CanGoFirst);
        Assert.False(vm.CanGoPrevious);
        Assert.False(vm.CanGoNext);
        Assert.False(vm.CanGoLast);
    }

    [Fact]
    public void PageNumbers_GeneratesCorrectWindow_ForVariousCurrentPages()
    {
        var vm = CreateViewModel();
        vm.TotalCount = 2500;
        vm.TotalPages = 100;

        // CurrentPage = 1 -> Window [1, 2, 3]
        vm.CurrentPage = 1;
        vm.UpdatePageNumbers();
        Assert.Equal(new[] { 1, 2, 3 }, vm.PageNumbers.Select(p => p.PageNumber));
        Assert.True(vm.PageNumbers.First(p => p.PageNumber == 1).IsActive);
        Assert.False(vm.CanGoFirst);
        Assert.False(vm.CanGoPrevious);
        Assert.True(vm.CanGoNext);
        Assert.True(vm.CanGoLast);

        // CurrentPage = 2 -> Window [1, 2, 3, 4]
        vm.CurrentPage = 2;
        vm.UpdatePageNumbers();
        Assert.Equal(new[] { 1, 2, 3, 4 }, vm.PageNumbers.Select(p => p.PageNumber));
        Assert.True(vm.PageNumbers.First(p => p.PageNumber == 2).IsActive);
        Assert.True(vm.CanGoFirst);
        Assert.True(vm.CanGoPrevious);
        Assert.True(vm.CanGoNext);
        Assert.True(vm.CanGoLast);

        // CurrentPage = 50 -> Window [48, 49, 50, 51, 52] (exact 5 buttons)
        vm.CurrentPage = 50;
        vm.UpdatePageNumbers();
        Assert.Equal(new[] { 48, 49, 50, 51, 52 }, vm.PageNumbers.Select(p => p.PageNumber));
        Assert.True(vm.PageNumbers.First(p => p.PageNumber == 50).IsActive);
        Assert.Equal("50", vm.TargetPageInput);
        Assert.True(vm.CanGoFirst);
        Assert.True(vm.CanGoPrevious);
        Assert.True(vm.CanGoNext);
        Assert.True(vm.CanGoLast);

        // CurrentPage = 100 -> Window [98, 99, 100]
        vm.CurrentPage = 100;
        vm.UpdatePageNumbers();
        Assert.Equal(new[] { 98, 99, 100 }, vm.PageNumbers.Select(p => p.PageNumber));
        Assert.True(vm.PageNumbers.First(p => p.PageNumber == 100).IsActive);
        Assert.True(vm.CanGoFirst);
        Assert.True(vm.CanGoPrevious);
        Assert.False(vm.CanGoNext);
        Assert.False(vm.CanGoLast);
    }

    [Fact]
    public async Task SubmitGoToPage_ClampsValuesAndSyncsInput()
    {
        var vm = CreateViewModel();
        _productServiceMock.Setup(s => s.GetPagedAsync(
            It.IsAny<string?>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResultDto<ProductDto>
            {
                TotalCount = 250,
                Items = new List<ProductDto>()
            });

        vm.TotalCount = 250;
        vm.TotalPages = 10;
        vm.CurrentPage = 3;
        vm.UpdatePageNumbers();

        // 1. Invalid non-numeric input should restore CurrentPage string
        vm.TargetPageInput = "invalid_text";
        await vm.SubmitGoToPageCommand.ExecuteAsync(null);
        Assert.Equal("3", vm.TargetPageInput);

        // 2. Value out of bounds below (e.g. -5) should navigate to 1
        vm.TargetPageInput = "-5";
        await vm.SubmitGoToPageCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal("1", vm.TargetPageInput);

        // 3. Value out of bounds above (e.g. 999) should navigate to TotalPages (10)
        vm.TargetPageInput = "999";
        await vm.SubmitGoToPageCommand.ExecuteAsync(null);
        Assert.Equal(10, vm.CurrentPage);
        Assert.Equal("10", vm.TargetPageInput);
    }

    [Fact]
    public async Task FirstLastPreviousNext_Commands_NavigateToExpectedPages()
    {
        var vm = CreateViewModel();
        _productServiceMock.Setup(s => s.GetPagedAsync(
            It.IsAny<string?>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResultDto<ProductDto>
            {
                TotalCount = 250,
                Items = new List<ProductDto>()
            });

        vm.TotalCount = 250;
        vm.TotalPages = 10;
        vm.CurrentPage = 5;
        vm.UpdatePageNumbers();

        // Previous -> Page 4
        await vm.PreviousPageCommand.ExecuteAsync(null);
        Assert.Equal(4, vm.CurrentPage);

        // Next -> Page 5
        await vm.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(5, vm.CurrentPage);

        // First -> Page 1
        await vm.FirstPageCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.CurrentPage);

        // Last -> Page 10
        await vm.LastPageCommand.ExecuteAsync(null);
        Assert.Equal(10, vm.CurrentPage);
    }
}
