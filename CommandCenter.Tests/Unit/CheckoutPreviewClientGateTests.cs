using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;
using ISalesService = Desktop.Client.Services.ISalesService;
using SalePaymentDto = Desktop.Client.Services.SalePaymentDto;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.156 (CLEAN-05 / REQ-WCP-02): espejo del gate del web (`CheckoutPreviewGate.test.js`).
/// El checkout WPF solo puede finalizar con un preview FRESCO del servidor: firma vigente,
/// sin fallo y con el estado de liquidación devuelto por /checkout-preview.
/// </summary>
public class CheckoutPreviewClientGateTests
{
    private const string CanonicalPreviewFailureMessage =
        "No se pudo validar el cobro con el servidor. Verifique la conexión e intente nuevamente (la transacción no puede cerrarse sin la validación canónica).";

    private static readonly PaymentMethodDto CashMethod = new()
    {
        Id = 1,
        Name = "Efectivo USD",
        IsCash = true,
        RequiresReference = false,
        IsActive = true
    };

    private static CheckoutViewModel CreateViewModel(Mock<ISalesService> salesService, SaleDto sale, SaleDto? overrideSale = null)
    {
        var vm = new CheckoutViewModel(
            sale: sale,
            availableMethods: new ObservableCollection<PaymentMethodDto> { CashMethod },
            salesService: salesService.Object,
            currentExchangeRate: 50m,
            overrideSale: overrideSale);

        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);
        return vm;
    }

    private static void AddCashPayment(CheckoutViewModel vm, string amountBsSText)
    {
        vm.SelectedMethod = CashMethod;
        vm.AmountBsSText = amountBsSText;
        vm.AddPaymentCommand.Execute(null);
    }

    /// <summary>Espejo mínimo del preview del servidor: deriva el estado del request real
    /// (siempre responde sobre los pagos vigentes, como /checkout-preview).</summary>
    private static CheckoutPreviewClientDto BuildPreview(decimal totalUsd, decimal rate, IEnumerable<SalePaymentDto> payments)
    {
        var list = payments.ToList();
        decimal totalBsS = System.Math.Round(totalUsd * rate, 2, MidpointRounding.AwayFromZero);
        decimal paidUsd = list.Sum(p => System.Math.Round(p.Amount, 2, MidpointRounding.AwayFromZero));
        decimal paidBsS = list.Sum(p => System.Math.Round(p.AmountBsS > 0m ? p.AmountBsS : p.AmountLocal, 2, MidpointRounding.AwayFromZero));
        decimal remainingUsd = System.Math.Max(0m, totalUsd - paidUsd);

        return new CheckoutPreviewClientDto
        {
            TotalUSD = totalUsd,
            TotalBsS = totalBsS,
            TotalPaidUSD = paidUsd,
            TotalPaidBsS = paidBsS,
            RemainingBalanceUSD = remainingUsd,
            RemainingBalanceBsS = System.Math.Max(0m, totalBsS - paidBsS),
            RoundingAdjustment = remainingUsd <= 0.01m ? 0.35m : 0m,
            IsFullyPaid = remainingUsd <= 0.05m
        };
    }

    private static void SetupPreview(Mock<ISalesService> salesService, decimal totalUsd = 100m)
    {
        salesService
            .Setup(s => s.GetCheckoutPreviewAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<IEnumerable<SalePaymentDto>>()))
            .ReturnsAsync((int _, decimal rate, IEnumerable<SalePaymentDto> payments) => BuildPreview(totalUsd, rate, payments));
    }

    /// <summary>Seam determinista (REQ-WCP-02): espera la última validación canónica disparada.</summary>
    private static async Task AwaitPreviewAsync(CheckoutViewModel vm)
    {
        var pending = vm.PendingPreview;
        if (pending != null) await pending;
    }

    [Fact]
    public async Task Gate_FreshFullPreview_AllowsFinalize_WithServerRoundingAdjustment()
    {
        var salesService = new Mock<ISalesService>();
        SetupPreview(salesService);

        var sale = new SaleDto { Id = 1, TotalUSD = 100m, AppliedRate = 50m, CustomerId = 1, CustomerName = "Maria Perez" };
        var vm = CreateViewModel(salesService, sale);
        AddCashPayment(vm, "5000.00");
        await AwaitPreviewAsync(vm);

        Assert.True(vm.IsFullLiquidation);
        Assert.True(vm.CanFinalize);
        Assert.Equal(0.35m, vm.RoundingAdjustment);
        Assert.Null(vm.ValidationHelperMessage);
    }

    [Fact]
    public async Task Gate_StaleSignature_BlocksFinalizeUntilFreshResponse()
    {
        var salesService = new Mock<ISalesService>();
        SetupPreview(salesService);

        var sale = new SaleDto { Id = 1, TotalUSD = 100m, AppliedRate = 50m, CustomerId = 1, CustomerName = "Maria Perez" };
        var vm = CreateViewModel(salesService, sale);
        AddCashPayment(vm, "5000.00");
        await AwaitPreviewAsync(vm);
        Assert.True(vm.CanFinalize);

        // El pago muta sin comando (sin respuesta nueva): la firma vigente deja de coincidir
        // con la del preview aplicado y el gate debe cerrarse.
        vm.Payments.Add(new CheckoutPaymentItem(new SalePaymentDto(1, 5m, 250m, null), "Efectivo USD", 250m, true));

        Assert.False(vm.CanFinalize);
        Assert.Equal(0m, vm.RoundingAdjustment);
    }

    [Fact]
    public async Task Gate_PreviewFailure_BlocksFinalize_WithCanonicalMessage()
    {
        var salesService = new Mock<ISalesService>();
        salesService
            .Setup(s => s.GetCheckoutPreviewAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<IEnumerable<SalePaymentDto>>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("sin conexión"));

        var sale = new SaleDto { Id = 1, TotalUSD = 100m, AppliedRate = 50m, CustomerId = 1, CustomerName = "Maria Perez" };
        var vm = CreateViewModel(salesService, sale);
        AddCashPayment(vm, "5000.00");
        await AwaitPreviewAsync(vm);

        Assert.False(vm.CanFinalize);
        Assert.False(vm.IsFullLiquidation);
        Assert.Equal(CanonicalPreviewFailureMessage, vm.ValidationHelperMessage);
    }

    [Fact]
    public async Task Gate_OverridePartialPreview_AllowsAdvance_WithRegistrarAbonoLabel()
    {
        var salesService = new Mock<ISalesService>();
        SetupPreview(salesService);

        var onHoldSale = new SaleDto
        {
            Id = 5,
            TotalUSD = 100m,
            TotalPaidUSD = 0m,
            RemainingBalanceUSD = 100m,
            CustomerId = 2,
            CustomerName = "Carlos Gomez"
        };

        var vm = CreateViewModel(salesService, onHoldSale, overrideSale: onHoldSale);
        AddCashPayment(vm, "1500.00");
        await AwaitPreviewAsync(vm);

        Assert.False(vm.IsFullLiquidation);
        Assert.True(vm.CanFinalize);
        Assert.Equal("REGISTRAR ABONO", vm.FinalizeSaleButtonLabel);
    }

    [Fact]
    public async Task Gate_FreshNotFullyPaid_NormalSale_BlocksFinalize_WithRemainingMessage()
    {
        var salesService = new Mock<ISalesService>();
        SetupPreview(salesService);

        var sale = new SaleDto { Id = 1, TotalUSD = 100m, AppliedRate = 50m, CustomerId = 1, CustomerName = "Maria Perez" };
        var vm = CreateViewModel(salesService, sale);
        AddCashPayment(vm, "2500.00");
        await AwaitPreviewAsync(vm);

        Assert.False(vm.IsFullLiquidation);
        Assert.False(vm.CanFinalize);
        Assert.Equal("El monto acumulado aún no cubre el 100% del total de la venta.", vm.ValidationHelperMessage);
    }
}
