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

/// <summary>
/// T4 (F3, S5/S7): devolución de venta a crédito (Admin/Manager) y estado de cuenta dual del
/// cliente. La devolución reduce primero el saldo y la caja solo paga el excedente efectivo.
/// </summary>
public class CreditReceivablesActionsTests
{
    private static CreditReceivableClientItemDto Receivable() => new()
    {
        ReceivableId = 3,
        CustomerId = 7,
        CustomerName = "Ana Pérez",
        SaleId = 11,
        InvoiceNumber = 21,
        OriginalUSD = 100m,
        BalanceUSD = 40m,
        BalanceTodayBsS = 1600m,
        Status = "Parcial"
    };

    private static SaleDto Sale() => new()
    {
        Id = 11,
        Items = new List<SaleItemDto>
        {
            new() { Id = 4, ProductId = 1, ProductName = "Producto A", Quantity = 2m, UnitPrice = 50m, Subtotal = 100m },
            new() { Id = 5, ProductId = 2, ProductName = "Producto B", Quantity = 1m, UnitPrice = 20m, Subtotal = 20m }
        }
    };

    [Fact]
    public async Task ReturnDialog_Confirm_BuildsLinesAndClosesWithResult()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        var vm = new CreditReturnDialogViewModel(sales.Object, dialog.Object, Receivable(), Sale());
        CreditReturnRequestClientDto? request = null;
        sales.Setup(s => s.RegisterCreditReturnAsync(11, It.IsAny<CreditReturnRequestClientDto>(), It.IsAny<CancellationToken>()))
            .Callback<int, CreditReturnRequestClientDto, CancellationToken>((_, r, _) => request = r)
            .ReturnsAsync(new CreditReturnClientResultDto
            {
                ReturnId = 2,
                SaleId = 11,
                ReceivableId = 3,
                ReturnedUSD = 50m,
                RefundedUSD = 10m,
                BalanceAfterUSD = 0m
            });

        vm.Lines.First(l => l.SaleItemId == 4).QuantityText = "1";
        vm.Reason = "Producto dañado";
        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.NotNull(request);
        Assert.Single(request!.Items);
        Assert.Equal(4, request.Items[0].SaleItemId);
        Assert.Equal(1m, request.Items[0].Quantity);
        Assert.Equal("Producto dañado", request.Reason);
        Assert.False(string.IsNullOrWhiteSpace(request.IdempotencyKey));
        Assert.NotNull(vm.Result);
        dialog.Verify(d => d.CloseCurrentModal(vm.Result!), Times.Once);
    }

    [Fact]
    public async Task ReturnDialog_WithoutLines_ShowsValidationAndDoesNotCallService()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        var vm = new CreditReturnDialogViewModel(sales.Object, dialog.Object, Receivable(), Sale());

        await vm.ConfirmCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowWarning("Devolución CxC", It.Is<string>(m => m.Contains("al menos"))), Times.Once);
        sales.Verify(s => s.RegisterCreditReturnAsync(It.IsAny<int>(), It.IsAny<CreditReturnRequestClientDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReturnDialog_QuantityAboveSold_ShowsValidation()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        var vm = new CreditReturnDialogViewModel(sales.Object, dialog.Object, Receivable(), Sale());
        vm.Lines.First().QuantityText = "3";

        await vm.ConfirmCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowWarning("Devolución CxC", It.Is<string>(m => m.Contains("excede"))), Times.Once);
        sales.Verify(s => s.RegisterCreditReturnAsync(It.IsAny<int>(), It.IsAny<CreditReturnRequestClientDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void StatementDialog_ExposesDualBalanceAndCloses()
    {
        var dialog = new Mock<IDialogService>();
        var statement = new CreditStatementClientDto
        {
            Profile = new CreditProfileClientDto { CustomerId = 7, CustomerName = "Ana Pérez", CurrentDebtUSD = 400m },
            TodayRate = 40m,
            CurrentDebtTodayBsS = 16000m,
            Receivables = new List<CreditStatementReceivableClientDto>
            {
                new() { ReceivableId = 3, BalanceUSD = 400m, BalanceTodayBsS = 16000m, Status = "Vigente" }
            },
            Payments = new List<CreditStatementPaymentClientDto>
            {
                new() { PaymentId = 55, AmountUSD = 20m, AmountBsS = 800m, PaymentRate = 40m }
            }
        };
        var vm = new CreditStatementDialogViewModel(statement, dialog.Object);

        Assert.Equal("Ana Pérez", vm.CustomerName);
        Assert.Equal(16000m, vm.CurrentDebtBsS);
        Assert.Single(vm.Receivables);
        Assert.Single(vm.Payments);
        Assert.Equal(40m, vm.TodayRate);

        vm.CloseCommand.Execute(null);
        dialog.Verify(d => d.CloseCurrentModal(null), Times.Once);
    }
}
