using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.150 (T10, design D5/D7): integracion del flujo de espera en el alta con precio manual del
/// POS. El backend responde el 403 detectable y el POS arranca el dialogo bloqueante, reintentando
/// con el token aprobado y degradando con error visible si la creacion falla.
/// </summary>
public class PosViewModelAuthorizationTests
{
    private static (PosViewModel Vm, Mock<ISalesService> Sales, Mock<IDialogService> Dialog) CreateViewModel()
    {
        var mockSales = new Mock<ISalesService>();
        var mockProducts = new Mock<IProductService>();
        var mockPayments = new Mock<IPaymentService>();
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.Setup(r => r.CurrentRate).Returns(50m);
        var mockDialog = new Mock<IDialogService>();

        var cartVm = new CartViewModel(mockSales.Object, mockRate.Object);
        cartVm.CurrentSale = new SaleDto { Id = 1, Items = new List<SaleItemDto>() };

        var vm = new PosViewModel(
            mockSales.Object,
            mockProducts.Object,
            mockPayments.Object,
            mockRate.Object,
            cartVm,
            new UserSession(),
            mockDialog.Object);

        // Aisla del bus global compartido entre pruebas (patron de PosScanNotFoundFeedbackTests).
        WeakReferenceMessenger.Default.UnregisterAll(vm);
        WeakReferenceMessenger.Default.UnregisterAll(cartVm);
        return (vm, mockSales, mockDialog);
    }

    private static ProductQuickInfoDto CashAdvanceProduct() => new()
    {
        Id = 42,
        Name = "Harina PAN",
        SKU = "12345670",
        IsCashAdvance = true,
        ProfitPercentage = 0m,
        IsActive = true
    };

    private static SaleDto UpdatedSale() => new()
    {
        Id = 1,
        Items = new List<SaleItemDto> { new() { Id = 7, ProductId = 42, Quantity = 1m } }
    };

    private static void SetupAuthorizationRefusal(Mock<ISalesService> sales)
    {
        sales.Setup(s => s.AddItemAsync(1, 42, 1m, 50m, 10m, 500m))
            .ThrowsAsync(new AuthorizationRequiredException(
                "Se requiere autorización remota de un administrador.", "ManualPriceOverride"));
    }

    [Fact]
    public async Task CustomPriceAdd_WhenAuthorizationRequired_StartsWaitFlowAndRetriesWithToken()
    {
        var (vm, sales, dialog) = CreateViewModel();
        var product = CashAdvanceProduct();
        dialog.Setup(d => d.ShowCashAdvanceDialog()).Returns(500m);
        SetupAuthorizationRefusal(sales);
        sales.Setup(s => s.AddItemAsync(1, 42, 1m, 50m, 10m, 500m, "token-1")).ReturnsAsync(UpdatedSale());

        AuthorizationRequestContext? captured = null;
        dialog.Setup(d => d.ShowAuthorizationWaitAsync(It.IsAny<AuthorizationRequestContext>(), It.IsAny<Func<string, Task>>()))
            .Returns<AuthorizationRequestContext, Func<string, Task>>(async (context, retry) =>
            {
                captured = context;
                await retry("token-1");
                return new AuthorizationWaitResult(AuthorizationWaitOutcome.Granted);
            });

        await vm.AddSelectedSuggestionCommand.ExecuteAsync(product);

        Assert.NotNull(captured);
        Assert.Equal(1, captured!.SaleId);
        Assert.Equal(42, captured.ProductId);
        Assert.Equal("Harina PAN", captured.ProductName);
        Assert.Equal(1m, captured.Quantity);
        Assert.Equal(10m, captured.CustomUnitPriceUsd);
        Assert.Equal(500m, captured.CustomUnitPriceLocal);

        sales.Verify(s => s.AddItemAsync(1, 42, 1m, 50m, 10m, 500m, "token-1"), Times.Once);
        Assert.Single(vm.Cart.CurrentSale!.Items);
        dialog.Verify(d => d.ShowError("Error", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CustomPriceAdd_WhenFlowCancelled_KeepsCartUnchangedWithoutGenericError()
    {
        var (vm, sales, dialog) = CreateViewModel();
        dialog.Setup(d => d.ShowCashAdvanceDialog()).Returns(500m);
        SetupAuthorizationRefusal(sales);
        dialog.Setup(d => d.ShowAuthorizationWaitAsync(It.IsAny<AuthorizationRequestContext>(), It.IsAny<Func<string, Task>>()))
            .ReturnsAsync(new AuthorizationWaitResult(AuthorizationWaitOutcome.Cancelled));

        await vm.AddSelectedSuggestionCommand.ExecuteAsync(CashAdvanceProduct());

        Assert.Empty(vm.Cart.CurrentSale!.Items);
        dialog.Verify(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.False(vm.IsProcessing);
    }

    [Fact]
    public async Task CustomPriceAdd_WhenRejected_ShowsExactRejectionWithReason()
    {
        var (vm, sales, dialog) = CreateViewModel();
        dialog.Setup(d => d.ShowCashAdvanceDialog()).Returns(500m);
        SetupAuthorizationRefusal(sales);
        dialog.Setup(d => d.ShowAuthorizationWaitAsync(It.IsAny<AuthorizationRequestContext>(), It.IsAny<Func<string, Task>>()))
            .ReturnsAsync(new AuthorizationWaitResult(AuthorizationWaitOutcome.Rejected, null, "Precio fuera de política."));

        await vm.AddSelectedSuggestionCommand.ExecuteAsync(CashAdvanceProduct());

        dialog.Verify(d => d.ShowError("Autorización", "Solicitud rechazada. Precio fuera de política."), Times.Once);
        Assert.Empty(vm.Cart.CurrentSale!.Items);
    }

    [Fact]
    public async Task CustomPriceAdd_WhenExpired_ShowsExpiredNotice()
    {
        var (vm, sales, dialog) = CreateViewModel();
        dialog.Setup(d => d.ShowCashAdvanceDialog()).Returns(500m);
        SetupAuthorizationRefusal(sales);
        dialog.Setup(d => d.ShowAuthorizationWaitAsync(It.IsAny<AuthorizationRequestContext>(), It.IsAny<Func<string, Task>>()))
            .ReturnsAsync(new AuthorizationWaitResult(AuthorizationWaitOutcome.Expired));

        await vm.AddSelectedSuggestionCommand.ExecuteAsync(CashAdvanceProduct());

        dialog.Verify(d => d.ShowWarning("Autorización", "Expirada"), Times.Once);
    }

    [Fact]
    public async Task CustomPriceAdd_WhenWaitFlowCreationFails_ShowsOperatorErrorAndKeepsPosUsable()
    {
        var (vm, sales, dialog) = CreateViewModel();
        dialog.Setup(d => d.ShowCashAdvanceDialog()).Returns(500m);
        SetupAuthorizationRefusal(sales);
        dialog.Setup(d => d.ShowAuthorizationWaitAsync(It.IsAny<AuthorizationRequestContext>(), It.IsAny<Func<string, Task>>()))
            .ReturnsAsync(new AuthorizationWaitResult(AuthorizationWaitOutcome.Failed, "Sin conexión con el hub."));

        await vm.AddSelectedSuggestionCommand.ExecuteAsync(CashAdvanceProduct());

        dialog.Verify(d => d.ShowError("Autorización", "Sin conexión con el hub."), Times.Once);
        Assert.Empty(vm.Cart.CurrentSale!.Items);
        Assert.False(vm.IsProcessing);
    }

    [Fact]
    public async Task NonCashAdvanceAdd_WhenSuccessful_NeverStartsWaitFlow()
    {
        var (vm, sales, dialog) = CreateViewModel();
        var product = new ProductQuickInfoDto { Id = 42, Name = "Harina PAN", SKU = "12345670", IsActive = true };
        sales.Setup(s => s.AddItemAsync(1, 42, 1m, 50m, null, null)).ReturnsAsync(UpdatedSale());

        await vm.AddSelectedSuggestionCommand.ExecuteAsync(product);

        dialog.Verify(
            d => d.ShowAuthorizationWaitAsync(It.IsAny<AuthorizationRequestContext>(), It.IsAny<Func<string, Task>>()),
            Times.Never);
        dialog.Verify(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.Single(vm.Cart.CurrentSale!.Items);
    }

    [Fact]
    public async Task NonCashAdvanceAdd_WhenOtherError_KeepsLegacyGenericError()
    {
        var (vm, sales, dialog) = CreateViewModel();
        var product = new ProductQuickInfoDto { Id = 42, Name = "Harina PAN", SKU = "12345670", IsActive = true };
        sales.Setup(s => s.AddItemAsync(1, 42, 1m, 50m, null, null))
            .ThrowsAsync(new InvalidOperationException("Stock insuficiente."));

        await vm.AddSelectedSuggestionCommand.ExecuteAsync(product);

        dialog.Verify(d => d.ShowError("Error", "Error adding item: Stock insuficiente."), Times.Once);
        dialog.Verify(
            d => d.ShowAuthorizationWaitAsync(It.IsAny<AuthorizationRequestContext>(), It.IsAny<Func<string, Task>>()),
            Times.Never);
    }
}
