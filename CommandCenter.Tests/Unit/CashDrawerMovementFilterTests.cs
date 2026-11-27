using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class CashDrawerMovementFilterTests
{
    private static CashTransactionDto Transaction(int id, CashTransactionType type, CashTransactionSource source, string description, int? invoice = null)
        => new()
        {
            Id = id,
            Type = type,
            Source = source,
            Description = description,
            InvoiceNumber = invoice,
            TransactionTimeLocal = new DateTime(2026, 11, 9, 10, 0, 0).AddMinutes(-id),
            AmountLocal = 100m,
            AmountUsd = 2m,
            ExchangeRate = 50m
        };

    private static async Task<CashDrawerViewModel> LoadViewModelAsync(params CashTransactionDto[] history)
    {
        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer.Setup(service => service.GetActiveSessionAsync()).ReturnsAsync((CashDrawerSessionDto?)null);
        cashDrawer.Setup(service => service.GetHistoryAsync(It.IsAny<int>()))
            .ReturnsAsync(history.ToList());

        var rate = new Mock<IExchangeRateService>();
        rate.SetupGet(service => service.CurrentRate).Returns(50m);

        var viewModel = new CashDrawerViewModel(cashDrawer.Object, rate.Object);
        WeakReferenceMessenger.Default.UnregisterAll(viewModel);
        await viewModel.LoadSessionAsync();
        return viewModel;
    }

    [Fact]
    public async Task SearchText_FiltersByInvoiceNumberAndDetail()
    {
        using var vm = await LoadViewModelAsync(
            Transaction(1, CashTransactionType.Income, CashTransactionSource.SalePayment, "Venta de mostrador", invoice: 1001),
            Transaction(2, CashTransactionType.Expense, CashTransactionSource.CashOut, "Retiro de caja"),
            Transaction(3, CashTransactionType.Income, CashTransactionSource.CashIn, "Ingreso manual"));

        vm.SearchText = "1001";

        var invoiceMatch = Assert.Single(vm.OrderedTransactions);
        Assert.Equal(1001, invoiceMatch.InvoiceNumber);
        Assert.Equal(1, vm.TotalPhysicalTransactions);

        vm.SearchText = "retiro";

        var detailMatch = Assert.Single(vm.OrderedTransactions);
        Assert.Equal("Retiro de caja", detailMatch.Description);
        Assert.Equal(1, vm.TotalPages);
        Assert.False(vm.CanGoNext);

        vm.SearchText = string.Empty;

        Assert.Equal(3, vm.OrderedTransactions.Count);
        Assert.Equal(3, vm.TotalPhysicalTransactions);
    }

    [Fact]
    public async Task SearchText_IsCaseInsensitiveAndTrimsWhitespace()
    {
        using var vm = await LoadViewModelAsync(
            Transaction(1, CashTransactionType.Income, CashTransactionSource.SalePayment, "Venta de mostrador"),
            Transaction(2, CashTransactionType.Expense, CashTransactionSource.CashOut, "Retiro de caja"));

        vm.SearchText = "  RETIRO  ";

        var match = Assert.Single(vm.OrderedTransactions);
        Assert.Equal("Retiro de caja", match.Description);
    }

    [Fact]
    public async Task MovementTypeFilter_SplitsIncomesAndExpenses()
    {
        using var vm = await LoadViewModelAsync(
            Transaction(1, CashTransactionType.Income, CashTransactionSource.SalePayment, "Venta"),
            Transaction(2, CashTransactionType.Expense, CashTransactionSource.CashOut, "Retiro"),
            Transaction(3, CashTransactionType.Income, CashTransactionSource.CashIn, "Ingreso"),
            Transaction(4, CashTransactionType.Expense, CashTransactionSource.CashAdvance, "Adelanto"));

        vm.MovementTypeFilter = CashMovementTypeFilter.Income;

        Assert.All(vm.OrderedTransactions, transaction => Assert.Equal(CashTransactionType.Income, transaction.Type));
        Assert.Equal(2, vm.TotalPhysicalTransactions);
        Assert.True(vm.IsFilterIncome);

        vm.MovementTypeFilter = CashMovementTypeFilter.Expense;

        Assert.All(vm.OrderedTransactions, transaction => Assert.Equal(CashTransactionType.Expense, transaction.Type));
        Assert.Equal(2, vm.TotalPhysicalTransactions);
        Assert.True(vm.IsFilterExpense);

        vm.MovementTypeFilter = CashMovementTypeFilter.All;

        Assert.Equal(4, vm.OrderedTransactions.Count);
        Assert.True(vm.IsFilterAll);
    }

    [Fact]
    public async Task SearchAndTypeFilter_Compose()
    {
        using var vm = await LoadViewModelAsync(
            Transaction(1, CashTransactionType.Income, CashTransactionSource.SalePayment, "Venta factura", invoice: 2001),
            Transaction(2, CashTransactionType.Expense, CashTransactionSource.CashOut, "Retiro factura"),
            Transaction(3, CashTransactionType.Income, CashTransactionSource.CashIn, "Ingreso manual"));

        vm.SearchText = "factura";
        vm.MovementTypeFilter = CashMovementTypeFilter.Income;

        var match = Assert.Single(vm.OrderedTransactions);
        Assert.Equal(2001, match.InvoiceNumber);
        Assert.Equal(1, vm.TotalPhysicalTransactions);
    }

    [Fact]
    public async Task ChangingFilter_ResetsPaginationToFirstPage()
    {
        var history = Enumerable.Range(1, 30)
            .Select(id => Transaction(id, CashTransactionType.Income, CashTransactionSource.SalePayment, $"Venta {id}"))
            .ToArray();
        history[0].InvoiceNumber = 9999;

        using var vm = await LoadViewModelAsync(history);

        Assert.Equal(2, vm.TotalPages);
        vm.NextPageCommand.Execute(null);
        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal(5, vm.OrderedTransactions.Count);

        vm.SearchText = "9999";

        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal(1, vm.TotalPages);
        var match = Assert.Single(vm.OrderedTransactions);
        Assert.Equal(9999, match.InvoiceNumber);
    }

    [Fact]
    public async Task FilterWithoutMatches_ReportsFilteredEmptySummary()
    {
        using var vm = await LoadViewModelAsync(
            Transaction(1, CashTransactionType.Income, CashTransactionSource.SalePayment, "Venta"));

        vm.SearchText = "sin coincidencias";

        Assert.Empty(vm.OrderedTransactions);
        Assert.Equal(0, vm.TotalPhysicalTransactions);
        Assert.Equal("No hay movimientos para el filtro aplicado", vm.PaginationSummary);
    }

    [Fact]
    public async Task SetMovementTypeFilterCommand_SelectsChipFilter()
    {
        using var vm = await LoadViewModelAsync(
            Transaction(1, CashTransactionType.Income, CashTransactionSource.SalePayment, "Venta"),
            Transaction(2, CashTransactionType.Expense, CashTransactionSource.CashOut, "Retiro"));

        vm.SetMovementTypeFilterCommand.Execute(CashMovementTypeFilter.Expense);

        Assert.Equal(CashMovementTypeFilter.Expense, vm.MovementTypeFilter);
        Assert.True(vm.IsFilterExpense);
        var match = Assert.Single(vm.OrderedTransactions);
        Assert.Equal("Retiro", match.Description);
    }

    [Fact]
    public async Task MovementsGridMinHeight_ReservesEightRowsAndAddsTwoAndHalfRowsOnOverflow()
    {
        var eight = Enumerable.Range(1, 8)
            .Select(id => Transaction(id, CashTransactionType.Income, CashTransactionSource.SalePayment, $"Venta {id}"))
            .ToArray();
        var nine = Enumerable.Range(1, 9)
            .Select(id => Transaction(id, CashTransactionType.Income, CashTransactionSource.SalePayment, $"Venta {id}"))
            .ToArray();

        using var eightRows = await LoadViewModelAsync(eight);
        using var nineRows = await LoadViewModelAsync(nine);

        Assert.Equal(48d, eightRows.MovementGridRowHeight);
        Assert.Equal(44d, eightRows.MovementGridColumnHeaderHeight);
        Assert.Equal(428d, eightRows.MovementsGridMinHeight);
        Assert.Equal(548d, nineRows.MovementsGridMinHeight);
    }

    [Fact]
    public async Task MovementsGridMinHeight_FollowsPaginationBackToEightRows()
    {
        var history = Enumerable.Range(1, 30)
            .Select(id => Transaction(id, CashTransactionType.Income, CashTransactionSource.SalePayment, $"Venta {id}"))
            .ToArray();

        using var vm = await LoadViewModelAsync(history);

        Assert.Equal(25, vm.OrderedTransactions.Count);
        Assert.Equal(548d, vm.MovementsGridMinHeight);

        vm.NextPageCommand.Execute(null);

        Assert.Equal(5, vm.OrderedTransactions.Count);
        Assert.Equal(428d, vm.MovementsGridMinHeight);
    }

    [Fact]
    public async Task RecentIncomesPanel_StartsCollapsed()
    {
        using var vm = await LoadViewModelAsync();

        Assert.False(vm.IsRecentIncomesExpanded);

        vm.ToggleRecentIncomesCommand.Execute(null);

        Assert.True(vm.IsRecentIncomesExpanded);
    }
}
