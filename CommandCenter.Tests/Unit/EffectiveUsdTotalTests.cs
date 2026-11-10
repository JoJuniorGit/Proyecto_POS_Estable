using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Sales.Module.Entities;
using Xunit;
using ISalesService = Desktop.Client.Services.ISalesService;
using SalePaymentDto = Desktop.Client.Services.SalePaymentDto;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Sincronización del monto equivalente en USD con el Total Efectivo Redondeado en Bs.S:
/// el $ derivado debe ser el inverso matemático del Bs.S EFECTIVO que se cobra
/// (BsS / tasa), no la suma canónica de precios base. El TotalUSD canónico permanece
/// intacto como referencia (gates, mensajes y recuperación no cambian de semántica).
/// </summary>
public class EffectiveUsdTotalTests
{
    // ---------------------------------------------------------------------
    // Backend: Sale.EffectiveTotalUSD (propiedad calculada, sin schema nuevo)
    // ---------------------------------------------------------------------

    [Fact]
    public void Sale_EffectiveTotalUSD_IsInverseOfRoundedBsSAtAnchoredRate()
    {
        var sale = new Sale { TotalUSD = 1.15m, TotalBsS = 12m, AppliedRate = 10m };

        Assert.Equal(1.2m, sale.EffectiveTotalUSD);
    }

    [Fact]
    public void Sale_EffectiveTotalUSD_CapturesRoundingSurplusOverCanonicalUsd()
    {
        // 0.81 USD * 842.21 = 682.1901 -> techo canonico 682.20 (sobre-ganancia del redondeo).
        var sale = new Sale { TotalUSD = 0.81m, TotalBsS = 682.20m, AppliedRate = 842.21m };

        Assert.Equal(0.8100m, sale.EffectiveTotalUSD);
        Assert.True(sale.EffectiveTotalUSD >= sale.TotalUSD);
    }

    [Fact]
    public void Sale_EffectiveTotalUSD_WithoutBsSTotal_FallsBackToCanonicalUsd()
    {
        var sale = new Sale { TotalUSD = 1.15m, TotalBsS = 0m, AppliedRate = 10m };

        Assert.Equal(1.15m, sale.EffectiveTotalUSD);
    }

    [Fact]
    public void Sale_EffectiveTotalUSD_WithoutRate_FallsBackToCanonicalUsd()
    {
        var sale = new Sale { TotalUSD = 1.15m, TotalBsS = 12m, AppliedRate = 0m };

        Assert.Equal(1.15m, sale.EffectiveTotalUSD);
    }

    // ---------------------------------------------------------------------
    // Carrito (WPF): TotalUsdEffective es el inverso del Bs.S efectivo mostrado
    // ---------------------------------------------------------------------

    private static SaleDto LivePendingSale()
    {
        return new SaleDto
        {
            Id = 101,
            Status = "Pending",
            AppliedRate = 10m,
            Subtotal = 1.15m,
            TotalUSD = 1.15m,
            SubtotalBsS = 12m,
            TotalBsS = 12m,
            Items = new List<SaleItemDto>
            {
                new()
                {
                    Id = 1,
                    ProductId = 1,
                    ProductName = "Item 1",
                    Quantity = 1m,
                    UnitPrice = 1.15m,
                    Subtotal = 1.15m,
                    UnitPriceBsS = 12m,
                    SubtotalBsS = 12m,
                    RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyInteger
                }
            }
        };
    }

    [Fact]
    public void Cart_TotalUsdEffective_IsInverseOfEffectiveBsS_NotCanonicalUsdSum()
    {
        var mockSales = new Mock<ISalesService>();
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.SetupGet(r => r.CurrentRate).Returns(10m);

        var cart = new CartViewModel(mockSales.Object, mockRate.Object);
        WeakReferenceMessenger.Default.UnregisterAll(cart);
        cart.CurrentSale = LivePendingSale();

        // Regla cash Entero + toggle: 1.15 * 10 = 11.5 -> 12.00 Bs.S efectivos por linea.
        Assert.Equal(12m, cart.TotalAmountLocal);
        // El $ mostrado es el inverso del Bs.S cobrado: 12 / 10 = 1.20 (sobre-ganancia visible).
        Assert.Equal(1.2m, cart.TotalUsdEffective);
        // El TotalUSD canonico (suma de precios base) no se altera.
        Assert.Equal(1.15m, cart.TotalUSD);
    }

    [Fact]
    public void Cart_TotalUsdEffective_WithoutRate_FallsBackToCanonicalUsd()
    {
        var mockSales = new Mock<ISalesService>();
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.SetupGet(r => r.CurrentRate).Returns(0m);

        var sale = LivePendingSale();
        sale.AppliedRate = 0m;

        var cart = new CartViewModel(mockSales.Object, mockRate.Object);
        WeakReferenceMessenger.Default.UnregisterAll(cart);
        cart.CurrentSale = sale;

        Assert.Equal(cart.TotalUSD, cart.TotalUsdEffective);
    }

    // ---------------------------------------------------------------------
    // Checkout (WPF): total y restante derivados del Bs.S del servidor
    // ---------------------------------------------------------------------

    private static CheckoutPreviewClientDto BuildPreview(decimal totalBsS, IEnumerable<SalePaymentDto> payments)
    {
        var list = new List<SalePaymentDto>(payments);
        decimal paidBsS = list.Sum(p => p.AmountBsS);
        decimal paidUsd = list.Sum(p => p.Amount);
        decimal remainingBsS = System.Math.Max(0m, totalBsS - paidBsS);

        return new CheckoutPreviewClientDto
        {
            TotalUSD = 1.15m,
            TotalBsS = totalBsS,
            TotalPaidUSD = paidUsd,
            TotalPaidBsS = paidBsS,
            RemainingBalanceUSD = System.Math.Max(0m, 1.15m - paidUsd),
            RemainingBalanceBsS = remainingBsS,
            RoundingAdjustment = 0m,
            IsFullyPaid = remainingBsS <= 0.05m
        };
    }

    private static async Task<CheckoutViewModel> CreateCheckoutAsync(Mock<ISalesService> mockSales, SaleDto sale)
    {
        mockSales
            .Setup(s => s.GetCheckoutPreviewAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<IEnumerable<SalePaymentDto>>()))
            .ReturnsAsync((int _, decimal __, IEnumerable<SalePaymentDto> payments) => BuildPreview(12m, payments));

        var methods = new System.Collections.ObjectModel.ObservableCollection<PaymentMethodDto>
        {
            new() { Id = 1, Name = "Efectivo USD", IsCash = true, RequiresReference = false, IsActive = true },
            new() { Id = 2, Name = "Pago Movil Bs.S", IsCash = false, RequiresReference = true, IsActive = true }
        };

        var vm = new CheckoutViewModel(
            sale: sale,
            availableMethods: methods,
            salesService: mockSales.Object,
            currentExchangeRate: 10m);
        WeakReferenceMessenger.Default.UnregisterAll(vm);

        var pending = vm.PendingPreview;
        if (pending != null) await pending;
        return vm;
    }

    private static SaleDto CheckoutSale()
    {
        var sale = LivePendingSale();
        sale.Id = 1;
        return sale;
    }

    [Fact]
    public async Task Checkout_TotalAndRemainingUsdEffective_AreInverseOfServerBsS()
    {
        var mockSales = new Mock<ISalesService>();
        var vm = await CreateCheckoutAsync(mockSales, CheckoutSale());

        Assert.Equal(12m, vm.TotalAmountLocal);
        Assert.Equal(1.2m, vm.TotalUsdEffective);
        Assert.Equal(12m, vm.RemainingBalanceLocal);
        Assert.Equal(1.2m, vm.RemainingUsdEffective);
    }

    [Fact]
    public async Task Checkout_RemainingUsdEffective_TracksPartialBsSPayment()
    {
        var mockSales = new Mock<ISalesService>();
        var vm = await CreateCheckoutAsync(mockSales, CheckoutSale());

        vm.SelectedMethod = vm.AvailableMethods.First(m => m.Id == 2);
        vm.CurrentReference = "REF-1";
        vm.AmountBsSText = "5";
        vm.AddPaymentCommand.Execute(null);

        var pending = vm.PendingPreview;
        if (pending != null) await pending;

        Assert.Equal(7m, vm.RemainingBalanceLocal);
        Assert.Equal(0.7m, vm.RemainingUsdEffective);
        Assert.Equal(1.2m, vm.TotalUsdEffective);
    }

    // ---------------------------------------------------------------------
    // Historial (WPF): el "Total USD" del detalle muestra el efectivo, no el canonico
    // ---------------------------------------------------------------------

    [Fact]
    public void History_DetailTotalUsd_ShowsEffectiveInverseOfChargedBsS()
    {
        var mockSales = new Mock<ISalesService>();
        var vm = new SalesHistoryViewModel(mockSales.Object, act => act());

        vm.SelectedSale = new Desktop.Client.Services.SaleHistoryDto
        {
            Id = 55,
            AppliedRate = 10m,
            TotalUSD = 1.15m,
            TotalBsS = 12m
        };

        // Precarga inmediata del panel de detalle: 12 / 10 = 1.20 (no el canonico 1.15).
        Assert.Equal(1.2m, vm.DetailTotalUSD);
    }

    // ---------------------------------------------------------------------
    // Cuentas abiertas — Retiros (PendingPickupDto servidor + cliente)
    // ---------------------------------------------------------------------

    [Fact]
    public void PendingPickupDto_EffectiveTotalUSD_IsInverseOfRoundedBsS()
    {
        var pickup = new Sales.Module.DTOs.PendingPickupDto
        {
            TotalUSD = 1.15m,
            TotalBsS = 12m,
            AppliedRate = 10m
        };

        Assert.Equal(1.2m, pickup.EffectiveTotalUSD);
    }

    [Fact]
    public void PendingPickupDto_EffectiveTotalUSD_FallsBackToCanonicalWithoutRateOrBsS()
    {
        var withoutRate = new Sales.Module.DTOs.PendingPickupDto { TotalUSD = 1.15m, TotalBsS = 12m, AppliedRate = 0m };
        var withoutBsS = new Sales.Module.DTOs.PendingPickupDto { TotalUSD = 1.15m, TotalBsS = 0m, AppliedRate = 10m };

        Assert.Equal(1.15m, withoutRate.EffectiveTotalUSD);
        Assert.Equal(1.15m, withoutBsS.EffectiveTotalUSD);
    }

    [Fact]
    public void PendingPickupClientDto_EffectiveTotalUSD_IsInverseOfRoundedBsS()
    {
        var pickup = new PendingPickupClientDto
        {
            TotalUSD = 1.15m,
            TotalBsS = 12m,
            AppliedRate = 10m
        };

        Assert.Equal(1.2m, pickup.EffectiveTotalUSD);
    }
}
