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

    [Fact]
    public void HideTestTransactions_WhenInitialized_DefaultsToFalse()
    {
        var vm = CreateViewModel();

        Assert.False(vm.HideTestTransactions);
        Assert.Equal(string.Empty, vm.CashierFilterText);
        Assert.Empty(vm.Cashiers);
    }

    [Fact]
    public async Task LoadHistoryCommand_WhenHideTestTransactions_MasksBotStressTransactions()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 1, FinalPaidAmountBsS = 100m, CashierName = "BOT_STRESS_TEST" },
            new() { Id = 2, InvoiceNumber = 2, FinalPaidAmountBsS = 250m, CashierName = "Ana Pérez" },
            new() { Id = 3, InvoiceNumber = 3, FinalPaidAmountBsS = 150m, CashierName = "BOT_STRESS_TEST" }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 3));

        var vm = CreateViewModel();

        vm.HideTestTransactions = true;
        await vm.LoadHistoryCommand.ExecuteAsync(null);

        Assert.Single(vm.Sales);
        Assert.Equal("Ana Pérez", vm.Sales.Single().CashierName);
        Assert.Equal(2, vm.HiddenByFilterCount);
        Assert.Equal(250m, vm.TotalBsSForThePeriod);
        Assert.Contains("2", vm.HiddenByFilterSummary);
    }

    [Fact]
    public void HideTestTransactions_WhenToggledAfterLoad_FiltersWithoutRefetching()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 1, FinalPaidAmountBsS = 100m, CashierName = "BOT_STRESS_TEST" },
            new() { Id = 2, InvoiceNumber = 2, FinalPaidAmountBsS = 250m, CashierName = "Ana Pérez" }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 2));

        var vm = CreateViewModel();
        vm.LoadHistoryCommand.Execute(null);

        Assert.Equal(2, vm.Sales.Count);
        Assert.Equal("Todas las ventas de la página se muestran", vm.HiddenByFilterSummary);

        vm.HideTestTransactions = true;

        Assert.Single(vm.Sales);
        Assert.Equal(250m, vm.TotalBsSForThePeriod);
        _salesServiceMock.Verify(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        vm.HideTestTransactions = false;

        Assert.Equal(2, vm.Sales.Count);
        Assert.Equal(350m, vm.TotalBsSForThePeriod);
    }

    [Fact]
    public async Task CashierFilterText_WhenTypingSubstring_FiltersCaseInsensitiveWithoutRefetching()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 1, FinalPaidAmountBsS = 100m, CashierName = "ANA PÉREZ" },
            new() { Id = 2, InvoiceNumber = 2, FinalPaidAmountBsS = 250m, CashierName = "Carlos Díaz" },
            new() { Id = 3, InvoiceNumber = 3, FinalPaidAmountBsS = 150m, CashierName = "ana" }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 3));

        var vm = CreateViewModel();
        await vm.LoadHistoryCommand.ExecuteAsync(null);

        vm.CashierFilterText = "ana";

        Assert.Equal(2, vm.Sales.Count);
        Assert.All(vm.Sales, s => Assert.Contains("ana", s.CashierName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, vm.HiddenByFilterCount);
        _salesServiceMock.Verify(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        vm.CashierFilterText = "carl";

        Assert.Single(vm.Sales);
        Assert.Equal("Carlos Díaz", vm.Sales.Single().CashierName);

        vm.CashierFilterText = "";

        Assert.Equal(3, vm.Sales.Count);
    }

    [Fact]
    public async Task LoadHistoryCommand_WhenPageFullyHiddenAndMorePages_AdvancesToNextVisiblePage()
    {
        var hiddenPage = Enumerable.Range(1, 25)
            .Select(i => new SaleHistoryDto { Id = i, InvoiceNumber = i, FinalPaidAmountBsS = 10m, CashierName = "BOT_STRESS_TEST" })
            .ToList();
        var visiblePage = new List<SaleHistoryDto>
        {
            new() { Id = 100, InvoiceNumber = 100, FinalPaidAmountBsS = 500m, CashierName = "Ana Pérez" },
            new() { Id = 101, InvoiceNumber = 101, FinalPaidAmountBsS = 300m, CashierName = "Carlos Díaz" }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(1, 25, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((hiddenPage, 100));
        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(2, 25, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((visiblePage, 100));

        var vm = CreateViewModel();
        vm.HideTestTransactions = true;

        await vm.LoadHistoryCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal(2, vm.Sales.Count);
        Assert.Equal(800m, vm.TotalBsSForThePeriod);
        _salesServiceMock.Verify(s => s.GetSalesHistoryAsync(2, 25, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoadHistoryCommand_WhenPageVisible_DoesNotAdvancePage()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 1, FinalPaidAmountBsS = 100m, CashierName = "BOT_STRESS_TEST" },
            new() { Id = 2, InvoiceNumber = 2, FinalPaidAmountBsS = 250m, CashierName = "Ana Pérez" }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 100));

        var vm = CreateViewModel();
        vm.HideTestTransactions = true;

        await vm.LoadHistoryCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.CurrentPage);
        Assert.Single(vm.Sales);
        _salesServiceMock.Verify(s => s.GetSalesHistoryAsync(1, 25, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoadHistoryCommand_WhenLoadingPages_AccumulatesDistinctCashierNames()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 1, CashierName = "Ana Pérez" },
            new() { Id = 2, InvoiceNumber = 2, CashierName = "BOT_STRESS_TEST" }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 2));

        var vm = CreateViewModel();
        await vm.LoadHistoryCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Cashiers.Count);
        Assert.Contains("Ana Pérez", vm.Cashiers);
        Assert.Contains("BOT_STRESS_TEST", vm.Cashiers);

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(1, 25, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<SaleHistoryDto>
            {
                new() { Id = 3, InvoiceNumber = 3, CashierName = "Carlos Díaz" },
                new() { Id = 4, InvoiceNumber = 4, CashierName = "BOT_STRESS_TEST" }
            }, 2));

        await vm.LoadHistoryCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Cashiers.Count);
        Assert.Contains("Carlos Díaz", vm.Cashiers);
    }

    [Fact]
    public async Task ApplySecondaryFilters_WhenDraftDiffersFromActive_AppliesAndClosesFlyout()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 1, FinalPaidAmountBsS = 100m, CashierName = "BOT_STRESS_TEST" },
            new() { Id = 2, InvoiceNumber = 2, FinalPaidAmountBsS = 250m, CashierName = "Ana Pérez" }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 2));

        var vm = CreateViewModel();
        await vm.LoadHistoryCommand.ExecuteAsync(null);

        vm.OpenFilterFlyoutCommand.Execute(null);
        Assert.True(vm.IsFilterFlyoutOpen);
        Assert.Equal(2, vm.Sales.Count);

        vm.DraftHideTestTransactions = true;

        vm.ApplySecondaryFiltersCommand.Execute(null);

        Assert.False(vm.IsFilterFlyoutOpen);
        Assert.Single(vm.Sales);
        Assert.Equal("Ana Pérez", vm.Sales.Single().CashierName);
        Assert.True(vm.HasActiveSecondaryFilters);
        _salesServiceMock.Verify(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplySecondaryFilters_WhenDraftEqualsActive_KeepsStateAndClosesFlyout()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 1, FinalPaidAmountBsS = 100m, CashierName = "Ana Pérez" }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 1));

        var vm = CreateViewModel();
        await vm.LoadHistoryCommand.ExecuteAsync(null);
        vm.CashierFilterText = "ana";

        vm.OpenFilterFlyoutCommand.Execute(null);
        Assert.Equal("ana", vm.DraftCashierFilterText);
        Assert.True(vm.IsFilterFlyoutOpen);

        vm.ApplySecondaryFiltersCommand.Execute(null);

        Assert.False(vm.IsFilterFlyoutOpen);
        Assert.Equal("ana", vm.CashierFilterText);
        Assert.Single(vm.Sales);
        _salesServiceMock.Verify(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DraftCashierFilterText_WhenChanged_DoesNotApplyFilterWithoutAccept()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 1, FinalPaidAmountBsS = 100m, CashierName = "BOT_STRESS_TEST" },
            new() { Id = 2, InvoiceNumber = 2, FinalPaidAmountBsS = 250m, CashierName = "Ana Pérez" }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 2));

        var vm = CreateViewModel();
        await vm.LoadHistoryCommand.ExecuteAsync(null);

        vm.OpenFilterFlyoutCommand.Execute(null);
        vm.DraftCashierFilterText = "ana";
        vm.DraftHideTestTransactions = true;

        Assert.False(vm.HasActiveSecondaryFilters);
        Assert.Equal(2, vm.Sales.Count);
        Assert.Equal("Todas las ventas de la página se muestran", vm.HiddenByFilterSummary);
        _salesServiceMock.Verify(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        vm.CloseFilterFlyoutCommand.Execute(null);
        Assert.False(vm.IsFilterFlyoutOpen);
    }

    [Fact]
    public async Task OpenFilterFlyout_WhenActiveHasValues_SyncsDraftsFromActive()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 1, FinalPaidAmountBsS = 100m, CashierName = "Ana Pérez" }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 1));

        var vm = CreateViewModel();
        await vm.LoadHistoryCommand.ExecuteAsync(null);
        vm.CashierFilterText = "ana";
        vm.HideTestTransactions = true;

        vm.OpenFilterFlyoutCommand.Execute(null);

        Assert.Equal("ana", vm.DraftCashierFilterText);
        Assert.True(vm.DraftHideTestTransactions);
        Assert.True(vm.IsFilterFlyoutOpen);
    }

    [Fact]
    public async Task ClearSecondaryFilterDrafts_ResetsDraftsWithoutApplying()
    {
        var sampleSales = new List<SaleHistoryDto>
        {
            new() { Id = 1, InvoiceNumber = 1, FinalPaidAmountBsS = 100m, CashierName = "Ana Pérez" }
        };

        _salesServiceMock
            .Setup(s => s.GetSalesHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((sampleSales, 1));

        var vm = CreateViewModel();
        await vm.LoadHistoryCommand.ExecuteAsync(null);
        vm.CashierFilterText = "ana";

        vm.OpenFilterFlyoutCommand.Execute(null);
        vm.DraftCashierFilterText = "carlos";
        vm.DraftHideTestTransactions = true;

        vm.ClearSecondaryFilterDraftsCommand.Execute(null);

        Assert.Equal(string.Empty, vm.DraftCashierFilterText);
        Assert.False(vm.DraftHideTestTransactions);
        Assert.True(vm.IsFilterFlyoutOpen);
        Assert.Equal("ana", vm.CashierFilterText);
        Assert.Single(vm.Sales);
    }

    [Fact]
    public void HasActiveSecondaryFilters_ReflectsActiveFilterState()
    {
        var vm = CreateViewModel();

        Assert.False(vm.HasActiveSecondaryFilters);

        vm.HideTestTransactions = true;
        Assert.True(vm.HasActiveSecondaryFilters);
        vm.HideTestTransactions = false;
        Assert.False(vm.HasActiveSecondaryFilters);

        vm.CashierFilterText = "  ana  ";
        Assert.True(vm.HasActiveSecondaryFilters);
        vm.CashierFilterText = "   ";
        Assert.False(vm.HasActiveSecondaryFilters);
    }
}
