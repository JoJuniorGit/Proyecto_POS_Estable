using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T4 (F3, S3): dashboard global de cuentas por cobrar. Filtros de cartera, totales, alertas de
/// mora, estado de cuenta, abonos, anulación sin pagos, devolución Admin/Manager y comprobantes.
/// </summary>
public class CreditReceivablesDashboardTests
{
    private static UserSession Session(UserRole role = UserRole.Manager)
    {
        var session = new UserSession();
        session.SetUser(new UserDto { Id = 9, Name = "Supervisor", Role = role }, "token");
        return session;
    }

    private static CreditReceivableClientItemDto Row(
        int id = 1,
        int customerId = 7,
        decimal original = 100m,
        decimal balance = 40m,
        bool overdue = true,
        string status = "Parcial") => new()
    {
        ReceivableId = id,
        CustomerId = customerId,
        CustomerName = "Ana Pérez",
        CustomerCedulaOrRif = "V-12345678",
        SaleId = 10 + id,
        InvoiceNumber = 20 + id,
        OriginalUSD = original,
        BalanceUSD = balance,
        OriginalBsS = original * 36.5m,
        HistoricalRate = 36.5m,
        BalanceTodayBsS = balance * 40m,
        IssuedAtUtc = DateTime.UtcNow.AddDays(-40),
        DueDateUtc = DateTime.UtcNow.AddDays(-5),
        Status = status,
        IsOverdue = overdue
    };

    private static CreditReceivablesClientDto List(params CreditReceivableClientItemDto[] rows) => new()
    {
        Items = rows.ToList(),
        TotalOpenUSD = rows.Sum(r => r.BalanceUSD),
        TotalOverdueUSD = rows.Where(r => r.IsOverdue).Sum(r => r.BalanceUSD),
        OpenCount = rows.Length,
        TodayRate = 40m
    };

    private static (CreditReceivablesViewModel Vm, Mock<ISalesService> Sales, Mock<IPaymentService> Payments, Mock<IDialogService> Dialog) Create(
        UserRole role = UserRole.Manager)
    {
        var sales = new Mock<ISalesService>();
        var payments = new Mock<IPaymentService>();
        var dialog = new Mock<IDialogService>();
        var vm = new CreditReceivablesViewModel(sales.Object, payments.Object, dialog.Object, Session(role));
        return (vm, sales, payments, dialog);
    }

    [Fact]
    public async Task EnsureLoaded_MapsRowsTotalsAndOverdueAlert()
    {
        var (vm, sales, _, _) = Create();
        sales.Setup(s => s.GetCreditReceivablesAsync(It.IsAny<CreditReceivablesFilterClientDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(List(Row(), Row(id: 2, balance: 10m, overdue: false, status: "Vigente")));

        await vm.EnsureLoadedAsync();

        Assert.Equal(2, vm.Receivables.Count);
        Assert.Equal(50m, vm.TotalOpenUSD);
        Assert.Equal(40m, vm.TotalOverdueUSD);
        Assert.Equal(40m, vm.TodayRate);
        Assert.True(vm.HasOverdueAlert);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task ApplyFilters_PassesSearchStatusAndOverdueToService()
    {
        var (vm, sales, _, _) = Create();
        sales.Setup(s => s.GetCreditReceivablesAsync(It.IsAny<CreditReceivablesFilterClientDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(List());

        vm.SearchQuery = "Ana";
        vm.SelectedStatus = "Vencida";
        vm.OverdueOnly = true;
        await vm.ApplyFiltersCommand.ExecuteAsync(null);

        sales.Verify(s => s.GetCreditReceivablesAsync(
            It.Is<CreditReceivablesFilterClientDto>(f =>
                f.Search == "Ana" &&
                f.Status == "Vencida" &&
                f.OverdueOnly),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClearFilters_ResetsToAllAndReloads()
    {
        var (vm, sales, _, _) = Create();
        sales.Setup(s => s.GetCreditReceivablesAsync(It.IsAny<CreditReceivablesFilterClientDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(List());
        vm.SearchQuery = "Ana";
        vm.SelectedStatus = "Vencida";
        vm.OverdueOnly = true;

        await vm.ClearFiltersCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.SearchQuery);
        Assert.Equal("Todos", vm.SelectedStatus);
        Assert.False(vm.OverdueOnly);
        sales.Verify(s => s.GetCreditReceivablesAsync(
            It.Is<CreditReceivablesFilterClientDto>(f => f.Search == null && f.Status == null && !f.OverdueOnly),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ViewStatement_LoadsCustomerStatementAndOpensDialog()
    {
        var (vm, sales, _, dialog) = Create();
        sales.Setup(s => s.GetCreditReceivablesAsync(It.IsAny<CreditReceivablesFilterClientDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(List(Row()));
        sales.Setup(s => s.GetCreditStatementAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditStatementClientDto
            {
                Profile = new CreditProfileClientDto { CustomerId = 7, CustomerName = "Ana Pérez", CurrentDebtUSD = 40m },
                TodayRate = 40m,
                CurrentDebtTodayBsS = 1600m,
                Receivables = new List<CreditStatementReceivableClientDto>
                {
                    new() { ReceivableId = 1, BalanceUSD = 40m, BalanceTodayBsS = 1600m, Status = "Parcial" }
                }
            });
        CreditStatementDialogViewModel? captured = null;
        dialog.Setup(d => d.ShowModalAsync(It.IsAny<object>(), "RootDialog"))
            .Callback<object, string?>((content, _) => captured = Assert.IsType<CreditStatementDialogViewModel>(content))
            .ReturnsAsync((object?)null);

        await vm.EnsureLoadedAsync();
        await vm.ViewStatementCommand.ExecuteAsync(vm.Receivables.First());

        Assert.NotNull(captured);
        Assert.Equal("Ana Pérez", captured!.CustomerName);
        Assert.Equal(1600m, captured.Statement.CurrentDebtTodayBsS);
    }

    [Fact]
    public async Task RegisterPayment_OpensFifoDialogAndShowsReceiptOption()
    {
        var (vm, sales, payments, dialog) = Create();
        var row = Row();
        sales.Setup(s => s.GetCreditReceivablesAsync(It.Is<CreditReceivablesFilterClientDto>(f => f.CustomerId == null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(List(row));
        sales.Setup(s => s.GetCreditReceivablesAsync(It.Is<CreditReceivablesFilterClientDto>(f => f.CustomerId == 7), It.IsAny<CancellationToken>()))
            .ReturnsAsync(List(row));
        payments.Setup(p => p.GetActiveMethodsAsync()).ReturnsAsync(new List<PaymentMethodDto>
        {
            new() { Id = 1, Name = "Efectivo", IsCash = true, Currency = "USD" }
        });
        var paymentResult = new CreditPaymentResultClientDto
        {
            PaymentId = 55,
            CustomerId = 7,
            AmountUSD = 40m,
            AmountBsS = 1600m,
            PaymentRate = 40m
        };
        CreditPaymentDialogViewModel? captured = null;
        dialog.Setup(d => d.ShowModalAsync(It.IsAny<object>(), "RootDialog"))
            .Callback<object, string?>((content, _) => captured = Assert.IsType<CreditPaymentDialogViewModel>(content))
            .ReturnsAsync(paymentResult);
        dialog.Setup(d => d.ShowSuccessDialog(It.IsAny<string>(), "Imprimir Comprobante")).Returns(false);

        await vm.EnsureLoadedAsync();
        await vm.RegisterPaymentCommand.ExecuteAsync(vm.Receivables.First());

        Assert.NotNull(captured);
        Assert.False(captured!.IsExplicitAllocation);
        Assert.Equal(7, captured.CustomerId);
        Assert.True(captured.OpenReceivables.Count >= 1);
        Assert.Contains("55", vm.SuccessMessage);
        dialog.Verify(d => d.ShowSuccessDialog(It.IsAny<string>(), "Imprimir Comprobante"), Times.Once);
    }

    [Fact]
    public async Task Cancel_WhenReceivableHasPayments_IsBlockedWithoutServiceCall()
    {
        var (vm, sales, _, dialog) = Create();
        sales.Setup(s => s.GetCreditReceivablesAsync(It.IsAny<CreditReceivablesFilterClientDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(List(Row(balance: 40m, original: 100m)));

        await vm.EnsureLoadedAsync();
        await vm.CancelCommand.ExecuteAsync(vm.Receivables.First());

        dialog.Verify(d => d.ShowWarning("Anulación CxC", It.Is<string>(m => m.Contains("abonos"))), Times.Once);
        sales.Verify(s => s.CancelCreditSaleAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_WithoutPayments_ConfirmsAndReloads()
    {
        var (vm, sales, _, dialog) = Create();
        var row = Row(balance: 100m, original: 100m, overdue: false, status: "Vigente");
        sales.Setup(s => s.GetCreditReceivablesAsync(It.IsAny<CreditReceivablesFilterClientDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(List(row));
        dialog.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        sales.Setup(s => s.CancelCreditSaleAsync(11, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditCancelClientResultDto { SaleId = 11, ReceivableId = 1, CancelledOriginalUSD = 100m });

        await vm.EnsureLoadedAsync();
        await vm.CancelCommand.ExecuteAsync(vm.Receivables.First());

        sales.Verify(s => s.CancelCreditSaleAsync(11, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("anulada", vm.SuccessMessage);
        // EnsureLoaded inicial + recarga posterior a la anulación.
        sales.Verify(s => s.GetCreditReceivablesAsync(It.IsAny<CreditReceivablesFilterClientDto>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Return_WhenCashier_IsDeniedByRole()
    {
        var (vm, sales, _, dialog) = Create(UserRole.Cashier);
        sales.Setup(s => s.GetCreditReceivablesAsync(It.IsAny<CreditReceivablesFilterClientDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(List(Row()));

        await vm.EnsureLoadedAsync();
        Assert.False(vm.CanManageCreditReturns);
        await vm.ReturnCommand.ExecuteAsync(vm.Receivables.First());

        dialog.Verify(d => d.ShowWarning("Devolución CxC", It.Is<string>(m => m.Contains("Administrador"))), Times.Once);
        sales.Verify(s => s.GetSaleAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Return_WhenManager_OpensReturnDialogAndReportsCajaResult()
    {
        var (vm, sales, _, dialog) = Create(UserRole.Manager);
        var row = Row();
        sales.Setup(s => s.GetCreditReceivablesAsync(It.IsAny<CreditReceivablesFilterClientDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(List(row));
        sales.Setup(s => s.GetSaleAsync(11)).ReturnsAsync(new Core.DTOs.SaleDto
        {
            Id = 11,
            Items = new List<Core.DTOs.SaleItemDto>
            {
                new() { Id = 4, ProductId = 1, ProductName = "Producto", Quantity = 2m, UnitPrice = 50m }
            }
        });
        CreditReturnDialogViewModel? captured = null;
        dialog.Setup(d => d.ShowModalAsync(It.IsAny<object>(), "RootDialog"))
            .Callback<object, string?>((content, _) => captured = Assert.IsType<CreditReturnDialogViewModel>(content))
            .ReturnsAsync(new CreditReturnClientResultDto
            {
                ReturnId = 2,
                SaleId = 11,
                ReceivableId = 1,
                ReturnedUSD = 10m,
                RefundedUSD = 5m,
                BalanceAfterUSD = 30m
            });

        await vm.EnsureLoadedAsync();
        await vm.ReturnCommand.ExecuteAsync(vm.Receivables.First());

        Assert.NotNull(captured);
        Assert.Single(captured!.Lines);
        Assert.Contains("devolución", vm.SuccessMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("caja", vm.SuccessMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrintPaymentReceipt_DownloadsAndOpensPdf()
    {
        var (vm, sales, _, _) = Create();
        sales.Setup(s => s.GetCreditPaymentReceiptAsync(55, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 9, 8, 7 });
        byte[]? captured = null;
        string? fileName = null;
        vm.ReceiptOpener = (bytes, name) =>
        {
            captured = bytes;
            fileName = name;
            return Task.CompletedTask;
        };

        await vm.PrintPaymentReceiptCommand.ExecuteAsync(55);

        Assert.Equal(new byte[] { 9, 8, 7 }, captured);
        Assert.Contains("55", fileName);
    }
}
