using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T4 (F3, S3/S6/S9): modal de abono CxC. La captura arranca en la moneda local (Bs.S en Dual) y
/// sólo cambia a USD con métodos de divisas; el monto exacto liquida el saldo sin residuo de
/// redondeo. La imputación sigue siendo FIFO de cartera o explícita por factura.
/// </summary>
[Xunit.Collection(CommandCenter.Tests.CurrencyDisplayStateCollection.Name)]
public class CreditReceivablesPaymentTests
{
    private static CreditReceivableClientItemDto Row(int id, decimal balance, string status = "Vigente") => new()
    {
        ReceivableId = id,
        CustomerId = 7,
        CustomerName = "Ana Pérez",
        CustomerCedulaOrRif = "V-12345678",
        SaleId = 100 + id,
        InvoiceNumber = 200 + id,
        OriginalUSD = balance,
        BalanceUSD = balance,
        BalanceTodayBsS = balance * 40m,
        DueDateUtc = DateTime.UtcNow.AddDays(10),
        Status = status
    };

    private static PaymentMethodDto Cash() => new()
    {
        Id = 1,
        Name = "Efectivo USD",
        IsCash = true,
        Currency = "USD"
    };

    private static PaymentMethodDto Transfer() => new()
    {
        Id = 2,
        Name = "Transferencia",
        IsCash = false,
        RequiresReference = true,
        Currency = "Bs.S"
    };

    private static CreditPaymentDialogViewModel Create(
        Mock<ISalesService> sales,
        Mock<IDialogService> dialog,
        IEnumerable<CreditReceivableClientItemDto>? rows = null,
        decimal rate = 40m,
        decimal? suggested = null)
    {
        return new CreditPaymentDialogViewModel(
            sales.Object,
            dialog.Object,
            7,
            "Ana Pérez",
            rows ?? new[] { Row(1, 100m), Row(2, 50m, "Parcial") },
            new[] { Cash(), Transfer() },
            rate,
            suggested,
            "V-12345678");
    }

    [Fact]
    public void Open_DefaultsToLocalCurrencyAndPrefillsExactUsdEquivalent()
    {
        var vm = Create(new Mock<ISalesService>(), new Mock<IDialogService>(), rate: 40m, suggested: 19.50m);

        Assert.Equal("Bs.S", vm.InputCurrencyLabel);
        Assert.False(vm.IsInputUsd);
        Assert.Equal(19.50m, vm.AmountUSD);
        Assert.Equal(780.00m, vm.AmountBsS);
        Assert.Contains("≈", vm.LiveConversionDisplay);
        Assert.Contains("19", vm.LiveConversionDisplay);
        Assert.Contains("V-12345678", vm.CustomerContextDisplay);
    }

    [Fact]
    public void SelectingUsdMethod_SwitchesCaptureCurrencyAndLocalMethodReturnsToBsS()
    {
        var vm = Create(new Mock<ISalesService>(), new Mock<IDialogService>(), rate: 40m, suggested: 19.50m);

        vm.SelectedMethod = vm.AvailableMethods.First(m => m.Id == 1);

        Assert.True(vm.IsInputUsd);
        Assert.Equal("USD", vm.InputCurrencyLabel);
        Assert.Equal(19.50m, vm.AmountUSD);

        vm.SelectedMethod = vm.AvailableMethods.First(m => m.Id == 2);

        Assert.False(vm.IsInputUsd);
        Assert.Equal("Bs.S", vm.InputCurrencyLabel);
        Assert.Equal(19.50m, vm.AmountUSD);
        Assert.Equal(780.00m, vm.AmountBsS);
    }

    [Fact]
    public void FullLocalPayment_LiquidatesWithoutRoundingResidue()
    {
        var rows = new[] { Row(1, 19.50m) };
        var vm = Create(new Mock<ISalesService>(), new Mock<IDialogService>(), rows, rate: 926.36m);

        Assert.Equal(19.50m, vm.AmountUSD);
        Assert.Equal(0m, vm.RemainingAfterUSD);
        Assert.True(vm.IsRemainingZero);
        Assert.Equal(vm.PendingLocalDisplay, vm.PaymentLocalDisplay);
    }

    [Fact]
    public void CanConfirm_RequiresMethodReferenceAndExplicitInvoiceSelection()
    {
        var vm = Create(
            new Mock<ISalesService>(),
            new Mock<IDialogService>(),
            rows: new[] { Row(1, 100m) },
            rate: 40m,
            suggested: 100m);

        Assert.False(vm.CanConfirm);
        Assert.Contains("método de pago", vm.ValidationMessage);

        vm.SelectedMethod = vm.AvailableMethods.First(m => m.Id == 2);

        Assert.False(vm.CanConfirm);
        Assert.Contains("referencia", vm.ValidationMessage);

        vm.Reference = "REF-99";
        Assert.True(vm.CanConfirm);

        vm.SelectExplicitModeCommand.Execute(null);
        Assert.False(vm.CanConfirm);
        Assert.Contains("factura", vm.ValidationMessage);

        vm.OpenReceivables.First().IsSelected = true;
        Assert.True(vm.CanConfirm);
    }

    [Fact]
    public async Task Confirm_Fifo_BuildsRequestWithoutReceivableIds()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        var vm = Create(sales, dialog, suggested: 120m);
        vm.SelectedMethod = vm.AvailableMethods.First();
        vm.AmountText = "120.00";
        CreditPaymentRequestClientDto? request = null;
        sales.Setup(s => s.RegisterCreditPaymentAsync(It.IsAny<CreditPaymentRequestClientDto>(), It.IsAny<CancellationToken>()))
            .Callback<CreditPaymentRequestClientDto, CancellationToken>((r, _) => request = r)
            .ReturnsAsync(new CreditPaymentResultClientDto { PaymentId = 55, CustomerId = 7, AmountUSD = 120m, PaymentRate = 40m });

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.NotNull(request);
        Assert.Equal(7, request!.CustomerId);
        Assert.Equal(120m, request.AmountUSD);
        Assert.Equal(1, request.PaymentMethodId);
        Assert.Null(request.ReceivableIds);
        Assert.False(string.IsNullOrWhiteSpace(request.IdempotencyKey));
        Assert.NotNull(vm.Result);
        dialog.Verify(d => d.CloseCurrentModal(vm.Result!), Times.Once);
    }

    [Fact]
    public async Task Confirm_Explicit_SendsSelectedInvoiceIdsInOrder()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        var vm = Create(sales, dialog);
        vm.SelectedMethod = vm.AvailableMethods.First();
        vm.SelectExplicitModeCommand.Execute(null);
        vm.OpenReceivables.First(o => o.Receivable.ReceivableId == 2).IsSelected = true;
        vm.AmountText = "30.00";
        CreditPaymentRequestClientDto? request = null;
        sales.Setup(s => s.RegisterCreditPaymentAsync(It.IsAny<CreditPaymentRequestClientDto>(), It.IsAny<CancellationToken>()))
            .Callback<CreditPaymentRequestClientDto, CancellationToken>((r, _) => request = r)
            .ReturnsAsync(new CreditPaymentResultClientDto { PaymentId = 56, CustomerId = 7, AmountUSD = 30m });

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.NotNull(request);
        Assert.Equal(new List<int> { 2 }, request!.ReceivableIds);
    }

    [Fact]
    public async Task Confirm_ExplicitWithoutSelection_ShowsValidationAndDoesNotCallService()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        var vm = Create(sales, dialog);
        vm.SelectedMethod = vm.AvailableMethods.First();
        vm.SelectExplicitModeCommand.Execute(null);
        vm.AmountText = "30.00";

        await vm.ConfirmCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowWarning("Abono CxC", It.Is<string>(m => m.Contains("factura"))), Times.Once);
        sales.Verify(s => s.RegisterCreditPaymentAsync(It.IsAny<CreditPaymentRequestClientDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Confirm_WithReferenceRequirement_BlocksUntilReferenceIsProvided()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        var vm = Create(sales, dialog);
        vm.SelectedMethod = vm.AvailableMethods.First(m => m.Id == 2);
        vm.AmountText = "800.00";

        await vm.ConfirmCommand.ExecuteAsync(null);
        dialog.Verify(d => d.ShowWarning("Abono CxC", It.Is<string>(m => m.Contains("referencia"))), Times.Once);

        vm.Reference = "REF-99";
        sales.Setup(s => s.RegisterCreditPaymentAsync(It.IsAny<CreditPaymentRequestClientDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditPaymentResultClientDto { PaymentId = 57, CustomerId = 7, AmountUSD = 20m });

        await vm.ConfirmCommand.ExecuteAsync(null);
        sales.Verify(s => s.RegisterCreditPaymentAsync(It.Is<CreditPaymentRequestClientDto>(r => r.ReferenceNumber == "REF-99"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_WhenExplicitAmountExceedsSelectedBalance_IsBlocked()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        var vm = Create(sales, dialog);
        vm.SelectedMethod = vm.AvailableMethods.First();
        vm.SelectExplicitModeCommand.Execute(null);
        vm.OpenReceivables.First().IsSelected = true;
        vm.AmountText = "150.00";

        await vm.ConfirmCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowWarning("Abono CxC", It.Is<string>(m => m.Contains("saldo"))), Times.Once);
        sales.Verify(s => s.RegisterCreditPaymentAsync(It.IsAny<CreditPaymentRequestClientDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Confirm_WhenServiceFails_ShowsErrorAndKeepsDialogOpen()
    {
        var sales = new Mock<ISalesService>();
        var dialog = new Mock<IDialogService>();
        var vm = Create(sales, dialog);
        vm.SelectedMethod = vm.AvailableMethods.First();
        vm.AmountText = "10.00";
        sales.Setup(s => s.RegisterCreditPaymentAsync(It.IsAny<CreditPaymentRequestClientDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("El abono excede el saldo pendiente."));

        await vm.ConfirmCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowError("Abono CxC", It.Is<string>(m => m.Contains("excede"))), Times.Once);
        dialog.Verify(d => d.CloseCurrentModal(It.IsAny<object>()), Times.Never);
        Assert.Null(vm.Result);
    }
}
