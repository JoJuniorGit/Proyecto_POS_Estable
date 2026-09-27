using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;
using CommunityToolkit.Mvvm.Messaging;

namespace CommandCenter.Tests.Unit;

public class PosViewModelRecoveryTests
{
    [Fact]
    public async Task InitializeForSessionAsync_WhenSnapshotExistsAndUserConfirms_RecoversSaleWithoutStartingNew()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var mockDialog = new Mock<IDialogService>();
        var mockStore = new Mock<ISaleRecoveryStore>();

        mockRate.SetupGet(r => r.CurrentRate).Returns(60.0m);
        mockPayments.Setup(p => p.GetActiveMethodsAsync())
            .ReturnsAsync(new List<PaymentMethodDto> { new() { Id = 1, Name = "Efectivo", IsCash = true } });

        mockStore.Setup(s => s.Load()).Returns(CreateSnapshot(55, 2));
        mockDialog.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        mockSales.Setup(s => s.GetSaleAsync(55)).ReturnsAsync(CreateSale(55, "Pending", 2));
        mockSales.Setup(s => s.StartSaleAsync(It.IsAny<int?>())).ReturnsAsync(CreateSale(100, "Draft", 0));

        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Cajero", Role = UserRole.Cashier }, "token");

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
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
        // Aísla también el carrito del bus global (CurrentSaleChangedMessage de otras pruebas).
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);

        await posVm.InitializeForSessionAsync();

        Assert.NotNull(posVm.Cart.CurrentSale);
        Assert.Equal(55, posVm.Cart.CurrentSale!.Id);
        mockSales.Verify(s => s.GetSaleAsync(55), Times.Once);
        mockSales.Verify(s => s.StartSaleAsync(It.IsAny<int?>()), Times.Never);
        mockStore.Verify(s => s.Clear(), Times.Never);
    }

    [Fact]
    public async Task InitializeForSessionAsync_WhenSnapshotExistsAndUserRejects_ClearsSnapshotAndStartsNewSale()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var mockDialog = new Mock<IDialogService>();
        var mockStore = new Mock<ISaleRecoveryStore>();

        mockRate.SetupGet(r => r.CurrentRate).Returns(60.0m);
        mockPayments.Setup(p => p.GetActiveMethodsAsync())
            .ReturnsAsync(new List<PaymentMethodDto> { new() { Id = 1, Name = "Efectivo", IsCash = true } });

        mockStore.Setup(s => s.Load()).Returns(CreateSnapshot(55, 2));
        mockDialog.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
        mockSales.Setup(s => s.StartSaleAsync(It.IsAny<int?>())).ReturnsAsync(CreateSale(100, "Draft", 0));

        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Cajero", Role = UserRole.Cashier }, "token");

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
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
        // Aísla también el carrito del bus global (CurrentSaleChangedMessage de otras pruebas).
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);

        await posVm.InitializeForSessionAsync();

        mockStore.Verify(s => s.Clear(), Times.AtLeastOnce());
        mockSales.Verify(s => s.GetSaleAsync(It.IsAny<int>()), Times.Never);
        mockSales.Verify(s => s.StartSaleAsync(It.IsAny<int?>()), Times.Once);
    }

    [Fact]
    public async Task InitializeForSessionAsync_WhenRecoveryFetchFails_KeepsSnapshotAndDoesNotStartNewSale()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var mockDialog = new Mock<IDialogService>();
        var mockStore = new Mock<ISaleRecoveryStore>();

        mockRate.SetupGet(r => r.CurrentRate).Returns(60.0m);
        mockPayments.Setup(p => p.GetActiveMethodsAsync())
            .ReturnsAsync(new List<PaymentMethodDto> { new() { Id = 1, Name = "Efectivo", IsCash = true } });

        mockStore.Setup(s => s.Load()).Returns(CreateSnapshot(55, 2));
        mockDialog.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        mockSales.Setup(s => s.GetSaleAsync(55)).ThrowsAsync(new HttpRequestException("Sin conexion con el servidor"));
        mockSales.Setup(s => s.StartSaleAsync(It.IsAny<int?>())).ReturnsAsync(CreateSale(100, "Pending", 0));

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
        // Aísla también el carrito del bus global (CurrentSaleChangedMessage de otras pruebas).
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);

        await posVm.InitializeForSessionAsync();

        mockSales.Verify(s => s.GetSaleAsync(55), Times.Once);
        mockDialog.Verify(d => d.ShowWarning(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        mockStore.Verify(s => s.Clear(), Times.Never);
        mockSales.Verify(s => s.StartSaleAsync(It.IsAny<int?>()), Times.Never);
        Assert.Null(posVm.Cart.CurrentSale);
    }

    [Fact]
    public void ResetSession_WithPendingCartItems_PreservesRecoverySnapshot()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var mockDialog = new Mock<IDialogService>();
        var mockStore = new Mock<ISaleRecoveryStore>();

        mockRate.SetupGet(r => r.CurrentRate).Returns(60.0m);

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object, recoveryStore: mockStore.Object);
        using var posVm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            null,
            mockDialog.Object,
            null,
            mockStore.Object);

        WeakReferenceMessenger.Default.Unregister<PaymentMethodsChangedMessage>(posVm);
        // Aísla también el carrito del bus global (CurrentSaleChangedMessage de otras pruebas).
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);

        cartVm.CurrentSale = CreateSale(77, "Pending", 1);

        mockStore.Verify(s => s.Save(It.Is<SaleRecoverySnapshot>(snapshot => snapshot.SaleId == 77)), Times.Once);

        posVm.ResetSession();

        mockStore.Verify(s => s.Clear(), Times.Never);
    }

    [Fact]
    public async Task InitializeForSessionAsync_WhenSnapshotSaleIsNotRecoverable_ClearsSnapshotAndStartsNewSale()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var mockDialog = new Mock<IDialogService>();
        var mockStore = new Mock<ISaleRecoveryStore>();

        mockRate.SetupGet(r => r.CurrentRate).Returns(60.0m);
        mockPayments.Setup(p => p.GetActiveMethodsAsync())
            .ReturnsAsync(new List<PaymentMethodDto> { new() { Id = 1, Name = "Efectivo", IsCash = true } });

        mockStore.Setup(s => s.Load()).Returns(CreateSnapshot(55, 2));
        mockDialog.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        mockSales.Setup(s => s.GetSaleAsync(55)).ReturnsAsync(CreateSale(55, "Completed", 2));
        mockSales.Setup(s => s.StartSaleAsync(It.IsAny<int?>())).ReturnsAsync(CreateSale(100, "Draft", 0));

        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Cajero", Role = UserRole.Cashier }, "token");

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
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
        // Aísla también el carrito del bus global (CurrentSaleChangedMessage de otras pruebas).
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);

        await posVm.InitializeForSessionAsync();

        mockStore.Verify(s => s.Clear(), Times.AtLeastOnce());
        mockSales.Verify(s => s.StartSaleAsync(It.IsAny<int?>()), Times.Once);
    }

    [Fact]
    public async Task InitializeForSessionAsync_WhenNoSnapshot_StartsNewSale()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var mockDialog = new Mock<IDialogService>();
        var mockStore = new Mock<ISaleRecoveryStore>();

        mockRate.SetupGet(r => r.CurrentRate).Returns(60.0m);
        mockPayments.Setup(p => p.GetActiveMethodsAsync())
            .ReturnsAsync(new List<PaymentMethodDto> { new() { Id = 1, Name = "Efectivo", IsCash = true } });

        mockStore.Setup(s => s.Load()).Returns((SaleRecoverySnapshot?)null);
        mockSales.Setup(s => s.StartSaleAsync(It.IsAny<int?>())).ReturnsAsync(CreateSale(100, "Draft", 0));

        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Cajero", Role = UserRole.Cashier }, "token");

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
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
        // Aísla también el carrito del bus global (CurrentSaleChangedMessage de otras pruebas).
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);

        await posVm.InitializeForSessionAsync();

        mockSales.Verify(s => s.StartSaleAsync(It.IsAny<int?>()), Times.Once);
        mockDialog.Verify(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        mockStore.Verify(s => s.Clear(), Times.Never);
    }

    private static SaleRecoverySnapshot CreateSnapshot(int saleId, int itemCount) => new()
    {
        SaleId = saleId,
        ItemCount = itemCount,
        Status = "Pending",
        TotalUSD = itemCount * 25m,
        SavedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
    };

    private static SaleDto CreateSale(int id, string status, int itemCount)
    {
        var sale = new SaleDto { Id = id, Status = status, TotalUSD = itemCount * 25m };
        for (int i = 1; i <= itemCount; i++)
        {
            sale.Items.Add(new SaleItemDto
            {
                Id = i,
                ProductId = i,
                ProductName = $"Producto {i}",
                Quantity = 1,
                UnitPrice = 25m,
                Subtotal = 25m
            });
        }

        return sale;
    }
}
