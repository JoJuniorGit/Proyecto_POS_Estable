using System;
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

public class PosSearchSuggestionsTests
{
    [Fact]
    public async Task AddSelectedSuggestionCommand_WithValidProduct_AddsItemToSaleAndClearsSearch()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        var session = new UserSession();
        // Aísla el carrito del bus global compartido entre pruebas (CurrentSaleChangedMessage).
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);

        var existingSale = new SaleDto
        {
            Id = 10,
            CashierId = 1,
            TotalUSD = 0,
            Items = new System.Collections.Generic.List<SaleItemDto>()
        };
        cartVm.CurrentSale = existingSale;

        mockSales.Setup(s => s.AddItemAsync(10, 42, 1, It.IsAny<decimal>(), null, null))
            .ReturnsAsync(new SaleDto
            {
                Id = 10,
                CashierId = 1,
                TotalUSD = 5,
                Items = new System.Collections.Generic.List<SaleItemDto>
                {
                    new SaleItemDto { ProductId = 42, Quantity = 1, UnitPrice = 5, Subtotal = 5 }
                }
            });

        var vm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            session);

        var product = new ProductQuickInfoDto { Id = 42, Name = "Refresco 2L", SKU = "REF002", PriceUSD = 5 };
        vm.SearchText = "Refresco";
        vm.Suggestions.Add(product);
        vm.HasSuggestions = true;

        await vm.AddSelectedSuggestionCommand.ExecuteAsync(product);

        mockSales.Verify(s => s.AddItemAsync(10, 42, 1, It.IsAny<decimal>(), null, null), Times.Once);
        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Empty(vm.Suggestions);
        Assert.False(vm.HasSuggestions);
        Assert.Null(vm.SelectedSuggestion);
    }

    [Fact]
    public async Task AddSelectedSuggestion_WithPendingQuantityEdit_CommitsEditBeforeAddingItem()
    {
        // Reproducción del bug de sincronización UI/Modelo: cantidad 10 tipeada (sin Enter) en el
        // Producto A; al agregar el Producto secundario, la edición debe commitearse ANTES del alta
        // y sobrevivir a la reconstrucción de la colección desde la respuesta del servidor.
        const decimal Rate = 842.21m;
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.SetupGet(r => r.CurrentRate).Returns(Rate);

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        var session = new UserSession();
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);

        // Servidor simulado con estado mutable: el alta devuelve lo que el servidor tiene al momento.
        decimal serverQuantityA = 1m;

        SaleDto BuildServerSale(bool withSecondaryItem)
        {
            var sale = new SaleDto
            {
                Id = 10,
                Status = "Pending",
                AppliedRate = Rate,
                TotalUSD = serverQuantityA,
                Items = new System.Collections.Generic.List<SaleItemDto>
                {
                    new() { Id = 1, ProductId = 1, ProductName = "Producto A", Quantity = serverQuantityA, UnitPrice = 1m, Subtotal = serverQuantityA, UnitPriceBsS = Rate, SubtotalBsS = Rate * serverQuantityA }
                }
            };
            if (withSecondaryItem)
            {
                sale.Items.Add(new SaleItemDto { Id = 2, ProductId = 42, ProductName = "Refresco 2L", Quantity = 1m, UnitPrice = 5m, Subtotal = 5m, UnitPriceBsS = 4211.05m, SubtotalBsS = 4211.05m });
            }
            return sale;
        }

        cartVm.CurrentSale = BuildServerSale(withSecondaryItem: false);
        cartVm.CartItems[0].QuantityText = "10";

        mockSales.Setup(s => s.UpdateSaleItemsAsync(10, It.IsAny<IEnumerable<UpdateSaleItemDto>>(), Rate))
                 .Callback(() => serverQuantityA = 10m)
                 .Returns(Task.CompletedTask);
        mockSales.Setup(s => s.GetSaleAsync(10))
                 .ReturnsAsync(() => BuildServerSale(withSecondaryItem: false));
        mockSales.Setup(s => s.AddItemAsync(10, 42, 1, It.IsAny<decimal>(), null, null))
                 .ReturnsAsync(() => BuildServerSale(withSecondaryItem: true));

        var vm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            session);

        var product = new ProductQuickInfoDto { Id = 42, Name = "Refresco 2L", SKU = "REF002", PriceUSD = 5 };
        vm.SearchText = "Refresco";
        vm.Suggestions.Add(product);
        vm.HasSuggestions = true;

        await vm.AddSelectedSuggestionCommand.ExecuteAsync(product);

        mockSales.Verify(
            s => s.UpdateSaleItemsAsync(
                10,
                It.Is<IEnumerable<UpdateSaleItemDto>>(items => items.Single().Quantity == 10m),
                Rate),
            Times.Once);
        Assert.Equal(10m, cartVm.CartItems.Single(i => i.Id == 1).Model.Quantity);
        Assert.Equal(2, cartVm.CartItems.Count);
    }

    [Fact]
    public async Task AddSelectedSuggestionCommand_WithDummyNotFoundProduct_DoesNotAddItem()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        var session = new UserSession();
        // Aísla el carrito del bus global compartido entre pruebas (CurrentSaleChangedMessage).
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);

        var existingSale = new SaleDto { Id = 10, CashierId = 1 };
        cartVm.CurrentSale = existingSale;

        var vm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            session);

        var dummyItem = new ProductQuickInfoDto { Id = -1, Name = "Producto no encontrado" };
        vm.SearchText = "NonExistent";
        vm.Suggestions.Add(dummyItem);
        vm.HasSuggestions = true;

        await vm.AddSelectedSuggestionCommand.ExecuteAsync(dummyItem);

        mockSales.Verify(s => s.AddItemAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal?>(), It.IsAny<decimal?>()), Times.Never);
        Assert.Equal("NonExistent", vm.SearchText);
        Assert.True(vm.HasSuggestions);
    }

    [Fact]
    public void SelectedSuggestion_SetterChanged_DoesNotAutomaticallyAddItem()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        var session = new UserSession();
        // Aísla el carrito del bus global compartido entre pruebas (CurrentSaleChangedMessage).
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);

        var existingSale = new SaleDto { Id = 10, CashierId = 1 };
        cartVm.CurrentSale = existingSale;

        var vm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            session);

        var product = new ProductQuickInfoDto { Id = 99, Name = "Galletas", SKU = "GAL01" };
        vm.SearchText = "Galletas";
        vm.Suggestions.Add(product);
        vm.HasSuggestions = true;

        vm.SelectedSuggestion = product;

        mockSales.Verify(s => s.AddItemAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal?>(), It.IsAny<decimal?>()), Times.Never);
        Assert.Equal("Galletas", vm.SearchText);
        Assert.True(vm.HasSuggestions);
        Assert.Equal(product, vm.SelectedSuggestion);
    }

    [Fact]
    public async Task AddSelectedSuggestionCommand_WithNullParameter_UsesSelectedSuggestionFallback()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        var session = new UserSession();
        // Aísla el carrito del bus global compartido entre pruebas (CurrentSaleChangedMessage).
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);

        var existingSale = new SaleDto { Id = 10, CashierId = 1 };
        cartVm.CurrentSale = existingSale;

        mockSales.Setup(s => s.AddItemAsync(10, 77, 1, It.IsAny<decimal>(), null, null))
            .ReturnsAsync(new SaleDto { Id = 10, CashierId = 1 });

        var vm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            session);

        var product = new ProductQuickInfoDto { Id = 77, Name = "Aceite", SKU = "ACE01" };
        vm.SelectedSuggestion = product;

        await vm.AddSelectedSuggestionCommand.ExecuteAsync(null);

        mockSales.Verify(s => s.AddItemAsync(10, 77, 1, It.IsAny<decimal>(), null, null), Times.Once);
        Assert.Equal(string.Empty, vm.SearchText);
        Assert.False(vm.HasSuggestions);
    }
}
