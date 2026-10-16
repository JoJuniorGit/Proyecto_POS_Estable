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
/// 8.185-T5 (S4/S5): el checkout cobra el precio de venta EFECTIVO publicado por el carrito.
/// El total mostrado sale del total efectivo del preview del servidor (T3), las patas USD/Bs.S
/// de cada pago viajan consistentes a la tasa anclada (una derivada de la otra) y la validación
/// es dual-dominio como el gate del servidor; el redondeo de céntimos residuales (RoundToCash)
/// se conserva intacto.
/// </summary>
public class CheckoutUxEffectivePricePaymentsTests
{
    private const decimal Rate = 36.51m;

    // Total USD canónico del carrito y total EFECTIVO Bs.S del precio publicado (política cash).
    private const decimal SaleTotalUsd = 3.00m;
    private const decimal EffectiveTotalBsS = 110.00m;

    private static readonly PaymentMethodDto CashMethod = new()
    {
        Id = 1,
        Name = "Efectivo Bs.S",
        IsCash = true,
        RequiresReference = false,
        IsActive = true
    };

    private static readonly PaymentMethodDto DigitalMethod = new()
    {
        Id = 2,
        Name = "Pago Móvil Bs.S",
        IsCash = false,
        RequiresReference = false,
        IsActive = true
    };

    /// <summary>Espejo del preview T3: total Bs.S EFECTIVO y cobertura dual-dominio
    /// (remanente USD y remanente Bs.S contra el efectivo); el vuelto se mide contra el efectivo.</summary>
    private static CheckoutPreviewClientDto BuildEffectivePreview(decimal effectiveTotalBsS, decimal totalUsd, decimal rate, IEnumerable<SalePaymentDto> payments)
    {
        var list = payments.ToList();
        decimal paidUsd = list.Sum(p => Math.Round(p.Amount, 2, MidpointRounding.AwayFromZero));
        decimal paidBsS = list.Sum(p => Math.Round(p.AmountBsS > 0m ? p.AmountBsS : p.AmountLocal, 2, MidpointRounding.AwayFromZero));
        decimal remainingUsd = Math.Max(0m, totalUsd - paidUsd);
        decimal remainingBsS = Math.Max(0m, effectiveTotalBsS - paidBsS);
        bool fullyPaid = !(remainingUsd > 0.05m && remainingBsS > 0.05m * rate);
        decimal paidBsSExcess = Math.Round(paidBsS - effectiveTotalBsS, 2, MidpointRounding.AwayFromZero);

        return new CheckoutPreviewClientDto
        {
            TotalUSD = totalUsd,
            TotalBsS = effectiveTotalBsS,
            TotalPaidUSD = paidUsd,
            TotalPaidBsS = paidBsS,
            RemainingBalanceUSD = remainingUsd,
            RemainingBalanceBsS = remainingBsS,
            RoundingAdjustment = remainingUsd <= 0.01m ? paidBsSExcess : 0m,
            ChangeDueUSD = paidBsSExcess > 0.05m * rate ? Math.Round(paidBsSExcess / rate, 2, MidpointRounding.AwayFromZero) : 0m,
            ChangeDueBsS = paidBsSExcess > 0.05m * rate ? paidBsSExcess : 0m,
            IsFullyPaid = fullyPaid
        };
    }

    private static CheckoutViewModel CreateViewModel(Mock<ISalesService> salesService, Mock<IDialogService>? dialog = null, decimal? saleTotalBsS = null)
    {
        var sale = new SaleDto
        {
            Id = 1,
            TotalUSD = SaleTotalUsd,
            TotalBsS = saleTotalBsS ?? EffectiveTotalBsS,
            AppliedRate = Rate,
            CustomerId = 1,
            CustomerName = "Maria Perez"
        };

        var vm = new CheckoutViewModel(
            sale: sale,
            availableMethods: new ObservableCollection<PaymentMethodDto> { CashMethod, DigitalMethod },
            salesService: salesService.Object,
            currentExchangeRate: Rate,
            dialogService: dialog?.Object);

        // Aislamiento del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);
        return vm;
    }

    private static void SetupPreview(Mock<ISalesService> salesService)
    {
        salesService
            .Setup(s => s.GetCheckoutPreviewAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<IEnumerable<SalePaymentDto>>()))
            .ReturnsAsync((int _, decimal rate, IEnumerable<SalePaymentDto> payments) =>
                BuildEffectivePreview(EffectiveTotalBsS, SaleTotalUsd, rate, payments));
    }

    private static async Task AwaitPreviewAsync(CheckoutViewModel vm)
    {
        var pending = vm.PendingPreview;
        if (pending != null) await pending;
    }

    private static void AssertPairConsistent(SalePaymentDto payment, decimal rate)
    {
        Assert.True(
            Math.Abs(payment.AmountBsS / rate - payment.Amount) <= 0.01m,
            $"Par USD/Bs.S inconsistente: Bs.S={payment.AmountBsS}, USD={payment.Amount}, tasa={rate}.");
    }

    [Fact]
    public async Task AddPayment_ExactEffectiveCashTotal_KeepsConsistentPair_AndAllowsFinalize()
    {
        var salesService = new Mock<ISalesService>();
        SetupPreview(salesService);
        var vm = CreateViewModel(salesService);

        vm.SelectedMethod = CashMethod;
        vm.AmountBsSText = "110.00";
        vm.AddPaymentCommand.Execute(null);
        await AwaitPreviewAsync(vm);

        // El pago exacto del precio efectivo se conserva íntegro (sin clamp a la cuota canónica).
        var payment = Assert.Single(vm.Payments);
        Assert.Equal(110.00m, payment.Dto.AmountBsS);
        Assert.Equal(3.01m, payment.Dto.Amount); // 110.00 / 36.51 = 3.0128... → 2dp
        AssertPairConsistent(payment.Dto, Rate);

        Assert.True(vm.IsFullLiquidation);
        Assert.True(vm.CanFinalize);
        Assert.Equal(0m, vm.RoundingAdjustment); // exacto al efectivo: el escalón no se devuelve
    }

    [Fact]
    public async Task AddPayment_MixedPayments_CoverEffectiveTotal_WithConsistentPairs()
    {
        var salesService = new Mock<ISalesService>();
        SetupPreview(salesService);
        var vm = CreateViewModel(salesService);

        // Primer abono parcial: 55.05 Bs.S (≈ USD 1.51).
        vm.SelectedMethod = DigitalMethod;
        vm.AmountBsSText = "55.05";
        vm.AddPaymentCommand.Execute(null);
        await AwaitPreviewAsync(vm);

        Assert.Single(vm.Payments);
        Assert.Equal(54.95m, vm.RemainingBalanceLocal);

        // El remanente en Bs.S cubre el total efectivo aunque exceda el remanente canónico USD:
        // el par sigue siendo consistente (una pata derivada de la otra a la tasa vigente).
        vm.AmountBsSText = "54.95";
        vm.AddPaymentCommand.Execute(null);
        await AwaitPreviewAsync(vm);

        Assert.Equal(2, vm.Payments.Count);
        Assert.All(vm.Payments, p => AssertPairConsistent(p.Dto, Rate));

        Assert.Equal(110.00m, vm.PaidAmountLocal); // sin decimales añadidos sobre el efectivo publicado
        Assert.Equal(0m, vm.RemainingBalanceLocal);
        Assert.True(vm.IsFullLiquidation);
        Assert.True(vm.CanFinalize);
    }

    [Fact]
    public async Task AddPayment_ExceedingBothDomains_IsRejected()
    {
        var salesService = new Mock<ISalesService>();
        SetupPreview(salesService);
        var dialog = new Mock<IDialogService>();
        var vm = CreateViewModel(salesService, dialog);

        vm.SelectedMethod = DigitalMethod;
        vm.AmountBsSText = "113.00"; // excede el remanente USD (≈ USD 3.10 > 3.05) y el Bs.S (113 > 111.83)
        vm.AddPaymentCommand.Execute(null);
        await AwaitPreviewAsync(vm);

        Assert.Empty(vm.Payments);
        dialog.Verify(d => d.ShowWarning("Monto Excedido", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task TotalAmountLocal_WithFreshPreview_UsesEffectiveServerTotal()
    {
        // El snapshot canónico del carrito (109.53) no debe pisar el total EFECTIVO del preview.
        var salesService = new Mock<ISalesService>();
        SetupPreview(salesService);
        var vm = CreateViewModel(salesService, saleTotalBsS: 109.53m);

        await AwaitPreviewAsync(vm);

        Assert.True(vm.IsPreviewFresh);
        Assert.Equal(110.00m, vm.TotalAmountLocal);
        Assert.Equal(110.00m, vm.RemainingBalanceLocal);
    }

    [Fact]
    public async Task SetAmountToRemainingBalance_KeepsRoundToCashResidual_ForCashMethod()
    {
        var salesService = new Mock<ISalesService>();
        salesService
            .Setup(s => s.GetCheckoutPreviewAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<IEnumerable<SalePaymentDto>>()))
            .ReturnsAsync((int _, decimal rate, IEnumerable<SalePaymentDto> payments) =>
                BuildEffectivePreview(73.49m, SaleTotalUsd, rate, payments));

        var vm = CreateViewModel(salesService);
        await AwaitPreviewAsync(vm);

        // Efectivo: redondeo de céntimos residuales a entero (comportamiento previo intacto).
        vm.SelectedMethod = CashMethod;
        Assert.Equal(73m, CheckoutViewModel.ParseAmount(vm.AmountBsSText));
        Assert.Equal(2.00m, vm.AmountUsdPreview);

        // Digital: conserva los centavos del saldo efectivo sin redondeo de caja.
        vm.SelectedMethod = DigitalMethod;
        Assert.Equal(73.49m, CheckoutViewModel.ParseAmount(vm.AmountBsSText));
    }
}
