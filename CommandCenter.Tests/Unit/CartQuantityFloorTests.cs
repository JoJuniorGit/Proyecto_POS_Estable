using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Piso de cantidad del carrito POS (paridad con el carrito web): la accion de disminuir (-)
/// nunca lleva la linea a 0 ni elimina; la edicion manual con 0/negativo revierte a 1; el
/// borrado queda exclusivamente en el boton 🗑 o la tecla Supr.
/// </summary>
public class CartQuantityFloorTests
{
    private const decimal Rate = 842.21m;

    private static CartViewModel CreateCart(Mock<ISalesService> mockSales, SaleDto sale)
    {
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.SetupGet(r => r.CurrentRate).Returns(Rate);
        var cart = new CartViewModel(mockSales.Object, mockRate.Object);
        // Aisla el carrito del bus global compartido entre pruebas (CurrentSaleChangedMessage).
        WeakReferenceMessenger.Default.UnregisterAll(cart);
        cart.CurrentSale = sale;
        return cart;
    }

    private static SaleDto PendingSale(params (int Id, decimal Quantity, Core.Entities.UnitOfMeasureType Uom)[] items)
    {
        var sale = new SaleDto
        {
            Id = 101,
            Status = "Pending",
            AppliedRate = Rate,
            Items = new List<SaleItemDto>()
        };

        foreach (var (id, quantity, uom) in items)
        {
            sale.Items.Add(new SaleItemDto
            {
                Id = id,
                ProductId = id,
                ProductName = $"Item {id}",
                Quantity = quantity,
                UnitOfMeasure = uom,
                UnitPrice = 1m,
                Subtotal = quantity,
                UnitPriceBsS = 842.21m,
                SubtotalBsS = 842.21m * quantity
            });
        }

        return sale;
    }

    [Fact]
    public async Task DecreaseQuantityCommand_WhenQuantityAtFloor_IgnoresActionAndNeverRemoves()
    {
        var mockSales = new Mock<ISalesService>();
        var cart = CreateCart(mockSales, PendingSale((1, 1m, Core.Entities.UnitOfMeasureType.Und)));

        await cart.DecreaseQuantityCommand.ExecuteAsync(cart.CartItems[0]);

        // En el piso (cantidad 1, paso 1) la resta se ignora: ni update ni eliminacion.
        mockSales.Verify(s => s.UpdateItemQuantityAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Never);
        mockSales.Verify(s => s.RemoveItemAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>()), Times.Never);
        Assert.Single(cart.CartItems);
        Assert.Equal(1m, cart.CartItems[0].Model.Quantity);
    }

    [Fact]
    public async Task DecreaseQuantityCommand_WhenQuantityAboveFloor_DecrementsOneStep()
    {
        var mockSales = new Mock<ISalesService>();
        var cart = CreateCart(mockSales, PendingSale((1, 2m, Core.Entities.UnitOfMeasureType.Und)));

        var updated = PendingSale((1, 1m, Core.Entities.UnitOfMeasureType.Und));
        mockSales.Setup(s => s.UpdateItemQuantityAsync(101, 1, 1m, Rate)).ReturnsAsync(updated);

        await cart.DecreaseQuantityCommand.ExecuteAsync(cart.CartItems[0]);

        mockSales.Verify(s => s.UpdateItemQuantityAsync(101, 1, 1m, Rate), Times.Once);
        mockSales.Verify(s => s.RemoveItemAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>()), Times.Never);
        Assert.Equal(1m, cart.CurrentSale!.Items[0].Quantity);
    }

    [Fact]
    public async Task DecreaseQuantityCommand_WhenFractionalAtOneStepFloor_IgnoresAction()
    {
        var mockSales = new Mock<ISalesService>();
        var cart = CreateCart(mockSales, PendingSale((1, 0.1m, Core.Entities.UnitOfMeasureType.Kg)));

        await cart.DecreaseQuantityCommand.ExecuteAsync(cart.CartItems[0]);

        mockSales.Verify(s => s.UpdateItemQuantityAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Never);
        mockSales.Verify(s => s.RemoveItemAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>()), Times.Never);
        Assert.Single(cart.CartItems);
    }

    [Fact]
    public async Task DecreaseQuantityCommand_WhenFractionalAboveOneStepFloor_DecrementsOneStep()
    {
        var mockSales = new Mock<ISalesService>();
        var cart = CreateCart(mockSales, PendingSale((1, 0.2m, Core.Entities.UnitOfMeasureType.Kg)));

        var updated = PendingSale((1, 0.1m, Core.Entities.UnitOfMeasureType.Kg));
        mockSales.Setup(s => s.UpdateItemQuantityAsync(101, 1, 0.1m, Rate)).ReturnsAsync(updated);

        await cart.DecreaseQuantityCommand.ExecuteAsync(cart.CartItems[0]);

        mockSales.Verify(s => s.UpdateItemQuantityAsync(101, 1, 0.1m, Rate), Times.Once);
        Assert.Equal(0.1m, cart.CurrentSale!.Items[0].Quantity);
    }

    [Fact]
    public async Task CommitItemQuantityAsync_WhenEditedToZero_RevertsToOneWithoutRemoving()
    {
        var mockSales = new Mock<ISalesService>();
        var cart = CreateCart(mockSales, PendingSale((1, 1m, Core.Entities.UnitOfMeasureType.Und)));

        var echoed = PendingSale((1, 1m, Core.Entities.UnitOfMeasureType.Und));
        mockSales.Setup(s => s.UpdateItemQuantityAsync(101, 1, 1m, Rate)).ReturnsAsync(echoed);

        await cart.CommitItemQuantityAsync(1, 0m);

        // Edicion manual protegida: el 0 confirmado revierte a 1 — nunca elimina la linea.
        mockSales.Verify(s => s.UpdateItemQuantityAsync(101, 1, 1m, Rate), Times.Once);
        mockSales.Verify(s => s.RemoveItemAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>()), Times.Never);
        Assert.Single(cart.CartItems);
        Assert.Equal(1m, cart.CartItems[0].Model.Quantity);
    }

    [Fact]
    public async Task CommitItemQuantityAsync_WhenEditedToNegative_RevertsToOneWithoutRemoving()
    {
        var mockSales = new Mock<ISalesService>();
        var cart = CreateCart(mockSales, PendingSale((1, 1m, Core.Entities.UnitOfMeasureType.Und)));

        var echoed = PendingSale((1, 1m, Core.Entities.UnitOfMeasureType.Und));
        mockSales.Setup(s => s.UpdateItemQuantityAsync(101, 1, 1m, Rate)).ReturnsAsync(echoed);

        await cart.CommitItemQuantityAsync(1, -2m);

        mockSales.Verify(s => s.UpdateItemQuantityAsync(101, 1, 1m, Rate), Times.Once);
        mockSales.Verify(s => s.RemoveItemAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>()), Times.Never);
        Assert.Single(cart.CartItems);
    }

    [Fact]
    public async Task CommitItemQuantityAsync_WhenEditedToPositive_UsesEditedValue()
    {
        var mockSales = new Mock<ISalesService>();
        var cart = CreateCart(mockSales, PendingSale((1, 1m, Core.Entities.UnitOfMeasureType.Und)));

        var updated = PendingSale((1, 5m, Core.Entities.UnitOfMeasureType.Und));
        mockSales.Setup(s => s.UpdateItemQuantityAsync(101, 1, 5m, Rate)).ReturnsAsync(updated);

        await cart.CommitItemQuantityAsync(1, 5m);

        mockSales.Verify(s => s.UpdateItemQuantityAsync(101, 1, 5m, Rate), Times.Once);
        Assert.Equal(5m, cart.CurrentSale!.Items[0].Quantity);
    }

    [Fact]
    public async Task FlushAllQuantitiesAsync_WhenPendingEditIsZero_RevertsToOneWithoutRemoving()
    {
        var mockSales = new Mock<ISalesService>();
        var cart = CreateCart(mockSales, PendingSale((1, 1m, Core.Entities.UnitOfMeasureType.Und)));

        // Borrador de edicion en 0 sin Enter: al confirmarlo (flush) debe revertir a 1.
        cart.CartItems[0].QuantityText = "0";

        var echoed = PendingSale((1, 1m, Core.Entities.UnitOfMeasureType.Und));
        mockSales.Setup(s => s.UpdateItemQuantityAsync(101, 1, 1m, Rate)).ReturnsAsync(echoed);
        mockSales.Setup(s => s.GetSaleAsync(101)).ReturnsAsync(echoed);

        await cart.FlushAllQuantitiesAsync();

        mockSales.Verify(s => s.UpdateItemQuantityAsync(101, 1, 1m, Rate), Times.Once);
        mockSales.Verify(s => s.RemoveItemAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>()), Times.Never);
        Assert.Single(cart.CartItems);
        Assert.Equal(1m, cart.CartItems[0].Model.Quantity);
    }

    [Fact]
    public async Task DecreaseQuantityCommand_SelectionsArePreservedAfterDecrement()
    {
        var mockSales = new Mock<ISalesService>();
        var cart = CreateCart(mockSales, PendingSale(
            (1, 2m, Core.Entities.UnitOfMeasureType.Und),
            (2, 3m, Core.Entities.UnitOfMeasureType.Und)));

        var updated = PendingSale(
            (1, 1m, Core.Entities.UnitOfMeasureType.Und),
            (2, 3m, Core.Entities.UnitOfMeasureType.Und));
        mockSales.Setup(s => s.UpdateItemQuantityAsync(101, 1, 1m, Rate)).ReturnsAsync(updated);

        cart.SelectedSaleItem = cart.CartItems.First(i => i.Id == 1);
        await cart.DecreaseQuantityCommand.ExecuteAsync(cart.CartItems.First(i => i.Id == 1));

        Assert.Equal(1, cart.SelectedSaleItem?.Id);
    }

    [Fact]
    public void CanDecrease_TracksStepFloor_AndNotifiesOnQuantityChange()
    {
        var mockSales = new Mock<ISalesService>();
        var cart = CreateCart(mockSales, PendingSale(
            (1, 1m, Core.Entities.UnitOfMeasureType.Und),
            (2, 0.1m, Core.Entities.UnitOfMeasureType.Kg)));

        var unitItem = cart.CartItems.First(i => i.Id == 1);
        var kgItem = cart.CartItems.First(i => i.Id == 2);

        // En el piso de un paso la resta queda deshabilitada (paridad con isAtMin del carrito web).
        Assert.False(unitItem.CanDecrease);
        Assert.False(kgItem.CanDecrease);

        var raised = new List<string>();
        unitItem.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CartItemViewModel.CanDecrease)) raised.Add(e.PropertyName!);
        };

        unitItem.QuantityText = "2";
        Assert.True(unitItem.CanDecrease);

        kgItem.QuantityText = "0.2";
        Assert.True(kgItem.CanDecrease);

        Assert.Contains(nameof(CartItemViewModel.CanDecrease), raised);
    }
}
