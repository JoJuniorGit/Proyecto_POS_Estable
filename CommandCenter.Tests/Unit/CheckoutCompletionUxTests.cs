using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class CheckoutCompletionUxTests
{
    [Fact]
    public async Task CheckoutCommand_WhenCheckoutCompletesAndUserChoosesReceipt_OpensReceiptDirectlyWithoutSecondaryPopup()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var mockDialog = new Mock<IDialogService>();

        mockRate.SetupGet(r => r.CurrentRate).Returns(60.0m);
        var initialSale = new SaleDto
        {
            Id = 101,
            Status = "Draft",
            TotalUSD = 50m,
            Items = new List<SaleItemDto>
            {
                new SaleItemDto { Id = 1, ProductId = 1, ProductName = "Test Item", Quantity = 1, UnitPrice = 50m, Subtotal = 50m }
            }
        };

        mockSales.Setup(s => s.StartSaleAsync(It.IsAny<int?>()))
            .ReturnsAsync(initialSale);
        mockSales.Setup(s => s.GetSaleAsync(101))
            .ReturnsAsync(initialSale);
        mockSales.Setup(s => s.UpdateItemQuantityAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>()))
            .ReturnsAsync(initialSale);

        mockSales.Setup(s => s.GetReceiptAsync(101))
            .ReturnsAsync(new byte[] { 1, 2, 3, 4 });

        mockDialog.Setup(d => d.ShowModalAsync(It.IsAny<CheckoutViewModel>(), "RootDialog"))
            .ReturnsAsync(101);

        mockDialog.Setup(d => d.ShowSuccessDialog(It.IsAny<string>(), "Guardar Recibo"))
            .Returns(true);

        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Cajero", Role = UserRole.Cashier }, "token");

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        cartVm.CurrentSale = initialSale;

        using var posVm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            session,
            mockDialog.Object);

        await posVm.CheckoutCommand.ExecuteAsync(null);

        mockDialog.Verify(d => d.ShowSuccessDialog(
            It.Is<string>(msg => msg.Contains("101") && msg.Contains("completada con éxito")),
            "Guardar Recibo"), Times.Once);

        mockDialog.Verify(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        mockSales.Verify(s => s.GetReceiptAsync(101), Times.Once);
    }

    [Fact]
    public async Task CheckoutCommand_WhenCheckoutCompletesAndUserDeclinesReceipt_DoesNotFetchReceipt()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        var mockDialog = new Mock<IDialogService>();

        mockRate.SetupGet(r => r.CurrentRate).Returns(60.0m);
        var initialSale = new SaleDto
        {
            Id = 102,
            Status = "Draft",
            TotalUSD = 30m,
            Items = new List<SaleItemDto>
            {
                new SaleItemDto { Id = 2, ProductId = 2, ProductName = "Test Item 2", Quantity = 1, UnitPrice = 30m, Subtotal = 30m }
            }
        };

        mockSales.Setup(s => s.StartSaleAsync(It.IsAny<int?>()))
            .ReturnsAsync(initialSale);
        mockSales.Setup(s => s.GetSaleAsync(102))
            .ReturnsAsync(initialSale);
        mockSales.Setup(s => s.UpdateItemQuantityAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>()))
            .ReturnsAsync(initialSale);

        mockDialog.Setup(d => d.ShowModalAsync(It.IsAny<CheckoutViewModel>(), "RootDialog"))
            .ReturnsAsync(102);

        mockDialog.Setup(d => d.ShowSuccessDialog(It.IsAny<string>(), "Guardar Recibo"))
            .Returns(false);

        var session = new UserSession();
        session.SetUser(new UserDto { Id = 1, Name = "Cajero", Role = UserRole.Cashier }, "token");

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        cartVm.CurrentSale = initialSale;

        using var posVm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            session,
            mockDialog.Object);

        await posVm.CheckoutCommand.ExecuteAsync(null);

        mockDialog.Verify(d => d.ShowSuccessDialog(
            It.Is<string>(msg => msg.Contains("102")),
            "Guardar Recibo"), Times.Once);

        mockDialog.Verify(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        mockSales.Verify(s => s.GetReceiptAsync(It.IsAny<int>()), Times.Never);
    }
}

