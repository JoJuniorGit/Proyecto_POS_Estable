using System.Collections.Generic;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class CartRecoveryStateTests
{
    [Fact]
    public void UpdateCollection_WhenPendingSaleHasItems_SavesRecoverySnapshot()
    {
        var mockStore = new Mock<ISaleRecoveryStore>();
        using var cart = CreateCart(mockStore);

        cart.CurrentSale = CreateSale(101, "Pending", CreateItem(1));

        mockStore.Verify(s => s.Save(It.Is<SaleRecoverySnapshot>(snapshot =>
            snapshot.SaleId == 101 &&
            snapshot.ItemCount == 1 &&
            snapshot.Status == "Pending")), Times.Once);
    }

    [Fact]
    public void UpdateCollection_WhenCartHasNoItems_ClearsRecoverySnapshot()
    {
        var mockStore = new Mock<ISaleRecoveryStore>();
        using var cart = CreateCart(mockStore);

        cart.CurrentSale = CreateSale(102, "Pending");

        mockStore.Verify(s => s.Clear(), Times.Once);
    }

    [Fact]
    public void UpdateCollection_WhenSaleIsNotPending_ClearsRecoverySnapshot()
    {
        var mockStore = new Mock<ISaleRecoveryStore>();
        using var cart = CreateCart(mockStore);

        cart.CurrentSale = CreateSale(103, "Completed", CreateItem(1));

        mockStore.Verify(s => s.Clear(), Times.Once);
    }

    [Fact]
    public void HasUncommittedItems_ReflectsPendingSaleWithItems()
    {
        var mockStore = new Mock<ISaleRecoveryStore>();
        using var cart = CreateCart(mockStore);

        Assert.False(cart.HasUncommittedItems);

        cart.CurrentSale = CreateSale(104, "Pending");
        Assert.False(cart.HasUncommittedItems);

        cart.CurrentSale = CreateSale(105, "Pending", CreateItem(2));
        Assert.True(cart.HasUncommittedItems);
    }

    [Fact]
    public void PreserveRecoverySnapshotOnNextClear_ThenEmptyCart_DoesNotClear()
    {
        var mockStore = new Mock<ISaleRecoveryStore>();
        using var cart = CreateCart(mockStore);

        cart.CurrentSale = CreateSale(105, "Pending", CreateItem(1));

        mockStore.Verify(s => s.Save(It.IsAny<SaleRecoverySnapshot>()), Times.Once);

        cart.PreserveRecoverySnapshotOnNextClear();
        cart.CurrentSale = CreateSale(106, "Pending");

        mockStore.Verify(s => s.Clear(), Times.Never);

        cart.CurrentSale = CreateSale(107, "Pending");

        mockStore.Verify(s => s.Clear(), Times.Once);
    }

    private static CartViewModel CreateCart(Mock<ISaleRecoveryStore> store)
    {
        var mockSales = new Mock<ISalesService>();
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.SetupGet(r => r.CurrentRate).Returns(60.0m);
        return new CartViewModel(mockSales.Object, mockRate.Object, recoveryStore: store.Object);
    }

    private static SaleDto CreateSale(int id, string status, params SaleItemDto[] items) => new()
    {
        Id = id,
        Status = status,
        TotalUSD = items.Length * 25m,
        Items = new List<SaleItemDto>(items)
    };

    private static SaleItemDto CreateItem(int id) => new()
    {
        Id = id,
        ProductId = id,
        ProductName = $"Producto {id}",
        Quantity = 1,
        UnitPrice = 25m,
        Subtotal = 25m
    };
}
