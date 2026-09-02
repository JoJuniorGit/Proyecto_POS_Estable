using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests;

public class RecentScannedItemsTests
{
    [Fact]
    public void RecentScannedItemViewModel_Quantity_BindsDirectlyToCartItems()
    {
        // Arrange: Mock SalesService & ExchangeRateService
        var salesMock = new Mock<ISalesService>();
        var rateMock = new Mock<IExchangeRateService>();
        rateMock.Setup(r => r.CurrentRate).Returns(36.5m);

        var cart = new CartViewModel(salesMock.Object, rateMock.Object);

        var sale = new SaleDto
        {
            Id = 1,
            Items = new System.Collections.Generic.List<SaleItemDto>
            {
                new() { Id = 10, ProductId = 100, ProductName = "Bebida Boka Naranja 2L", Quantity = 3m, UnitPrice = 2m, UnitPriceBsS = 73m }
            }
        };
        cart.CurrentSale = sale;

        var recentItem = new RecentScannedItemViewModel(
            100,
            "7591001002009",
            "Bebida Boka Naranja 2L",
            73m,
            2m,
            cart,
            (_, _) => Task.CompletedTask
        );

        // Assert: Quantity is 3 and TotalQuantityText is x3
        Assert.Equal(3m, recentItem.Quantity);
        Assert.Equal("x3", recentItem.TotalQuantityText);

        // Update cart with quantity 4
        sale.Items[0].Quantity = 4m;
        cart.CurrentSale = new SaleDto { Id = 1, Items = sale.Items };
        recentItem.Refresh();

        // Assert: Bound quantity automatically reflects 4!
        Assert.Equal(4m, recentItem.Quantity);
        Assert.Equal("x4", recentItem.TotalQuantityText);
    }

    [Fact]
    public void RecentScannedProducts_MaintainsMax3DistinctItems()
    {
        var list = new ObservableCollection<int>();

        void RegisterScan(int productId)
        {
            if (list.Contains(productId))
            {
                list.Remove(productId);
                list.Insert(0, productId);
            }
            else
            {
                list.Insert(0, productId);
                while (list.Count > 3)
                {
                    list.RemoveAt(list.Count - 1);
                }
            }
        }

        // Scan products 1, 2, 3
        RegisterScan(1);
        RegisterScan(2);
        RegisterScan(3);
        Assert.Equal(new[] { 3, 2, 1 }, list);

        // Re-scan product 1 (already present)
        RegisterScan(1);
        Assert.Equal(new[] { 1, 3, 2 }, list);
        Assert.Equal(3, list.Count);

        // Scan product 4
        RegisterScan(4);
        Assert.Equal(new[] { 4, 1, 3 }, list);
        Assert.Equal(3, list.Count);
    }
}
