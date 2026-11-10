using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Cierre de productividad del Inventario: la última operación por producto se recuerda para
/// repetirla con Ctrl+R (o con el botón de la fila) sin duplicar ni saltear las confirmaciones
/// internas de Pausar/Eliminar.
/// </summary>
public class InventoryQuickRepeatTests
{
    private readonly Mock<IProductService> _productService = new();
    private readonly Mock<IExchangeRateService> _exchangeRateService = new();
    private readonly Mock<IDialogService> _dialogService = new();

    private InventoryViewModel CreateViewModel()
    {
        _exchangeRateService.Setup(s => s.CurrentRate).Returns(36.5m);
        var vm = new InventoryViewModel(
            _productService.Object,
            _exchangeRateService.Object,
            new UserSession(),
            _dialogService.Object);

        // Aisla del bus global compartido entre pruebas.
        WeakReferenceMessenger.Default.UnregisterAll(vm);
        return vm;
    }

    private ProductItemViewModel Item(int id, bool isActive = true) =>
        new(
            new ProductDto
            {
                Id = id,
                Name = $"Producto {id}",
                SKU = $"SKU-{id:D4}",
                PriceUSD = 1m,
                PriceBsS = 36.5m,
                IsActive = isActive
            },
            _exchangeRateService.Object);

    [Fact]
    public async Task TogglePause_RecordsLastQuickRepeatMetadata()
    {
        var vm = CreateViewModel();
        var item = Item(1);

        await vm.TogglePauseProductCommand.ExecuteAsync(item);

        Assert.True(vm.HasLastQuickRepeat);
        Assert.Equal("Pause", vm.LastQuickRepeatIcon);
        Assert.Equal("Repetir: Pausar producto (Ctrl+R)", vm.LastQuickRepeatToolTip);
        Assert.True(vm.RepeatLastActionCommand.CanExecute(null));
        Assert.False(item.IsActive);
    }

    [Fact]
    public async Task RepeatLastAction_WithoutParameter_ReexecutesRememberedTarget()
    {
        var vm = CreateViewModel();
        var item = Item(7);
        await vm.TogglePauseProductCommand.ExecuteAsync(item);

        await vm.RepeatLastActionCommand.ExecuteAsync(null);

        _productService.Verify(
            s => s.SetStatusAsync(7, It.IsAny<bool>(), false),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RepeatLastAction_WhenLastActionIsDeleteAndNotConfirmed_DoesNotDelete()
    {
        var vm = CreateViewModel();
        var item = Item(9);
        _dialogService
            .Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        await vm.DeleteProductCommand.ExecuteAsync(item);
        _dialogService.Invocations.Clear();
        _productService.Invocations.Clear();

        await vm.RepeatLastActionCommand.ExecuteAsync(null);

        _dialogService.Verify(
            d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()),
            Times.Exactly(2));
        _productService.Verify(
            s => s.DeleteAsync(It.IsAny<int>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task RepeatLastAction_WhenDeleteConfirmed_DeletesRememberedTarget()
    {
        var vm = CreateViewModel();
        var item = Item(10);
        _dialogService
            .SetupSequence(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false) // no pausar
            .Returns(true)  // eliminar
            .Returns(false) // repetir: no pausar
            .Returns(true); // repetir: eliminar
        _productService
            .Setup(s => s.DeleteAsync(10, true))
            .ReturnsAsync("hard_deleted");

        await vm.DeleteProductCommand.ExecuteAsync(item);
        await vm.RepeatLastActionCommand.ExecuteAsync(null);

        _productService.Verify(s => s.DeleteAsync(10, true), Times.Exactly(2));
    }

    [Fact]
    public async Task RepeatLastAction_WithoutRecordedAction_IsDisabledAndNoOp()
    {
        var vm = CreateViewModel();

        Assert.False(vm.HasLastQuickRepeat);
        Assert.False(vm.RepeatLastActionCommand.CanExecute(null));

        await vm.RepeatLastActionCommand.ExecuteAsync(null);

        _productService.Verify(
            s => s.SetStatusAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>()),
            Times.Never);
        _productService.Verify(
            s => s.DeleteAsync(It.IsAny<int>(), It.IsAny<bool>()),
            Times.Never);
        _productService.Verify(
            s => s.RestoreAsync(It.IsAny<int>()),
            Times.Never);
        _productService.Verify(
            s => s.AdjustStockAsync(
                It.IsAny<int>(),
                It.IsAny<decimal>(),
                It.IsAny<string>(),
                It.IsAny<Core.Entities.StockMovementReason?>(),
                It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task RepeatLastAction_WithRowParameter_UsesProvidedRow()
    {
        var vm = CreateViewModel();
        var remembered = Item(1);
        var requested = Item(2);
        await vm.TogglePauseProductCommand.ExecuteAsync(remembered);

        await vm.RepeatLastActionCommand.ExecuteAsync(requested);

        _productService.Verify(
            s => s.SetStatusAsync(1, It.IsAny<bool>(), false),
            Times.Once);
        _productService.Verify(
            s => s.SetStatusAsync(2, It.IsAny<bool>(), false),
            Times.Once);
    }
}
