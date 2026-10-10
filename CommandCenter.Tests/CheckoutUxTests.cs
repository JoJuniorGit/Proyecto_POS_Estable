using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Sales.Module.Entities;
using Sales.Module.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using ISalesService = Desktop.Client.Services.ISalesService;
using SalePaymentDto = Desktop.Client.Services.SalePaymentDto;

namespace CommandCenter.Tests;

public class CheckoutUxTests
{
    private readonly Mock<ISalesService> _mockSalesService;
    private readonly Mock<IDialogService> _mockDialogService;
    private readonly ObservableCollection<PaymentMethodDto> _paymentMethods;

    public CheckoutUxTests()
    {
        _mockSalesService = new Mock<ISalesService>();
        _mockDialogService = new Mock<IDialogService>();

        _paymentMethods = new ObservableCollection<PaymentMethodDto>
        {
            new() { Id = 1, Name = "Efectivo USD", IsCash = true, RequiresReference = false, IsActive = true },
            new() { Id = 2, Name = "Pago Móvil Bs.S", IsCash = false, RequiresReference = true, IsActive = true }
        };
    }

    /// <summary>8.156 (CLEAN-05): espejo mínimo del preview del servidor para derivar el gate desde
    /// el escenario (el VM ya no decide la liquidación con aritmética local).</summary>
    private void SetupPreviewMirror(SaleDto targetSale)
    {
        _mockSalesService
            .Setup(s => s.GetCheckoutPreviewAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<IEnumerable<SalePaymentDto>>()))
            .ReturnsAsync((int _, decimal rate, IEnumerable<SalePaymentDto> payments) => BuildPreview(targetSale, rate, payments));
    }

    private static CheckoutPreviewClientDto BuildPreview(SaleDto targetSale, decimal rate, IEnumerable<SalePaymentDto> payments)
    {
        var list = payments.ToList();
        decimal totalUsd = targetSale.TotalUSD;
        decimal totalBsS = System.Math.Round(totalUsd * rate, 2, System.MidpointRounding.AwayFromZero);
        decimal paidUsd = list.Sum(p => System.Math.Round(p.Amount, 2, System.MidpointRounding.AwayFromZero));
        decimal paidBsS = list.Sum(p => System.Math.Round(p.AmountBsS > 0m ? p.AmountBsS : p.AmountLocal, 2, System.MidpointRounding.AwayFromZero));
        decimal remainingUsd = System.Math.Max(0m, totalUsd - paidUsd);

        return new CheckoutPreviewClientDto
        {
            TotalUSD = totalUsd,
            TotalBsS = totalBsS,
            TotalPaidUSD = paidUsd,
            TotalPaidBsS = paidBsS,
            RemainingBalanceUSD = remainingUsd,
            RemainingBalanceBsS = System.Math.Max(0m, totalBsS - paidBsS),
            RoundingAdjustment = remainingUsd <= 0.01m ? paidBsS - totalBsS : 0m,
            IsFullyPaid = remainingUsd <= 0.05m
        };
    }

    /// <summary>Seam determinista (REQ-WCP-02): espera la última validación canónica disparada.</summary>
    private static async Task AwaitPreviewAsync(CheckoutViewModel vm)
    {
        var pending = vm.PendingPreview;
        if (pending != null) await pending;
    }

    [Fact]
    public async Task CanFinalize_NormalSale_NoPayments_ReturnsFalse()
    {
        var sale = new SaleDto
        {
            Id = 1,
            TotalUSD = 100m,
            CustomerId = 1,
            CustomerName = "Consumidor Final"
        };

        SetupPreviewMirror(sale);
        var vm = new CheckoutViewModel(
            sale: sale,
            availableMethods: _paymentMethods,
            salesService: _mockSalesService.Object,
            currentExchangeRate: 50m,
            dialogService: _mockDialogService.Object);

        // Aislamiento del bus global compartido entre pruebas (CartUpdatedMessage cambiaría TotalUSD).
        WeakReferenceMessenger.Default.UnregisterAll(vm);
        await AwaitPreviewAsync(vm);

        Assert.False(vm.HasValidPayments);
        Assert.False(vm.IsFullLiquidation);
        Assert.False(vm.CanFinalize);
        Assert.Equal("COBRAR Y FINALIZAR", vm.FinalizeSaleButtonLabel);
        Assert.Contains("Agregue al menos un método de pago", vm.ValidationHelperMessage);
    }

    [Fact]
    public async Task CanFinalize_NormalSale_PartialPayment_ReturnsFalse()
    {
        var sale = new SaleDto
        {
            Id = 1,
            TotalUSD = 100m,
            CustomerId = 1,
            CustomerName = "Consumidor Final"
        };

        SetupPreviewMirror(sale);
        var vm = new CheckoutViewModel(
            sale: sale,
            availableMethods: _paymentMethods,
            salesService: _mockSalesService.Object,
            currentExchangeRate: 50m,
            dialogService: _mockDialogService.Object);

        // Aislamiento del bus global compartido entre pruebas (CartUpdatedMessage cambiaría TotalUSD).
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        // Add 50 USD payment (out of 100 USD) -> 2500 Bs.S
        vm.SelectedMethod = _paymentMethods[0];
        vm.AmountBsSText = "2500.00";
        vm.AddPaymentCommand.Execute(null);
        await AwaitPreviewAsync(vm);

        Assert.True(vm.HasValidPayments);
        Assert.False(vm.IsFullLiquidation);
        Assert.False(vm.CanFinalize);
        Assert.Equal("COBRAR Y FINALIZAR", vm.FinalizeSaleButtonLabel);
        Assert.Contains("no cubre el 100%", vm.ValidationHelperMessage);
    }

    [Fact]
    public async Task CanFinalize_NormalSale_FullPayment_ReturnsTrue()
    {
        var sale = new SaleDto
        {
            Id = 1,
            TotalUSD = 100m,
            CustomerId = 1,
            CustomerName = "Consumidor Final"
        };

        SetupPreviewMirror(sale);
        var vm = new CheckoutViewModel(
            sale: sale,
            availableMethods: _paymentMethods,
            salesService: _mockSalesService.Object,
            currentExchangeRate: 50m,
            dialogService: _mockDialogService.Object);

        // Aislamiento del bus global compartido entre pruebas (CartUpdatedMessage cambiaría TotalUSD).
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        // Add 100 USD full payment -> 5000 Bs.S
        vm.SelectedMethod = _paymentMethods[0];
        vm.AmountBsSText = "5000.00";
        vm.AddPaymentCommand.Execute(null);
        await AwaitPreviewAsync(vm);

        Assert.True(vm.HasValidPayments);
        Assert.True(vm.IsFullLiquidation);
        Assert.True(vm.CanFinalize);
        Assert.Equal("COBRAR Y FINALIZAR", vm.FinalizeSaleButtonLabel);
        Assert.Null(vm.ValidationHelperMessage);
    }

    [Fact]
    public async Task CanFinalize_OverrideSale_PartialPayment_ReturnsTrue_WithRegistrarAbonoLabel()
    {
        var onHoldSale = new SaleDto
        {
            Id = 5,
            TotalUSD = 100m,
            TotalPaidUSD = 0m,
            RemainingBalanceUSD = 100m,
            CustomerId = 2,
            CustomerName = "Carlos Gomez"
        };

        SetupPreviewMirror(onHoldSale);
        var vm = new CheckoutViewModel(
            sale: onHoldSale,
            availableMethods: _paymentMethods,
            salesService: _mockSalesService.Object,
            currentExchangeRate: 50m,
            overrideSale: onHoldSale,
            dialogService: _mockDialogService.Object);

        // Aislamiento del bus global compartido entre pruebas (CartUpdatedMessage cambiaría TotalUSD).
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        // Add 30 USD partial abono -> 1500 Bs.S
        vm.SelectedMethod = _paymentMethods[0];
        vm.AmountBsSText = "1500.00";
        vm.AddPaymentCommand.Execute(null);
        await AwaitPreviewAsync(vm);

        Assert.True(vm.IsOverrideMode);
        Assert.True(vm.HasValidPayments);
        Assert.False(vm.IsFullLiquidation);
        Assert.True(vm.CanFinalize); // In override mode, partial abonos can be finalized!
        Assert.Equal("REGISTRAR ABONO", vm.FinalizeSaleButtonLabel);
    }

    [Fact]
    public async Task CanFinalize_OverrideSale_FullPayment_ReturnsTrue_WithLiquidarCuentaLabel()
    {
        var onHoldSale = new SaleDto
        {
            Id = 5,
            TotalUSD = 100m,
            TotalPaidUSD = 0m,
            RemainingBalanceUSD = 100m,
            CustomerId = 2,
            CustomerName = "Carlos Gomez"
        };

        SetupPreviewMirror(onHoldSale);
        var vm = new CheckoutViewModel(
            sale: onHoldSale,
            availableMethods: _paymentMethods,
            salesService: _mockSalesService.Object,
            currentExchangeRate: 50m,
            overrideSale: onHoldSale,
            dialogService: _mockDialogService.Object);

        // Aislamiento del bus global compartido entre pruebas (CartUpdatedMessage cambiaría TotalUSD).
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        // Add 100 USD full payment -> 5000 Bs.S
        vm.SelectedMethod = _paymentMethods[0];
        vm.AmountBsSText = "5000.00";
        vm.AddPaymentCommand.Execute(null);
        await AwaitPreviewAsync(vm);

        Assert.True(vm.IsOverrideMode);
        Assert.True(vm.HasValidPayments);
        Assert.True(vm.IsFullLiquidation);
        Assert.True(vm.CanFinalize);
        Assert.Equal("LIQUIDAR CUENTA", vm.FinalizeSaleButtonLabel);
    }

    [Fact]
    public async Task CanFinalize_CustodyWithoutIdentifiedCustomer_ReturnsFalse_WithAlertMessage()
    {
        var sale = new SaleDto
        {
            Id = 1,
            TotalUSD = 50m,
            CustomerId = null,
            CustomerName = "Consumidor Final"
        };

        SetupPreviewMirror(sale);
        var vm = new CheckoutViewModel(
            sale: sale,
            availableMethods: _paymentMethods,
            salesService: _mockSalesService.Object,
            currentExchangeRate: 50m,
            dialogService: _mockDialogService.Object);

        // Aislamiento del bus global compartido entre pruebas (CartUpdatedMessage cambiaría TotalUSD).
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        // Add 100% payment
        vm.SelectedMethod = _paymentMethods[0];
        vm.AmountBsSText = "2500.00";
        vm.AddPaymentCommand.Execute(null);
        await AwaitPreviewAsync(vm);

        Assert.True(vm.IsFullLiquidation);

        // Check Pending Pickup
        vm.IsPendingPickup = true;

        Assert.True(vm.IsDefaultCustomer);
        Assert.False(vm.IsCustodyAllowed);
        Assert.False(vm.CanFinalize);
        Assert.NotNull(vm.PendingPickupErrorMessage);
    }

    [Fact]
    public async Task CanFinalize_CustodyWithRealCustomer_ReturnsTrue_WithEnviarARetiroLabel()
    {
        var sale = new SaleDto
        {
            Id = 1,
            TotalUSD = 50m,
            CustomerId = 10,
            CustomerName = "Maria Perez"
        };

        SetupPreviewMirror(sale);
        var vm = new CheckoutViewModel(
            sale: sale,
            availableMethods: _paymentMethods,
            salesService: _mockSalesService.Object,
            currentExchangeRate: 50m,
            dialogService: _mockDialogService.Object);

        // Aislamiento del bus global compartido entre pruebas (CartUpdatedMessage cambiaría TotalUSD).
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        // Add 100% payment
        vm.SelectedMethod = _paymentMethods[0];
        vm.AmountBsSText = "2500.00";
        vm.AddPaymentCommand.Execute(null);
        await AwaitPreviewAsync(vm);

        // Check Pending Pickup
        vm.IsPendingPickup = true;

        Assert.False(vm.IsDefaultCustomer);
        Assert.True(vm.IsCustodyAllowed);
        Assert.True(vm.CanFinalize);
        Assert.Null(vm.PendingPickupErrorMessage);
        Assert.Equal("COBRAR Y ENVIAR A RETIRO", vm.FinalizeSaleButtonLabel);
    }

    [Fact]
    public async Task UpdateCustomer_PreservesExistingPaymentsAndRecalculatesCustody()
    {
        var sale = new SaleDto
        {
            Id = 1,
            TotalUSD = 100m,
            CustomerId = null,
            CustomerName = "Consumidor Final"
        };

        SetupPreviewMirror(sale);
        var vm = new CheckoutViewModel(
            sale: sale,
            availableMethods: _paymentMethods,
            salesService: _mockSalesService.Object,
            currentExchangeRate: 50m,
            dialogService: _mockDialogService.Object);

        // Aislamiento del bus global compartido entre pruebas (CartUpdatedMessage cambiaría TotalUSD).
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        // Add 100% payment
        vm.SelectedMethod = _paymentMethods[0];
        vm.AmountBsSText = "5000.00";
        vm.AddPaymentCommand.Execute(null);
        await AwaitPreviewAsync(vm);

        vm.IsPendingPickup = true;
        Assert.False(vm.CanFinalize); // Blocked because default customer

        // Change customer to real customer
        vm.UpdateCustomer(15, "Roberto Sanchez");

        // Verify payments are preserved
        Assert.Single(vm.Payments);
        Assert.Equal(100m, vm.PaidAmountUsd);
        Assert.Equal(0m, vm.RemainingBalanceUsd);

        // Verify custody is now allowed and button updated
        Assert.False(vm.IsDefaultCustomer);
        Assert.True(vm.IsCustodyAllowed);
        Assert.True(vm.CanFinalize);
        Assert.Equal("COBRAR Y ENVIAR A RETIRO", vm.FinalizeSaleButtonLabel);
    }

    [Fact]
    public void DailyClosure_RoleBasedReceipt_GeneratesBlindAndAuditReceiptsCorrectly()
    {
        var closure = new DailyClosure
        {
            Id = 1,
            ClosureDate = DateTime.UtcNow,
            UserId = "Cajero_01",
            TotalActualBsS = 5000m,
            TotalExpectedBsS = 4800m,
            TotalDifferenceBsS = 200m,
            Details = new List<ClosureDetail>
            {
                new()
                {
                    PaymentMethodId = 1,
                    PaymentMethodName = "Efectivo USD",
                    ActualAmountBsS = 5000m,
                    ExpectedAmountBsS = 4800m,
                    DifferenceBsS = 200m
                }
            }
        };

        // 1. Blind receipt (Cajero)
        var closureDto = ShiftReportMapper.MapClosure(closure);
        string blindReceipt = DailyClosureService.GenerateReceiptContent(closureDto, isBlind: true);
        Assert.Contains("COMPROBANTE DE ARQUEO A CIEGAS", blindReceipt);
        Assert.DoesNotContain("DIFERENCIA", blindReceipt);
        Assert.DoesNotContain("MONTO SISTEMA", blindReceipt);
        Assert.Contains("MONTO DECLARADO (Bs.S)", blindReceipt);

        // 2. Audit receipt (Admin)
        string auditReceipt = DailyClosureService.GenerateReceiptContent(closureDto, isBlind: false);
        Assert.Contains("COMPROBANTE DE CIERRE Y AUDITORÍA DE CAJA", auditReceipt);
        Assert.Contains("MONTO SISTEMA (Bs.S)", auditReceipt);
        Assert.Contains("DIFERENCIA (Bs.S)", auditReceipt);
        Assert.Contains("ESTADO DE CAJA", auditReceipt);
    }
}
