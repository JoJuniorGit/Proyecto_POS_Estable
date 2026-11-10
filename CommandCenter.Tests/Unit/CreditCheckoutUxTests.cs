using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;
using SalePaymentDto = Desktop.Client.Services.SalePaymentDto;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T4 (F3, S2/S4): checkout a crédito total y mixto, cliente obligatorio, monto financiado,
/// vencimiento y sobregiro resuelto por autorización de supervisor con el contexto exacto.
/// </summary>
public class CreditCheckoutUxTests
{
    private const decimal Rate = 36.50m;

    private static UserSession Session()
    {
        var session = new UserSession();
        session.SetUser(new UserDto { Id = 9, Name = "Cajera", Role = UserRole.Cashier }, "token");
        return session;
    }

    private static SaleDto Sale(bool withCustomer = true) => new()
    {
        Id = 9,
        Status = "Draft",
        TotalUSD = 200m,
        TotalBsS = 200m * Rate,
        AppliedRate = Rate,
        CustomerId = withCustomer ? 7 : null,
        CustomerName = withCustomer ? "Ana Pérez" : "Consumidor Final",
        Items = new List<SaleItemDto>
        {
            new() { Id = 1, ProductId = 1, ProductName = "Producto", Quantity = 1m, UnitPrice = 200m, Subtotal = 200m }
        }
    };

    private static CreditProfileClientDto Profile(
        bool enabled = true,
        decimal available = 1000m,
        int overdueCount = 0,
        CreditStatus status = CreditStatus.Active) => new()
    {
        CustomerId = 7,
        CustomerName = "Ana Pérez",
        IsActive = true,
        IsCreditEnabled = enabled,
        Status = status,
        CreditLimitUSD = 1000m,
        CurrentDebtUSD = 200m,
        AvailableCreditUSD = available,
        PaymentTermDays = 30,
        CreditGraceDays = 3,
        OverdueCount = overdueCount
    };

    private static Mock<ISalesService> PreviewMock()
    {
        var sales = new Mock<ISalesService>();
        sales.Setup(s => s.GetCheckoutPreviewAsync(
                9,
                Rate,
                It.IsAny<IEnumerable<SalePaymentDto>>()))
            .Returns< int, decimal, IEnumerable<SalePaymentDto>>((_, _, payments) =>
            {
                decimal paidBs = payments.Sum(p => p.AmountBsS);
                decimal paidUsd = payments.Sum(p => p.Amount);
                decimal remainingBs = Math.Max(0m, 200m * Rate - paidBs);
                decimal remainingUsd = Math.Round(remainingBs / Rate, 2, MidpointRounding.AwayFromZero);
                return Task.FromResult(new CheckoutPreviewClientDto
                {
                    TotalUSD = 200m,
                    TotalBsS = 200m * Rate,
                    TotalPaidUSD = paidUsd,
                    TotalPaidBsS = paidBs,
                    RemainingBalanceUSD = remainingUsd,
                    RemainingBalanceBsS = remainingBs,
                    IsFullyPaid = remainingBs <= 0.05m
                });
            });
        return sales;
    }

    private static async Task<CheckoutViewModel> CreateCreditVmAsync(
        Mock<ISalesService> sales,
        Mock<IDialogService> dialog,
        CreditProfileClientDto profile,
        bool withCustomer = true)
    {
        sales.Setup(s => s.GetCreditProfileAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        var methods = new System.Collections.ObjectModel.ObservableCollection<PaymentMethodDto>
        {
            new() { Id = 1, Name = "Efectivo Bs.S", IsCash = false, Currency = "Bs.S" }
        };
        var vm = new CheckoutViewModel(
            Sale(withCustomer),
            methods,
            sales.Object,
            Rate,
            Session(),
            overrideSale: null,
            dialogService: dialog.Object);
        await (vm.PendingPreview ?? Task.CompletedTask);
        vm.IsCreditMode = true;
        await (vm.PendingCreditProfile ?? Task.CompletedTask);
        return vm;
    }

    [Fact]
    public async Task CreditMode_TotalSale_ShowsFinancedAmountDueDateAndAllowsFinalizeWithoutPayments()
    {
        var sales = PreviewMock();
        var dialog = new Mock<IDialogService>();
        using var vm = await CreateCreditVmAsync(sales, dialog, Profile());

        Assert.True(vm.IsCreditMode);
        Assert.True(vm.CanFinalize);
        Assert.Equal(200m, vm.FinancedUSD);
        Assert.Equal(200m * Rate, vm.FinancedBsS);
        Assert.Equal(DateTime.Today.AddDays(30), vm.CreditDueDate);
        Assert.False(vm.IsCreditOverLimit);
        Assert.Equal("Ana Pérez", vm.CustomerDisplay);
    }

    [Fact]
    public async Task CreditMode_WithInitialPayment_FinancesOnlyTheRemainder()
    {
        var sales = PreviewMock();
        var dialog = new Mock<IDialogService>();
        using var vm = await CreateCreditVmAsync(sales, dialog, Profile());

        vm.SelectedMethod = vm.AvailableMethods.First();
        vm.AmountBsSText = "3600.00";
        vm.AddPaymentCommand.Execute(null);
        await (vm.PendingPreview ?? Task.CompletedTask);

        Assert.Single(vm.Payments);
        Assert.Equal(101.37m, vm.FinancedUSD);
        Assert.Equal(3700m, vm.FinancedBsS);
        Assert.True(vm.CanFinalize);
    }

    [Fact]
    public async Task CreditMode_WithoutRegisteredCustomer_BlocksFinalizeWithExplicitMessage()
    {
        var sales = PreviewMock();
        var dialog = new Mock<IDialogService>();
        using var vm = await CreateCreditVmAsync(sales, dialog, Profile(), withCustomer: false);

        Assert.False(vm.CanFinalize);
        Assert.Contains("cliente registrado", vm.ValidationHelperMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreditMode_WhenProfileDisabled_BlocksFinalize()
    {
        var sales = PreviewMock();
        var dialog = new Mock<IDialogService>();
        using var vm = await CreateCreditVmAsync(sales, dialog, Profile(enabled: false));

        Assert.False(vm.CanFinalize);
        Assert.Contains("crédito", vm.ValidationHelperMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreditMode_WhenCustomerHasOverdueDebt_BlocksFinalize()
    {
        var sales = PreviewMock();
        var dialog = new Mock<IDialogService>();
        using var vm = await CreateCreditVmAsync(sales, dialog, Profile(overdueCount: 2));

        Assert.False(vm.CanFinalize);
        Assert.Contains("vencida", vm.ValidationHelperMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreditMode_OverLimit_ShowsWarningButStillAllowsSupervisorAuthorization()
    {
        var sales = PreviewMock();
        var dialog = new Mock<IDialogService>();
        using var vm = await CreateCreditVmAsync(sales, dialog, Profile(available: 50m));

        Assert.True(vm.IsCreditOverLimit);
        Assert.Contains("autorización", vm.CreditOverLimitMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.CanFinalize);
    }

    [Fact]
    public async Task FinalizeCreditSale_WhenBackendRequestsOverLimitAuthorization_RetriesWithTokenAndClosesWithInvoice()
    {
        var sales = PreviewMock();
        var dialog = new Mock<IDialogService>();
        using var vm = await CreateCreditVmAsync(sales, dialog, Profile(available: 50m));

        var context = new CreditOverLimitAuthorizationRequestContext
        {
            SaleId = 9,
            CustomerId = 7,
            CustomerName = "Ana Pérez",
            FinancedUSD = 200m,
            AvailableCreditUSD = 50m,
            Terminal = "CAJA-1"
        };

        sales.Setup(s => s.CompleteCreditSaleAsync(
                9, Rate, It.IsAny<IEnumerable<SalePaymentDto>>(), 9, null, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CreditOverLimitAuthorizationException("Se requiere autorización.", context));
        sales.Setup(s => s.CompleteCreditSaleAsync(
                9, Rate, It.IsAny<IEnumerable<SalePaymentDto>>(), 9, "tok-1", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(321);

        CreditOverLimitAuthorizationRequestContext? captured = null;
        dialog.Setup(d => d.ShowAuthorizationWaitAsync(It.IsAny<IAuthorizationRequestContext>(), It.IsAny<Func<string, Task>>()))
            .Returns<IAuthorizationRequestContext, Func<string, Task>>(async (ctx, retry) =>
            {
                captured = Assert.IsType<CreditOverLimitAuthorizationRequestContext>(ctx);
                await retry("tok-1");
                return new AuthorizationWaitResult(AuthorizationWaitOutcome.Granted);
            });

        await vm.FinalizeSaleCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal(7, captured!.CustomerId);
        Assert.Equal(200m, captured.FinancedUSD);
        Assert.Equal(50m, captured.AvailableCreditUSD);
        sales.Verify(s => s.CompleteCreditSaleAsync(9, Rate, It.IsAny<IEnumerable<SalePaymentDto>>(), 9, "tok-1", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        dialog.Verify(d => d.CloseCurrentModal(321), Times.Once);
        dialog.Verify(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task FinalizeCreditSale_WhenAuthorizationRejected_ShowsErrorAndKeepsModalOpen()
    {
        var sales = PreviewMock();
        var dialog = new Mock<IDialogService>();
        using var vm = await CreateCreditVmAsync(sales, dialog, Profile(available: 50m));

        var context = new CreditOverLimitAuthorizationRequestContext
        {
            SaleId = 9,
            CustomerId = 7,
            FinancedUSD = 200m,
            AvailableCreditUSD = 50m
        };
        sales.Setup(s => s.CompleteCreditSaleAsync(
                9, Rate, It.IsAny<IEnumerable<SalePaymentDto>>(), 9, null, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CreditOverLimitAuthorizationException("Se requiere autorización.", context));
        dialog.Setup(d => d.ShowAuthorizationWaitAsync(It.IsAny<IAuthorizationRequestContext>(), It.IsAny<Func<string, Task>>()))
            .ReturnsAsync(new AuthorizationWaitResult(AuthorizationWaitOutcome.Rejected, null, "Límite excedido."));

        await vm.FinalizeSaleCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowError("Autorización", It.Is<string>(m => m.Contains("Límite excedido"))), Times.Once);
        dialog.Verify(d => d.CloseCurrentModal(It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task PosCheckout_WhenCreditSaleCompletes_OffersCreditReceiptAndResetsCart()
    {
        var sales = new Mock<ISalesService>();
        var products = new Mock<IProductService>();
        var payments = new Mock<IPaymentService>();
        var rate = new Mock<IExchangeRateService>();
        rate.SetupGet(r => r.CurrentRate).Returns(Rate);
        var dialog = new Mock<IDialogService>();

        var sale = Sale();
        sales.Setup(s => s.StartSaleAsync(It.IsAny<int?>())).ReturnsAsync(sale);
        sales.Setup(s => s.GetSaleAsync(9)).ReturnsAsync(sale);
        sales.Setup(s => s.UpdateItemQuantityAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>()))
            .ReturnsAsync(sale);
        sales.Setup(s => s.GetCheckoutPreviewAsync(9, Rate, It.IsAny<IEnumerable<SalePaymentDto>>()))
            .ReturnsAsync(new CheckoutPreviewClientDto
            {
                TotalUSD = 200m,
                TotalBsS = 200m * Rate,
                RemainingBalanceUSD = 200m,
                RemainingBalanceBsS = 200m * Rate,
                IsFullyPaid = false
            });
        sales.Setup(s => s.GetCreditProfileAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(Profile());
        sales.Setup(s => s.CompleteCreditSaleAsync(
                9, Rate, It.IsAny<IEnumerable<SalePaymentDto>>(), 9, null, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(321);
        dialog.Setup(d => d.ShowModalAsync(It.IsAny<object>(), "RootDialog"))
            .Returns<object, string?>(async (content, _) =>
            {
                var vm = Assert.IsType<CheckoutViewModel>(content);
                await (vm.PendingPreview ?? Task.CompletedTask);
                vm.IsCreditMode = true;
                await (vm.PendingCreditProfile ?? Task.CompletedTask);
                return 321;
            });
        dialog.Setup(d => d.ShowSuccessDialog(It.IsAny<string>(), "Guardar Comprobante de Crédito")).Returns(false);

        var cart = new CartViewModel(sales.Object, rate.Object);
        WeakReferenceMessenger.Default.UnregisterAll(cart);
        cart.CurrentSale = sale;
        using var pos = new PosViewModel(sales.Object, products.Object, payments.Object, rate.Object, cart, Session(), dialog.Object);

        await pos.CheckoutCommand.ExecuteAsync(null);

        dialog.Verify(d => d.ShowSuccessDialog(
            It.Is<string>(m => m.Contains("crédito") && m.Contains("321")),
            "Guardar Comprobante de Crédito"), Times.Once);
        dialog.Verify(d => d.ShowSuccessDialog(It.IsAny<string>(), "Guardar Recibo"), Times.Never);
    }

    [Fact]
    public async Task CashMode_WithFullPayment_UsesRegularCheckoutEndpoint()
    {
        var sales = PreviewMock();
        var dialog = new Mock<IDialogService>();
        var vm = new CheckoutViewModel(
            Sale(),
            new System.Collections.ObjectModel.ObservableCollection<PaymentMethodDto>
            {
                new() { Id = 1, Name = "Efectivo Bs.S", IsCash = false, Currency = "Bs.S" }
            },
            sales.Object,
            Rate,
            Session(),
            overrideSale: null,
            dialogService: dialog.Object);
        await (vm.PendingPreview ?? Task.CompletedTask);

        vm.SelectedMethod = vm.AvailableMethods.First();
        vm.AmountBsSText = "7300.00";
        vm.AddPaymentCommand.Execute(null);
        await (vm.PendingPreview ?? Task.CompletedTask);

        sales.Setup(s => s.CompleteSaleAsync(
                9, Rate, It.IsAny<IEnumerable<SalePaymentDto>>(), It.IsAny<decimal>(), 9,
                false, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(77);

        await vm.FinalizeSaleCommand.ExecuteAsync(null);

        sales.Verify(s => s.CompleteSaleAsync(9, Rate, It.IsAny<IEnumerable<SalePaymentDto>>(), It.IsAny<decimal>(), 9, false, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        sales.Verify(s => s.CompleteCreditSaleAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<IEnumerable<SalePaymentDto>>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.Dispose();
    }
}
