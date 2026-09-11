using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class SalesHistoryViewModelTests
{
    private readonly Mock<ISalesService> _salesServiceMock = new();

    private SalesHistoryViewModel CreateViewModel()
    {
        return new SalesHistoryViewModel(_salesServiceMock.Object, action => action());
    }

    [Fact]
    public void PaginationProperties_WhenInitialized_ReturnDefaults()
    {
        var vm = CreateViewModel();

        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal(25, vm.PageSize);
        Assert.Equal(0, vm.TotalItems);
        Assert.Equal(1, vm.TotalPages);
        Assert.Equal("Página 1 de 1", vm.PaginationPageText);
        Assert.Equal("Mostrando 0 registros", vm.PaginationSummaryText);
        Assert.False(vm.CanGoToPreviousPage);
        Assert.False(vm.CanGoToNextPage);
    }

    [Fact]
    public void PaginationSummaryText_WhenMultiplePages_CalculatesRangeCorrectly()
    {
        var vm = CreateViewModel();
        vm.PageSize = 10;
        vm.TotalItems = 25;
        vm.CurrentPage = 2;

        Assert.Equal(3, vm.TotalPages);
        Assert.Equal("Página 2 de 3", vm.PaginationPageText);
        Assert.Equal("Mostrando 11-20 de 25", vm.PaginationSummaryText);
        Assert.True(vm.CanGoToPreviousPage);
        Assert.True(vm.CanGoToNextPage);
    }

    [Fact]
    public void PaginationSummaryText_WhenOnLastPartialPage_CapsUpperBoundToTotalItems()
    {
        var vm = CreateViewModel();
        vm.PageSize = 10;
        vm.TotalItems = 25;
        vm.CurrentPage = 3;

        Assert.Equal("Página 3 de 3", vm.PaginationPageText);
        Assert.Equal("Mostrando 21-25 de 25", vm.PaginationSummaryText);
        Assert.True(vm.CanGoToPreviousPage);
        Assert.False(vm.CanGoToNextPage);
    }

    [Fact]
    public async Task NextPageCommand_WhenHasMoreItems_IncrementsPageAndReloads()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 101, FinalPaidAmountBsS = 500m, TotalBsS = 500m }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 50));

        var vm = CreateViewModel();
        await vm.LoadHistoryCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal(50, vm.TotalItems);
        Assert.True(vm.CanGoToNextPage);

        await vm.NextPageCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.CurrentPage);
        _salesServiceMock.Verify(s => s.GetSalesHistoryAsync(2, 25, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PreviousPageCommand_WhenOnPageTwo_DecrementsPageAndReloads()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 101, FinalPaidAmountBsS = 500m, TotalBsS = 500m }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 50));

        var vm = CreateViewModel();
        await vm.LoadHistoryCommand.ExecuteAsync(null);
        await vm.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.CurrentPage);

        await vm.PreviousPageCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.CurrentPage);
        _salesServiceMock.Verify(s => s.GetSalesHistoryAsync(1, 25, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task LoadHistoryCommand_WhenInvoked_CalculatesTotalBsSForThePeriod()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 1, FinalPaidAmountBsS = 150.50m, TotalBsS = 150.50m, TotalUSD = 2.50m },
            new() { Id = 2, InvoiceNumber = 2, FinalPaidAmountBsS = 250.00m, TotalBsS = 250.00m, TotalUSD = 4.00m }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(1, 25, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 2));

        var vm = CreateViewModel();

        await vm.LoadHistoryCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Sales.Count);
        Assert.Equal(400.50m, vm.TotalBsSForThePeriod);
        Assert.Equal(2, vm.TotalItems);
        Assert.Null(vm.SelectedSale);
    }

    [Fact]
    public void SelectedSale_WhenAssigned_PreloadsDetailHeadersImmediately()
    {
        var vm = CreateViewModel();
        var sale = new SaleHistoryDto
        {
            Id = 42,
            InvoiceNumber = 1042,
            Date = new DateTime(2026, 9, 11, 14, 30, 0),
            AppliedRate = 60.50m,
            TotalUSD = 10.00m,
            TotalBsS = 605.00m
        };

        vm.SelectedSale = sale;

        Assert.True(vm.IsPurchaseDetailsVisible);
        Assert.True(vm.IsPurchaseDetailsExpanded);
        Assert.Equal(60.50m, vm.DetailAppliedRate);
        Assert.Equal(10.00m, vm.DetailTotalUSD);
        Assert.Equal(605.00m, vm.DetailTotalBsS);
    }

    [Fact]
    public async Task FirstPageCommand_WhenOnPageThree_NavigatesToPageOne()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 101, FinalPaidAmountBsS = 500m, TotalBsS = 500m }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 100));

        var vm = CreateViewModel();
        await vm.LoadHistoryCommand.ExecuteAsync(null);
        vm.CurrentPage = 3;

        Assert.True(vm.CanGoFirst);
        await vm.FirstPageCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.CurrentPage);
        _salesServiceMock.Verify(s => s.GetSalesHistoryAsync(1, 25, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task LastPageCommand_WhenOnPageOne_NavigatesToLastPage()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 101, FinalPaidAmountBsS = 500m, TotalBsS = 500m }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 100));

        var vm = CreateViewModel();
        await vm.LoadHistoryCommand.ExecuteAsync(null);

        Assert.True(vm.CanGoLast);
        await vm.LastPageCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.CurrentPage);
        _salesServiceMock.Verify(s => s.GetSalesHistoryAsync(4, 25, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GoToPageCommand_WhenValidPage_NavigatesToSpecifiedPage()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 101, FinalPaidAmountBsS = 500m, TotalBsS = 500m }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 100));

        var vm = CreateViewModel();
        await vm.LoadHistoryCommand.ExecuteAsync(null);

        await vm.GoToPageCommand.ExecuteAsync(3);

        Assert.Equal(3, vm.CurrentPage);
        _salesServiceMock.Verify(s => s.GetSalesHistoryAsync(3, 25, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GoToPageCommand_WhenSameOrInvalidPage_DoesNotReload()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 101, FinalPaidAmountBsS = 500m, TotalBsS = 500m }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 100));

        var vm = CreateViewModel();
        await vm.LoadHistoryCommand.ExecuteAsync(null);

        await vm.GoToPageCommand.ExecuteAsync(1);
        await vm.GoToPageCommand.ExecuteAsync(99);

        Assert.Equal(1, vm.CurrentPage);
        _salesServiceMock.Verify(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubmitGoToPageCommand_WhenValidInput_NavigatesToClampedPage()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 101, FinalPaidAmountBsS = 500m, TotalBsS = 500m }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 100));

        var vm = CreateViewModel();
        await vm.LoadHistoryCommand.ExecuteAsync(null);

        vm.TargetPageInput = "3";
        await vm.SubmitGoToPageCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.CurrentPage);
        _salesServiceMock.Verify(s => s.GetSalesHistoryAsync(3, 25, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubmitGoToPageCommand_WhenInvalidInput_RestoresCurrentPageInput()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 101, FinalPaidAmountBsS = 500m, TotalBsS = 500m }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 100));

        var vm = CreateViewModel();
        await vm.LoadHistoryCommand.ExecuteAsync(null);

        vm.TargetPageInput = "abc";
        await vm.SubmitGoToPageCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal("1", vm.TargetPageInput);
    }

    [Fact]
    public void UpdatePageNumbers_WhenMultiplePages_GeneratesSurroundingPagesAndHighlightsActive()
    {
        var vm = CreateViewModel();
        vm.PageSize = 10;
        vm.TotalItems = 100;
        vm.CurrentPage = 5;

        vm.UpdatePageNumbers();

        Assert.Equal(5, vm.PageNumbers.Count);
        Assert.Equal(new[] { 3, 4, 5, 6, 7 }, vm.PageNumbers.Select(p => p.PageNumber));
        Assert.True(vm.PageNumbers.First(p => p.PageNumber == 5).IsActive);
        Assert.False(vm.PageNumbers.First(p => p.PageNumber == 4).IsActive);
        Assert.Equal("Pág. 5 de 10 (100 ventas)", vm.PageSummary);
        Assert.Equal("5", vm.TargetPageInput);
    }

    [Fact]
    public void UpdatePageNumbers_WhenEmptyList_SetsDefaults()
    {
        var vm = CreateViewModel();
        vm.TotalItems = 0;

        vm.UpdatePageNumbers();

        Assert.Empty(vm.PageNumbers);
        Assert.Equal(0, vm.CurrentPage);
        Assert.Equal("Página 0 de 0 (0 ventas)", vm.PageSummary);
        Assert.Equal("0", vm.TargetPageInput);
        Assert.False(vm.CanGoFirst);
        Assert.False(vm.CanGoLast);
    }
}
