using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Converters;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;
using ISalesService = Desktop.Client.Services.ISalesService;

namespace CommandCenter.Tests.Unit;

public class PendingOrdersClaimTests : IDisposable
{
    private readonly Mock<ISalesService> _salesServiceMock = new();
    private readonly Mock<IExchangeRateService> _exchangeRateServiceMock = new();
    private readonly Mock<IPaymentService> _paymentServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();
    private readonly List<PendingOrdersViewModel> _viewModels = new();

    public void Dispose()
    {
        foreach (var vm in _viewModels)
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    private PendingOrdersViewModel CreateViewModel(UserSession session, params SaleDto[] sales)
    {
        _exchangeRateServiceMock
            .Setup(s => s.GetCurrentRateAsync())
            .ReturnsAsync((50m, (DateTime?)null));
        _salesServiceMock
            .Setup(s => s.GetPendingSalesPagedAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((sales, sales.Length));
        _paymentServiceMock
            .Setup(s => s.GetActiveMethodsAsync())
            .ReturnsAsync(new List<PaymentMethodDto>
            {
                new() { Id = 1, Name = "Efectivo USD", IsCash = true, IsActive = true }
            });

        var vm = new PendingOrdersViewModel(
            _salesServiceMock.Object,
            _exchangeRateServiceMock.Object,
            _paymentServiceMock.Object,
            _dialogServiceMock.Object,
            session);
        _viewModels.Add(vm);
        return vm;
    }

    private static UserSession CreateSession(int userId, UserRole role)
    {
        var session = new UserSession();
        session.SetUser(new UserDto { Id = userId, Name = "Cajero Test", Cedula = "V-1", Role = role });
        return session;
    }

    private static SaleDto CreateSale(int id, int? claimedByUserId = null, string? claimedByUserName = null, string claimAction = "None") => new()
    {
        Id = id,
        Status = "OnHold",
        TotalUSD = 100m,
        TotalBsS = 5000m,
        RemainingBalanceUSD = 100m,
        CustomerId = 1,
        CustomerName = "Cliente Test",
        ClaimedByUserId = claimedByUserId,
        ClaimedByUserName = claimedByUserName,
        ClaimAction = claimAction
    };

    [Fact]
    public async Task LiquidarAbonar_WhenOrderUnclaimed_ClaimsCheckoutOpensModalAndReleases()
    {
        var session = CreateSession(3, UserRole.Cashier);
        var sale = CreateSale(10);
        var vm = CreateViewModel(session, sale);

        _salesServiceMock
            .Setup(s => s.ClaimSaleAsync(10, "Checkout", It.IsAny<CancellationToken>()))
            .ReturnsAsync(sale);
        _salesServiceMock
            .Setup(s => s.ReleaseSaleAsync(10, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sale);
        _dialogServiceMock
            .Setup(d => d.ShowModalAsync(It.IsAny<object>(), It.IsAny<string?>()))
            .ReturnsAsync((object?)99);

        await vm.LiquidarAbonarCommand.ExecuteAsync(sale);

        _salesServiceMock.Verify(s => s.ClaimSaleAsync(10, "Checkout", It.IsAny<CancellationToken>()), Times.Once);
        _dialogServiceMock.Verify(d => d.ShowModalAsync(It.IsAny<object>(), "RootDialog"), Times.Once);
        _salesServiceMock.Verify(s => s.ReleaseSaleAsync(10, false, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("Factura N° 00099", vm.SuccessMessage);
    }

    [Fact]
    public async Task LiquidarAbonar_WhenClaimRejected_ShowsErrorAndDoesNotOpenModal()
    {
        var session = CreateSession(3, UserRole.Cashier);
        var sale = CreateSale(10);
        var vm = CreateViewModel(session, sale);

        _salesServiceMock
            .Setup(s => s.ClaimSaleAsync(10, "Checkout", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("El pedido #10 está bloqueado por Ana (en proceso de pago)."));

        await vm.LiquidarAbonarCommand.ExecuteAsync(sale);

        _dialogServiceMock.Verify(d => d.ShowError("Pedido bloqueado", It.IsAny<string>()), Times.Once);
        _dialogServiceMock.Verify(d => d.ShowModalAsync(It.IsAny<object>(), It.IsAny<string?>()), Times.Never);
        _salesServiceMock.Verify(s => s.ReleaseSaleAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Editar_WhenConfirmed_ClaimsEditingEditsAndReleases()
    {
        var session = CreateSession(3, UserRole.Cashier);
        var sale = CreateSale(20);
        var vm = CreateViewModel(session, sale);
        await vm.EnsureLoadedAsync();

        var modifiedItems = new List<UpdateSaleItemDto>
        {
            new() { SaleItemId = 1, ProductId = 1, Quantity = 2m, UnitPrice = 1m }
        };

        _salesServiceMock
            .Setup(s => s.ClaimSaleAsync(20, "Editing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(sale);
        _salesServiceMock
            .Setup(s => s.UpdateSaleItemsAsync(20, It.IsAny<IEnumerable<UpdateSaleItemDto>>(), 50m))
            .Returns(Task.CompletedTask);
        _salesServiceMock
            .Setup(s => s.ReleaseSaleAsync(20, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sale);
        _dialogServiceMock
            .Setup(d => d.ShowEditSaleDialogAsync(sale, 50m))
            .ReturnsAsync((true, (IEnumerable<UpdateSaleItemDto>?)modifiedItems));

        await vm.EditarCommand.ExecuteAsync(sale);

        _salesServiceMock.Verify(s => s.ClaimSaleAsync(20, "Editing", It.IsAny<CancellationToken>()), Times.Once);
        _salesServiceMock.Verify(s => s.UpdateSaleItemsAsync(20, It.IsAny<IEnumerable<UpdateSaleItemDto>>(), 50m), Times.Once);
        _salesServiceMock.Verify(s => s.ReleaseSaleAsync(20, false, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("Pedido #20 actualizado correctamente.", vm.SuccessMessage);
    }

    [Fact]
    public async Task Editar_WhenClaimRejected_ShowsErrorAndDoesNotEdit()
    {
        var session = CreateSession(3, UserRole.Cashier);
        var sale = CreateSale(20);
        var vm = CreateViewModel(session, sale);

        _salesServiceMock
            .Setup(s => s.ClaimSaleAsync(20, "Editing", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("El pedido #20 está bloqueado por Ana (editando pedido)."));

        await vm.EditarCommand.ExecuteAsync(sale);

        _dialogServiceMock.Verify(d => d.ShowError("Pedido bloqueado", It.IsAny<string>()), Times.Once);
        _dialogServiceMock.Verify(d => d.ShowEditSaleDialogAsync(It.IsAny<SaleDto>(), It.IsAny<decimal>()), Times.Never);
        _salesServiceMock.Verify(s => s.UpdateSaleItemsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<UpdateSaleItemDto>>(), It.IsAny<decimal>()), Times.Never);
        _salesServiceMock.Verify(s => s.ReleaseSaleAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LiberarBloqueo_WhenElevated_CallsReleaseWithForce()
    {
        var session = CreateSession(1, UserRole.Admin);
        var sale = CreateSale(30, claimedByUserId: 9, claimedByUserName: "Juan", claimAction: "Checkout");
        var vm = CreateViewModel(session, sale);

        _salesServiceMock
            .Setup(s => s.ReleaseSaleAsync(30, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sale);

        Assert.True(vm.CanForceRelease);

        await vm.LiberarBloqueoCommand.ExecuteAsync(sale);

        _salesServiceMock.Verify(s => s.ReleaseSaleAsync(30, true, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("Bloqueo del pedido #30 liberado.", vm.SuccessMessage);
    }

    [Fact]
    public void CanActOnOrder_WhenLockedByAnotherCashier_DisablesCommands()
    {
        var session = CreateSession(3, UserRole.Cashier);
        var sale = CreateSale(40, claimedByUserId: 9, claimedByUserName: "Otro Cajero", claimAction: "Editing");
        var vm = CreateViewModel(session, sale);

        Assert.False(vm.LiquidarAbonarCommand.CanExecute(sale));
        Assert.False(vm.EditarCommand.CanExecute(sale));
    }

    [Fact]
    public async Task LiquidarAbonar_WhenOrderLockedBySelf_AllowsAction()
    {
        var session = CreateSession(3, UserRole.Cashier);
        var sale = CreateSale(50, claimedByUserId: 3, claimedByUserName: "Cajero Test", claimAction: "Checkout");
        var vm = CreateViewModel(session, sale);

        _salesServiceMock
            .Setup(s => s.ClaimSaleAsync(50, "Checkout", It.IsAny<CancellationToken>()))
            .ReturnsAsync(sale);
        _salesServiceMock
            .Setup(s => s.ReleaseSaleAsync(50, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sale);
        _dialogServiceMock
            .Setup(d => d.ShowModalAsync(It.IsAny<object>(), It.IsAny<string?>()))
            .ReturnsAsync((object?)null);

        Assert.True(vm.LiquidarAbonarCommand.CanExecute(sale));
        Assert.True(vm.EditarCommand.CanExecute(sale));

        await vm.LiquidarAbonarCommand.ExecuteAsync(sale);

        _salesServiceMock.Verify(s => s.ClaimSaleAsync(50, "Checkout", It.IsAny<CancellationToken>()), Times.Once);
        _dialogServiceMock.Verify(d => d.ShowModalAsync(It.IsAny<object>(), "RootDialog"), Times.Once);
    }

    [Fact]
    public void HoldLockDisplay_WhenCheckoutClaim_ReturnsLabelWithActionAndName()
    {
        var converter = new HoldLockDisplayConverter();
        var sale = CreateSale(60, claimedByUserId: 9, claimedByUserName: "María Pérez", claimAction: "Checkout");

        var result = converter.Convert(sale, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("Bloqueado por María Pérez - En proceso de pago", result);
    }

    [Fact]
    public void HoldLockDisplay_WhenMissingNameAndUnknownAction_UsesFallbacks()
    {
        var converter = new HoldLockDisplayConverter();
        var sale = CreateSale(61, claimedByUserId: 9, claimedByUserName: null, claimAction: "OtraCosa");

        var result = converter.Convert(sale, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("Bloqueado por otro cajero - Bloqueado", result);
    }
}
