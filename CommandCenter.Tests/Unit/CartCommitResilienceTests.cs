using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Core.Logging;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.149 / CLEAN-02 (S5): resiliencia del commit de cantidades del carrito.
/// Un fallo de actualización debe loguearse, restaurar el estado autoritativo desde el
/// servidor y notificar al operador con los mensajes exactos del spec.
/// </summary>
public class CartCommitResilienceTests
{
    private const decimal Rate = 842.21m;
    private const string RollbackMessage = "No se pudo actualizar la cantidad. Se restauró el valor del servidor.";
    private const string StaleMessage = "No se pudo actualizar la cantidad y no se pudo restaurar el estado. Verifique el carrito antes de cobrar.";

    private static CartViewModel CreateCart(
        Mock<ISalesService> mockSales,
        Mock<IDialogService>? mockDialog,
        SaleDto sale,
        ISaleRecoveryStore? recoveryStore = null)
    {
        var mockRate = new Mock<IExchangeRateService>();
        mockRate.SetupGet(r => r.CurrentRate).Returns(Rate);
        var cart = new CartViewModel(mockSales.Object, mockRate.Object, mockDialog?.Object, null, recoveryStore);
        // Aísla el carrito del bus global compartido entre pruebas (CurrentSaleChangedMessage).
        WeakReferenceMessenger.Default.UnregisterAll(cart);
        cart.CurrentSale = sale;
        return cart;
    }

    private static SaleDto PendingSale(params (int Id, decimal Quantity)[] items)
    {
        var sale = new SaleDto
        {
            Id = 101,
            Status = "Pending",
            AppliedRate = Rate,
            Items = new System.Collections.Generic.List<SaleItemDto>()
        };

        foreach (var (id, quantity) in items)
        {
            sale.Items.Add(new SaleItemDto
            {
                Id = id,
                ProductId = id,
                ProductName = $"Item {id}",
                Quantity = quantity,
                UnitPrice = 1m,
                Subtotal = quantity,
                UnitPriceBsS = 842.21m,
                SubtotalBsS = 842.21m * quantity
            });
        }

        return sale;
    }

    [Fact]
    public async Task CommitItemQuantityAsync_FailedServerUpdate_RestoresServerQuantityAndShowsRollbackMessage()
    {
        var mockSales = new Mock<ISalesService>();
        var mockDialog = new Mock<IDialogService>();
        var cart = CreateCart(mockSales, mockDialog, PendingSale((1, 1m)));

        // Edición optimista local ya aplicada por CartItemViewModel antes del commit.
        cart.CartItems[0].Model.Quantity = 5m;

        mockSales.Setup(s => s.UpdateItemQuantityAsync(101, 1, 5m, Rate))
                 .ThrowsAsync(new InvalidOperationException("commit-down"));
        mockSales.Setup(s => s.GetSaleAsync(101)).ReturnsAsync(PendingSale((1, 1m)));

        await cart.CommitItemQuantityAsync(1, 5m);

        Assert.Equal(1m, cart.CartItems[0].Model.Quantity);
        Assert.Equal(1m, cart.CurrentSale!.Items[0].Quantity);
        mockDialog.Verify(d => d.ShowError("Error", RollbackMessage), Times.Once);
    }

    [Fact]
    public async Task FlushAllQuantitiesAsync_FailedFlush_ResyncsEveryItemAndShowsRollbackMessage()
    {
        var mockSales = new Mock<ISalesService>();
        var mockDialog = new Mock<IDialogService>();
        var cart = CreateCart(mockSales, mockDialog, PendingSale((1, 1m), (2, 1m)));

        cart.CartItems[0].Model.Quantity = 3m;
        cart.CartItems[1].Model.Quantity = 4m;

        mockSales.Setup(s => s.UpdateItemQuantityAsync(101, 1, 3m, Rate))
                 .ThrowsAsync(new InvalidOperationException("flush-down"));
        mockSales.Setup(s => s.GetSaleAsync(101)).ReturnsAsync(PendingSale((1, 1m), (2, 1m)));

        await cart.FlushAllQuantitiesAsync();

        Assert.Equal(1m, cart.CartItems[0].Model.Quantity);
        Assert.Equal(1m, cart.CartItems[1].Model.Quantity);
        mockDialog.Verify(d => d.ShowError("Error", RollbackMessage), Times.Once);
    }

    [Fact]
    public async Task CommitItemQuantityAsync_ResyncFailure_ShowsStaleWarningAndLogsBothFailures()
    {
        string commitMarker = $"commit-crash-{Guid.NewGuid():N}";
        string resyncMarker = $"resync-crash-{Guid.NewGuid():N}";

        var mockSales = new Mock<ISalesService>();
        var mockDialog = new Mock<IDialogService>();
        var cart = CreateCart(mockSales, mockDialog, PendingSale((1, 1m)));

        cart.CartItems[0].Model.Quantity = 5m;

        mockSales.Setup(s => s.UpdateItemQuantityAsync(101, 1, 5m, Rate))
                 .ThrowsAsync(new InvalidOperationException(commitMarker));
        mockSales.Setup(s => s.GetSaleAsync(101)).ThrowsAsync(new InvalidOperationException(resyncMarker));

        await cart.CommitItemQuantityAsync(1, 5m);

        mockDialog.Verify(d => d.ShowError("Error", StaleMessage), Times.Once);
        // Sin re-sync no hay valor autoritativo que restaurar: el estado queda potencialmente stale,
        // pero el operador fue advertido (nunca un éxito silencioso).
        Assert.Equal(5m, cart.CartItems[0].Model.Quantity);

        var log = ReadResilienceLogUntil(commitMarker, resyncMarker);
        Assert.Contains(commitMarker, log);
        Assert.Contains(resyncMarker, log);
    }

    [Fact]
    public async Task CommitAndFlush_WithoutDialogService_DoNotThrow()
    {
        var mockSales = new Mock<ISalesService>();
        var cart = CreateCart(mockSales, mockDialog: null, PendingSale((1, 1m)));

        cart.CartItems[0].Model.Quantity = 5m;
        mockSales.Setup(s => s.UpdateItemQuantityAsync(101, 1, 5m, Rate))
                 .ThrowsAsync(new InvalidOperationException("down"));
        mockSales.Setup(s => s.GetSaleAsync(101)).ThrowsAsync(new InvalidOperationException("still-down"));

        var commitError = await Record.ExceptionAsync(() => cart.CommitItemQuantityAsync(1, 5m));
        var flushError = await Record.ExceptionAsync(() => cart.FlushAllQuantitiesAsync());

        Assert.Null(commitError);
        Assert.Null(flushError);
    }

    [Fact]
    public async Task CommitItemQuantityAsync_SuccessfulUpdate_PersistsRecoverySnapshotThroughPropertySetter()
    {
        var mockSales = new Mock<ISalesService>();
        var mockRecovery = new Mock<ISaleRecoveryStore>();
        var cart = CreateCart(mockSales, mockDialog: null, PendingSale((1, 1m)), mockRecovery.Object);

        // El snapshot inicial se persistió al asignar CurrentSale en el setup.
        mockRecovery.Invocations.Clear();

        var updated = PendingSale((1, 2m));
        mockSales.Setup(s => s.UpdateItemQuantityAsync(101, 1, 2m, Rate)).ReturnsAsync(updated);

        await cart.CommitItemQuantityAsync(1, 2m);

        Assert.Same(updated, cart.CurrentSale);
        Assert.Equal(2m, cart.CartItems[0].Model.Quantity);
        // El setter dispara UpdateCollection -> PersistRecoveryState: colección y recuperación coherentes.
        mockRecovery.Verify(
            r => r.Save(It.Is<SaleRecoverySnapshot>(s => s.SaleId == 101 && s.ItemCount == 1)),
            Times.Once);
    }

    [Fact]
    public async Task PersistRecoveryState_FailingRecoveryWrite_LogsAndCartFlowContinues()
    {
        var mockSales = new Mock<ISalesService>();
        var mockRecovery = new Mock<ISaleRecoveryStore>();
        var cart = CreateCart(mockSales, mockDialog: null, PendingSale((1, 1m)), mockRecovery.Object);

        // A partir de aquí toda escritura de recuperación falla (escenario 4 del spec).
        mockRecovery.Invocations.Clear();
        string persistMarker = $"persist-crash-{Guid.NewGuid():N}";
        mockRecovery.Setup(r => r.Save(It.IsAny<SaleRecoverySnapshot>()))
                    .Throws(new InvalidOperationException(persistMarker));

        var updated = PendingSale((1, 2m));
        mockSales.Setup(s => s.UpdateItemQuantityAsync(101, 1, 2m, Rate)).ReturnsAsync(updated);

        // El fallo de persistencia no debe propagarse ni interrumpir el commit (best-effort).
        var commitError = await Record.ExceptionAsync(() => cart.CommitItemQuantityAsync(1, 2m));

        Assert.Null(commitError);
        Assert.Same(updated, cart.CurrentSale);
        Assert.Equal(2m, cart.CartItems[0].Model.Quantity);
        mockRecovery.Verify(r => r.Save(It.IsAny<SaleRecoverySnapshot>()), Times.Once);

        var log = ReadResilienceLogUntil(persistMarker);
        Assert.Contains(persistMarker, log);
    }

    private static string ReadResilienceLogUntil(params string[] markers)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        string content = string.Empty;

        while (true)
        {
            try
            {
                if (File.Exists(ClientStateLogger.ResilienceLogPath))
                {
                    using var stream = new FileStream(ClientStateLogger.ResilienceLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    content = reader.ReadToEnd();
                }
            }
            catch (IOException)
            {
                content = string.Empty;
            }

            if (markers.All(m => content.Contains(m, StringComparison.Ordinal)) || DateTime.UtcNow >= deadline)
            {
                return content;
            }

            Thread.Sleep(100);
        }
    }
}
