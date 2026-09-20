using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Desktop.Client.Helpers;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class CartTotalsDesyncTests
{
    private const decimal Rate = 842.21m;

    private static CartViewModel CreateCart(Mock<ISalesService> mockSales, Mock<IExchangeRateService> mockRate, SaleDto sale)
    {
        mockRate.SetupGet(r => r.CurrentRate).Returns(Rate);
        var cart = new CartViewModel(mockSales.Object, mockRate.Object);
        // Aísla el carrito del bus global compartido entre pruebas (CurrentSaleChangedMessage).
        WeakReferenceMessenger.Default.UnregisterAll(cart);
        cart.CurrentSale = sale;
        return cart;
    }

    private static SaleDto PendingSaleItem(int itemId, decimal unitPrice)
    {
        decimal unitPriceBsS = PricingHelper.ToBsSCeiling(unitPrice, Rate);
        decimal subtotalBsS = PricingHelper.RoundToDigital(unitPriceBsS);
        return new SaleDto
        {
            Id = 101,
            Status = "Pending",
            AppliedRate = Rate,
            Items = new System.Collections.Generic.List<SaleItemDto>
            {
                new()
                {
                    Id = itemId,
                    ProductId = itemId,
                    ProductName = $"Item {itemId}",
                    Quantity = 1m,
                    UnitPrice = unitPrice,
                    Subtotal = unitPrice,
                    UnitPriceBsS = unitPriceBsS,
                    SubtotalBsS = subtotalBsS
                }
            }
        };
    }

    [Fact]
    public void TotalAmountLocal_PendingSaleWithoutLocalEdits_MatchesBackendTotals()
    {
        var mockSales = new Mock<ISalesService>();
        var mockRate = new Mock<IExchangeRateService>();
        var sale = PendingSaleItem(1, 1m);
        sale.Subtotal = 1m;
        sale.TotalUSD = 1m;
        sale.SubtotalBsS = 842.21m;
        sale.TotalBsS = 842.21m;

        var cart = CreateCart(mockSales, mockRate, sale);

        Assert.Equal(842.21m, cart.TotalAmountLocal);
        Assert.Equal(842.21m, cart.SubtotalLocal);
    }

    [Fact]
    public void RecalculateTotals_QuantityEditedLocally_TotalGeneralShowsLivePreview()
    {
        var mockSales = new Mock<ISalesService>();
        var mockRate = new Mock<IExchangeRateService>();
        var sale = PendingSaleItem(1, 1m);
        sale.Subtotal = 1m;
        sale.TotalUSD = 1m;
        sale.SubtotalBsS = 842.21m;
        sale.TotalBsS = 842.21m;

        var cart = CreateCart(mockSales, mockRate, sale);

        cart.CartItems[0].QuantityText = "2";

        Assert.Equal(1684.42m, cart.TotalAmountLocal);
        Assert.Equal(1684.42m, cart.SubtotalLocal);
        Assert.Equal(1684.42m, cart.CartItems[0].SubtotalBsS);
        Assert.Equal(842.21m, cart.CartItems[0].UnitPriceBsS);
    }

    [Fact]
    public void RecalculateTotals_QuantityEditedLocally_BackendSnapshotStaysImmutable()
    {
        var mockSales = new Mock<ISalesService>();
        var mockRate = new Mock<IExchangeRateService>();
        var sale = PendingSaleItem(1, 1m);
        sale.Subtotal = 1m;
        sale.TotalUSD = 1m;
        sale.SubtotalBsS = 842.21m;
        sale.TotalBsS = 842.21m;

        var cart = CreateCart(mockSales, mockRate, sale);

        cart.CartItems[0].QuantityText = "3";

        Assert.Equal(842.21m, cart.CurrentSale!.TotalBsS);
        Assert.Equal(842.21m, cart.CurrentSale!.SubtotalBsS);
    }

    [Fact]
    public void TotalAmountLocal_HistoricalCompletedSale_UsesPersistedSnapshot()
    {
        var mockSales = new Mock<ISalesService>();
        var mockRate = new Mock<IExchangeRateService>();
        var sale = PendingSaleItem(1, 1m);
        sale.Status = "Completed";
        sale.Subtotal = 1m;
        sale.TotalUSD = 1m;
        sale.SubtotalBsS = 500m;
        sale.TotalBsS = 500m;

        var cart = CreateCart(mockSales, mockRate, sale);

        cart.CartItems[0].QuantityText = "2";

        Assert.Equal(500m, cart.TotalAmountLocal);
        Assert.Equal(500m, cart.SubtotalLocal);
    }

    [Fact]
    public async Task RemoveItemCommand_WhenItemRemoved_TotalsReflectRemainingItems()
    {
        var mockSales = new Mock<ISalesService>();
        var mockRate = new Mock<IExchangeRateService>();
        var sale = new SaleDto
        {
            Id = 101,
            Status = "Pending",
            AppliedRate = Rate,
            Subtotal = 3m,
            TotalUSD = 3m,
            SubtotalBsS = 2526.63m,
            TotalBsS = 2526.63m,
            Items = new System.Collections.Generic.List<SaleItemDto>
            {
                new()
                {
                    Id = 1,
                    ProductId = 1,
                    ProductName = "Item 1",
                    Quantity = 1m,
                    UnitPrice = 1m,
                    Subtotal = 1m,
                    UnitPriceBsS = 842.21m,
                    SubtotalBsS = 842.21m
                },
                new()
                {
                    Id = 2,
                    ProductId = 2,
                    ProductName = "Item 2",
                    Quantity = 1m,
                    UnitPrice = 2m,
                    Subtotal = 2m,
                    UnitPriceBsS = 1684.42m,
                    SubtotalBsS = 1684.42m
                }
            }
        };

        var afterRemove = new SaleDto
        {
            Id = 101,
            Status = "Pending",
            AppliedRate = Rate,
            Subtotal = 2m,
            TotalUSD = 2m,
            SubtotalBsS = 1684.42m,
            TotalBsS = 1684.42m,
            Items = new System.Collections.Generic.List<SaleItemDto>
            {
                new()
                {
                    Id = 2,
                    ProductId = 2,
                    ProductName = "Item 2",
                    Quantity = 1m,
                    UnitPrice = 2m,
                    Subtotal = 2m,
                    UnitPriceBsS = 1684.42m,
                    SubtotalBsS = 1684.42m
                }
            }
        };

        mockSales.Setup(s => s.RemoveItemAsync(101, 1, It.IsAny<decimal>())).ReturnsAsync(afterRemove);

        var cart = CreateCart(mockSales, mockRate, sale);

        await cart.RemoveItemCommand.ExecuteAsync(cart.CartItems[0]);

        Assert.Single(cart.CartItems);
        Assert.Equal(1684.42m, cart.TotalAmountLocal);
        Assert.Equal(1684.42m, cart.SubtotalLocal);
        Assert.Equal(2m, cart.TotalUSD);
    }
}