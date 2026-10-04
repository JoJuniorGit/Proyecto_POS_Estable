using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class PendingPickupsViewModelTests
{
    private readonly Mock<ISalesService> _salesServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();

    private PendingPickupsViewModel CreateViewModel(Action<DeliveryReceiptClientDto, byte[]>? openDeliveryNote = null)
    {
        return new PendingPickupsViewModel(_salesServiceMock.Object, _dialogServiceMock.Object, openDeliveryNote);
    }

    [Fact]
    public async Task EnsureLoadedAsync_WithDuplicateSalesFromApi_DeduplicatesBySaleId()
    {
        var duplicateItems = new List<PendingPickupClientDto>
        {
            new() { SaleId = 10, InvoiceNumber = 101, CustomerName = "Cliente 1", Date = DateTime.UtcNow.AddMinutes(-10) },
            new() { SaleId = 10, InvoiceNumber = 101, CustomerName = "Cliente 1", Date = DateTime.UtcNow.AddMinutes(-10) },
            new() { SaleId = 11, InvoiceNumber = 102, CustomerName = "Cliente 2", Date = DateTime.UtcNow.AddMinutes(-5) }
        };

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((duplicateItems, 2));

        var vm = CreateViewModel();

        await vm.EnsureLoadedAsync();

        Assert.Equal(2, vm.Pickups.Count);
        Assert.Single(vm.Pickups, p => p.SaleId == 10);
        Assert.Single(vm.Pickups, p => p.SaleId == 11);
    }

    [Fact]
    public async Task EnsureLoadedAsync_WhenAlreadyLoading_PreventsConcurrentExecution()
    {
        var tcs = new TaskCompletionSource<(IEnumerable<PendingPickupClientDto> Items, int TotalCount)>();

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .Returns(tcs.Task);

        var vm = CreateViewModel();

        var firstLoadTask = vm.EnsureLoadedAsync();
        var secondLoadTask = vm.EnsureLoadedAsync();

        _salesServiceMock.Verify(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0), Times.Once);

        tcs.SetResult((new List<PendingPickupClientDto>(), 0));
        await Task.WhenAll(firstLoadTask, secondLoadTask);
    }

    [Fact]
    public async Task ConfirmPickupAsync_WhenDeliveryIsCompleted_RemovesItemFromPickupsCollection()
    {
        var pickup = CreatePickup(42, totalUnits: 2m);

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { pickup }, 1));

        _dialogServiceMock
            .Setup(d => d.ShowPartialDeliveryDialogAsync(It.IsAny<PendingPickupClientDto>()))
            .ReturnsAsync(CreateDialogResult(new PartialDeliveryItemRequestDto { SaleItemId = 142, Quantity = 2m }));

        _dialogServiceMock
            .Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        _salesServiceMock
            .Setup(s => s.DeliverPartialAsync(
                42,
                It.IsAny<IReadOnlyList<PartialDeliveryItemRequestDto>>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateReceipt(42, "Delivered"));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        Assert.Single(vm.Pickups);

        await vm.ConfirmPickupCommand.ExecuteAsync(pickup);

        _salesServiceMock.Verify(s => s.DeliverPartialAsync(
            42,
            It.Is<IReadOnlyList<PartialDeliveryItemRequestDto>>(items => items.Count == 1 && items[0].Quantity == 2m),
            It.IsAny<string?>(),
            It.Is<string>(key => key.Length == 32),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(vm.Pickups);
        Assert.NotNull(vm.SuccessMessage);
        Assert.Contains("Factura N° 00205", vm.SuccessMessage);
    }

    [Fact]
    public async Task ConfirmPickupAsync_WhenDialogIsCancelled_DoesNotSendRequestOrRemove()
    {
        var pickup = CreatePickup(42);

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { pickup }, 1));

        _dialogServiceMock
            .Setup(d => d.ShowPartialDeliveryDialogAsync(It.IsAny<PendingPickupClientDto>()))
            .ReturnsAsync((PartialDeliveryDialogResult?)null);

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        await vm.ConfirmPickupCommand.ExecuteAsync(pickup);

        _salesServiceMock.Verify(s => s.DeliverPartialAsync(
            It.IsAny<int>(),
            It.IsAny<IReadOnlyList<PartialDeliveryItemRequestDto>>(),
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(vm.Pickups);
    }

    [Fact]
    public async Task DeliverAll_PrefillsAllPending_AndPostsAllPending()
    {
        var pickup = CreatePickup(42, totalUnits: 3m, deliveredUnits: 0.5m, deliveryStatus: "PartiallyDelivered");
        pickup.Items.Add(new PendingPickupItemDto
        {
            SaleItemId = 143,
            ProductId = 10,
            ProductName = "Segundo producto",
            Quantity = 4m,
            DeliveredQuantity = 1m,
            PendingQuantity = 3m
        });
        pickup.Items.Add(new PendingPickupItemDto
        {
            SaleItemId = 144,
            ProductId = 11,
            ProductName = "Producto ya entregado",
            Quantity = 1m,
            DeliveredQuantity = 1m,
            PendingQuantity = 0m
        });

        PartialDeliveryDialogResult? receivedDraft = null;
        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { pickup }, 1));
        _dialogServiceMock
            .Setup(d => d.ShowPartialDeliveryDialogAsync(It.IsAny<PendingPickupClientDto>()))
            .Returns((PendingPickupClientDto receivedPickup) =>
            {
                receivedDraft = receivedPickup.PendingDraft;
                return Task.FromResult(receivedPickup.PendingDraft);
            });
        _dialogServiceMock
            .Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);
        _salesServiceMock
            .Setup(s => s.DeliverPartialAsync(
                42,
                It.IsAny<IReadOnlyList<PartialDeliveryItemRequestDto>>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateReceipt(42, "Delivered"));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        await vm.DeliverAllCommand.ExecuteAsync(pickup);

        Assert.NotNull(receivedDraft);
        Assert.Equal(new[] { (142, 2.5m), (143, 3m) },
            receivedDraft!.Items.Select(item => (item.SaleItemId, item.Quantity)));
        _salesServiceMock.Verify(s => s.DeliverPartialAsync(
            42,
            It.Is<IReadOnlyList<PartialDeliveryItemRequestDto>>(items =>
                items.Count == 2
                && items[0].SaleItemId == 142 && items[0].Quantity == 2.5m
                && items[1].SaleItemId == 143 && items[1].Quantity == 3m),
            It.IsAny<string?>(),
            It.Is<string>(key => key.Length == 32),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeliverAll_WhenNoPendingItems_DoesNotPrefillOrPost()
    {
        var pickup = CreatePickup(43, totalUnits: 1m, deliveredUnits: 1m, deliveryStatus: "PartiallyDelivered");
        PartialDeliveryDialogResult? receivedDraft = null;
        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { pickup }, 1));
        _dialogServiceMock
            .Setup(d => d.ShowPartialDeliveryDialogAsync(It.IsAny<PendingPickupClientDto>()))
            .Returns((PendingPickupClientDto receivedPickup) =>
            {
                receivedDraft = receivedPickup.PendingDraft;
                return Task.FromResult(receivedPickup.PendingDraft);
            });

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        await vm.DeliverAllCommand.ExecuteAsync(pickup);

        Assert.NotNull(receivedDraft);
        Assert.Empty(receivedDraft!.Items);
        _salesServiceMock.Verify(s => s.DeliverPartialAsync(
            It.IsAny<int>(),
            It.IsAny<IReadOnlyList<PartialDeliveryItemRequestDto>>(),
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmPickupAsync_WhenDeliveryIsPartial_ReloadsUpdatedPickup()
    {
        var pickup = CreatePickup(42, totalUnits: 5m);
        var updatedPickup = CreatePickup(42, totalUnits: 5m, deliveredUnits: 2m, deliveryStatus: "PartiallyDelivered");

        _salesServiceMock
            .SetupSequence(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { pickup }, 1))
            .ReturnsAsync((new List<PendingPickupClientDto> { updatedPickup }, 1));

        _dialogServiceMock
            .Setup(d => d.ShowPartialDeliveryDialogAsync(It.IsAny<PendingPickupClientDto>()))
            .ReturnsAsync(CreateDialogResult(new PartialDeliveryItemRequestDto { SaleItemId = 142, Quantity = 2m }));
        _dialogServiceMock
            .Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);
        _salesServiceMock
            .Setup(s => s.DeliverPartialAsync(
                42,
                It.IsAny<IReadOnlyList<PartialDeliveryItemRequestDto>>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateReceipt(42, "PartiallyDelivered"));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        await vm.ConfirmPickupCommand.ExecuteAsync(pickup);

        _salesServiceMock.Verify(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0), Times.Exactly(2));
        var refreshed = Assert.Single(vm.Pickups);
        Assert.Same(updatedPickup, refreshed);
        Assert.Equal(2m, refreshed.DeliveredUnits);
        Assert.Equal(3m, Assert.Single(refreshed.Items).PendingQuantity);
    }

    [Fact]
    public async Task ConfirmPickupAsync_WhenDeliveryConflicts_ShowsServerErrorAndReloads()
    {
        const string errorMessage = "Otro usuario modificó el retiro simultáneamente. Actualice la lista e intente de nuevo.";
        var pickup = CreatePickup(42, totalUnits: 5m);
        var updatedPickup = CreatePickup(42, totalUnits: 5m, deliveredUnits: 2m, deliveryStatus: "PartiallyDelivered");
        _salesServiceMock
            .SetupSequence(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { pickup }, 1))
            .ReturnsAsync((new List<PendingPickupClientDto> { updatedPickup }, 1));
        _dialogServiceMock
            .SetupSequence(d => d.ShowPartialDeliveryDialogAsync(It.IsAny<PendingPickupClientDto>()))
            .ReturnsAsync(new PartialDeliveryDialogResult
            {
                Items = new List<PartialDeliveryItemRequestDto> { new() { SaleItemId = 142, Quantity = 1m } },
                Notes = "Cliente retira el resto luego."
            })
            .ReturnsAsync((PartialDeliveryDialogResult?)null);
        _salesServiceMock
            .Setup(s => s.DeliverPartialAsync(
                42,
                It.IsAny<IReadOnlyList<PartialDeliveryItemRequestDto>>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception(errorMessage));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        await vm.ConfirmPickupCommand.ExecuteAsync(pickup);

        _dialogServiceMock.Verify(d => d.ShowError("Error", errorMessage), Times.Once);
        _salesServiceMock.Verify(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0), Times.Exactly(2));
        _salesServiceMock.Verify(s => s.DeliverPartialAsync(
            42,
            It.IsAny<IReadOnlyList<PartialDeliveryItemRequestDto>>(),
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _dialogServiceMock.Verify(d => d.ShowPartialDeliveryDialogAsync(It.Is<PendingPickupClientDto>(refreshed =>
            refreshed.PendingDraft != null
            && refreshed.PendingDraft.Items.Single().Quantity == 1m
            && refreshed.PendingDraft.Notes == "Cliente retira el resto luego.")), Times.Once);
        Assert.Same(updatedPickup, Assert.Single(vm.Pickups));
    }

    [Fact]
    public async Task ConfirmPickupAsync_WhenPrintIsAccepted_DownloadsAndOpensDeliveryNote()
    {
        var pickup = CreatePickup(42, totalUnits: 2m);
        var receipt = CreateReceipt(42, "Delivered", deliveryId: 77);
        var noteBytes = new byte[] { 1, 2, 3 };
        DeliveryReceiptClientDto? openedReceipt = null;
        byte[]? openedBytes = null;

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { pickup }, 1));
        _dialogServiceMock
            .Setup(d => d.ShowPartialDeliveryDialogAsync(It.IsAny<PendingPickupClientDto>()))
            .ReturnsAsync(CreateDialogResult(new PartialDeliveryItemRequestDto { SaleItemId = 142, Quantity = 2m }));
        _dialogServiceMock
            .Setup(d => d.ShowConfirm("Nota de Despacho", "Retiro registrado. ¿Desea imprimir la Nota de Despacho?"))
            .Returns(true);
        _salesServiceMock
            .Setup(s => s.DeliverPartialAsync(
                42,
                It.IsAny<IReadOnlyList<PartialDeliveryItemRequestDto>>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(receipt);
        _salesServiceMock
            .Setup(s => s.GetDeliveryNoteAsync(42, 77, It.IsAny<CancellationToken>()))
            .ReturnsAsync(noteBytes);

        var vm = CreateViewModel((opened, bytes) =>
        {
            openedReceipt = opened;
            openedBytes = bytes;
        });
        await vm.EnsureLoadedAsync();

        await vm.ConfirmPickupCommand.ExecuteAsync(pickup);

        _salesServiceMock.Verify(s => s.GetDeliveryNoteAsync(42, 77, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Same(receipt, openedReceipt);
        Assert.Equal(noteBytes, openedBytes);
    }

    [Fact]
    public void PendingPickupClientDto_WithPartialDelivery_ProvidesLocalizedProgress()
    {
        var pickup = CreatePickup(42, totalUnits: 10m, deliveredUnits: 4m, deliveryStatus: "PartiallyDelivered");

        Assert.True(pickup.IsPartiallyDelivered);
        Assert.Equal("Entrega Parcial", pickup.DeliveryStatusLabel);
        Assert.Equal("Retirado: 4/10", pickup.ProgressText);
        Assert.Equal(40d, pickup.ProgressPercent);
    }

    [Theory]
    [InlineData("Carlos", true)]
    [InlineData("V-12345678", true)]
    [InlineData("999", true)]
    [InlineData("Inexistente", false)]
    public void MatchesSearch_WhenSearchingByCustomerOrInvoice_ReturnsExpectedMatch(string search, bool expectedMatch)
    {
        var vm = CreateViewModel();
        vm.SearchQuery = search;

        var dto = new PendingPickupClientDto
        {
            SaleId = 999,
            CustomerName = "Carlos Perez",
            CustomerCedula = "V-12345678"
        };

        var result = vm.MatchesSearch(dto);

        Assert.Equal(expectedMatch, result);
    }

    [Fact]
    public void MatchesSearch_WhenDtoIsNull_ReturnsFalse()
    {
        var vm = CreateViewModel();
        vm.SearchQuery = "algo";

        var result = vm.MatchesSearch(null);

        Assert.False(result);
    }

    [Fact]
    public async Task EnsureLoadedAsync_WithNullItemsFromApi_FiltersNullsSafely()
    {
        var itemsWithNulls = new List<PendingPickupClientDto?>
        {
            null,
            new() { SaleId = 20, InvoiceNumber = 201, CustomerName = "Cliente Valido", Date = DateTime.UtcNow },
            null
        };

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((itemsWithNulls!, 1));

        var vm = CreateViewModel();

        await vm.EnsureLoadedAsync();

        Assert.Single(vm.Pickups);
        Assert.Equal(20, vm.Pickups[0].SaleId);
    }

    [Fact]
    public async Task EnsureLoadedAsync_WhenCalledMultipleTimes_ClearsAndReloadsWithoutExceptions()
    {
        var firstBatch = new List<PendingPickupClientDto>
        {
            new() { SaleId = 1, InvoiceNumber = 10, CustomerName = "Cliente Uno", Date = DateTime.UtcNow }
        };
        var secondBatch = new List<PendingPickupClientDto>
        {
            new() { SaleId = 2, InvoiceNumber = 20, CustomerName = "Cliente Dos", Date = DateTime.UtcNow }
        };

        _salesServiceMock
            .SetupSequence(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((firstBatch, 1))
            .ReturnsAsync((secondBatch, 1));

        var vm = CreateViewModel();

        await vm.EnsureLoadedAsync();
        Assert.Single(vm.Pickups);
        Assert.Equal(1, vm.Pickups[0].SaleId);

        await vm.EnsureLoadedAsync();
        Assert.Single(vm.Pickups);
        Assert.Equal(2, vm.Pickups[0].SaleId);
    }

    private static PendingPickupClientDto CreatePickup(
        int saleId,
        decimal totalUnits = 3m,
        decimal deliveredUnits = 0m,
        string deliveryStatus = "PendingPickup")
    {
        return new PendingPickupClientDto
        {
            SaleId = saleId,
            InvoiceNumber = 205,
            Date = DateTime.UtcNow,
            CustomerName = "Carlos Perez",
            TotalUSD = 50m,
            TotalUnits = totalUnits,
            DeliveredUnits = deliveredUnits,
            DeliveryStatus = deliveryStatus,
            Items = new List<PendingPickupItemDto>
            {
                new()
                {
                    SaleItemId = 100 + saleId,
                    ProductId = 9,
                    ProductName = "Producto",
                    Quantity = totalUnits,
                    DeliveredQuantity = deliveredUnits,
                    PendingQuantity = totalUnits - deliveredUnits
                }
            }
        };
    }

    private static PartialDeliveryDialogResult CreateDialogResult(params PartialDeliveryItemRequestDto[] items) => new()
    {
        Items = items.ToList()
    };

    private static DeliveryReceiptClientDto CreateReceipt(int saleId, string deliveryStatus, int deliveryId = 70) => new()
    {
        DeliveryId = deliveryId,
        SaleId = saleId,
        DeliveryStatus = deliveryStatus
    };
}
