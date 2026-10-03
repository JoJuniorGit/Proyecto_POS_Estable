using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Bug de hidratación: al recuperar un pedido Pending tras un cierre inesperado, el carrito
/// debe derivar los Bs.S de la tasa VIGENTE (rate-first, igual que UpdateAllPrices cuando la
/// tasa cambia en runtime). Antes del fix, UpdateCollection priorizaba el AppliedRate
/// persistido (tasa vieja "A") y la tabla quedaba desincronizada respecto al modal de cobro,
/// que recalcula con la tasa nueva "B".
/// </summary>
public class CartRecoveryRateHydrationTests
{
    private static CartViewModel CreateCart(decimal currentRate, SaleDto sale)
    {
        var mockSales = new Mock<ISalesService>();
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.SetupGet(r => r.CurrentRate).Returns(currentRate);
        var cart = new CartViewModel(mockSales.Object, mockRate.Object);
        // Aísla el carrito del bus global compartido entre pruebas (CurrentSaleChangedMessage).
        WeakReferenceMessenger.Default.UnregisterAll(cart);
        cart.CurrentSale = sale;
        return cart;
    }

    private static SaleDto CreatePendingSale(decimal appliedRate, decimal unitPrice, decimal quantity)
    {
        var sale = new SaleDto
        {
            Id = 101,
            Status = "Pending",
            AppliedRate = appliedRate,
            TotalUSD = quantity * unitPrice,
            Subtotal = quantity * unitPrice
        };
        sale.Items.Add(new SaleItemDto
        {
            Id = 1,
            ProductId = 1,
            ProductName = "Producto recuperado",
            Quantity = quantity,
            UnitPrice = unitPrice,
            Subtotal = quantity * unitPrice
        });
        return sale;
    }

    [Fact]
    public void UpdateCollection_WhenRecoveredPendingSaleHasStaleAppliedRate_DerivesBsPricesWithCurrentRate()
    {
        // Tasa vieja "A" al momento del autoguardado; tasa vigente "B" al recuperar.
        const decimal staleRate = 50m;
        const decimal currentRate = 60m;
        var sale = CreatePendingSale(staleRate, unitPrice: 10m, quantity: 2m);
        // Snapshots persistidos por el backend con la tasa vieja (A).
        sale.Items[0].UnitPriceBsS = 500m;
        sale.Items[0].SubtotalBsS = 1000m;
        sale.SubtotalBsS = 1000m;
        sale.TotalBsS = 1000m;

        var cart = CreateCart(currentRate, sale);

        Assert.Equal(600m, cart.CartItems[0].UnitPriceBsS);
        Assert.Equal(1200m, cart.CartItems[0].SubtotalBsS);
        Assert.Equal(1200m, cart.SubtotalLocal);
        Assert.Equal(1200m, cart.TotalAmountLocal);
    }

    [Fact]
    public void UpdateCollection_WhenRecoveredPendingSaleHasFractionalPrice_AppliesCeilingRoundingLikeAddItem()
    {
        // Misma lógica que agregar un producto: ToBsSCeiling por unidad y suma por cantidad.
        // 0.81 * 842.21 = 682.1901 -> 682.20 (ceiling); qty 2 -> 1364.40.
        var sale = CreatePendingSale(appliedRate: 800m, unitPrice: 0.81m, quantity: 2m);

        var cart = CreateCart(currentRate: 842.21m, sale);

        Assert.Equal(682.20m, cart.CartItems[0].UnitPriceBsS);
        Assert.Equal(1364.40m, cart.CartItems[0].SubtotalBsS);
        Assert.Equal(1364.40m, cart.TotalAmountLocal);
    }

    [Fact]
    public void UpdateCollection_WhenCurrentRateNotLoaded_FallsBackToPersistedAppliedRate()
    {
        // Arranque offline: sin tasa vigente disponible se conserva el snapshot (tasa A).
        var sale = CreatePendingSale(appliedRate: 50m, unitPrice: 10m, quantity: 1m);
        sale.Items[0].UnitPriceBsS = 500m;
        sale.Items[0].SubtotalBsS = 500m;
        sale.SubtotalBsS = 500m;
        sale.TotalBsS = 500m;

        var cart = CreateCart(currentRate: 0m, sale);

        Assert.Equal(500m, cart.CartItems[0].UnitPriceBsS);
        Assert.Equal(500m, cart.CartItems[0].SubtotalBsS);
        Assert.Equal(500m, cart.TotalAmountLocal);
    }

    [Fact]
    public void UpdateCollection_WhenSaleIsHistorical_UsesPersistedSnapshotRegardlessOfCurrentRate()
    {
        var sale = CreatePendingSale(appliedRate: 50m, unitPrice: 10m, quantity: 2m);
        sale.Status = "Completed";
        sale.Items[0].UnitPriceBsS = 500m;
        sale.Items[0].SubtotalBsS = 1000m;
        sale.SubtotalBsS = 1000m;
        sale.TotalBsS = 1000m;

        var cart = CreateCart(currentRate: 60m, sale);

        Assert.Equal(500m, cart.CartItems[0].UnitPriceBsS);
        Assert.Equal(1000m, cart.CartItems[0].SubtotalBsS);
        Assert.Equal(1000m, cart.TotalAmountLocal);
    }

    [Fact]
    public void UpdateCollection_WhenRecoveredPendingSaleHasStaleAppliedRate_KeepsPersistedSnapshotImmutable()
    {
        var sale = CreatePendingSale(appliedRate: 50m, unitPrice: 10m, quantity: 2m);
        sale.Items[0].UnitPriceBsS = 500m;
        sale.Items[0].SubtotalBsS = 1000m;
        sale.SubtotalBsS = 1000m;
        sale.TotalBsS = 1000m;

        _ = CreateCart(currentRate: 60m, sale);

        // El snapshot del backend no se sobreescribe en el cliente (historial inmutable).
        Assert.Equal(500m, sale.Items[0].UnitPriceBsS);
        Assert.Equal(1000m, sale.Items[0].SubtotalBsS);
        Assert.Equal(1000m, sale.SubtotalBsS);
        Assert.Equal(1000m, sale.TotalBsS);
    }

    [Fact]
    public async Task InitializeForSessionAsync_WhenRecoveringOrphanSale_DerivesCartBsPricesWithCurrentRate()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var mockDialog = new Mock<IDialogService>();
        var mockStore = new Mock<ISaleRecoveryStore>();

        mockRate.SetupGet(r => r.CurrentRate).Returns(60m);
        mockPayments.Setup(p => p.GetActiveMethodsAsync())
            .ReturnsAsync(new List<PaymentMethodDto> { new() { Id = 1, Name = "Efectivo", IsCash = true } });

        var recovered = CreatePendingSale(appliedRate: 50m, unitPrice: 10m, quantity: 2m);
        recovered.Items[0].UnitPriceBsS = 500m;
        recovered.Items[0].SubtotalBsS = 1000m;
        recovered.SubtotalBsS = 1000m;
        recovered.TotalBsS = 1000m;

        mockStore.Setup(s => s.Load()).Returns(new SaleRecoverySnapshot
        {
            SaleId = recovered.Id,
            ItemCount = 1,
            Status = "Pending",
            TotalUSD = 20m,
            SavedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        });
        mockDialog.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        mockSales.Setup(s => s.GetSaleAsync(recovered.Id)).ReturnsAsync(recovered);

        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Cajero", Role = UserRole.Cashier }, "token");

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object, recoveryStore: mockStore.Object);
        using var posVm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            session,
            mockDialog.Object,
            null,
            mockStore.Object);

        WeakReferenceMessenger.Default.Unregister<PaymentMethodsChangedMessage>(posVm);
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);

        await posVm.InitializeForSessionAsync();

        Assert.Equal(recovered.Id, posVm.Cart.CurrentSale!.Id);
        Assert.Equal(600m, posVm.Cart.CartItems[0].UnitPriceBsS);
        Assert.Equal(1200m, posVm.Cart.CartItems[0].SubtotalBsS);
        Assert.Equal(1200m, posVm.Cart.TotalAmountLocal);
    }
}
