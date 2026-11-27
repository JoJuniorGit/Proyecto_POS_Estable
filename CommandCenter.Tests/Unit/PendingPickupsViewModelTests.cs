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

    [Fact]
    public async Task Kpis_WithPartialPickup_ComputeOperationalTotals()
    {
        var partial = CreatePricedPickup(
            saleId: 501,
            invoiceNumber: 289,
            date: new DateTime(2026, 3, 3, 10, 0, 0),
            customerName: "Carlos Perez",
            customerCedula: "V-12345678",
            totalUnits: 10m,
            deliveredUnits: 4m,
            unitPriceBsS: 10m,
            totalBsS: 100m,
            appliedRate: 2m,
            deliveryStatus: "PartiallyDelivered");

        var pending = CreatePricedPickup(
            saleId: 502,
            invoiceNumber: 290,
            date: new DateTime(2026, 3, 2, 10, 0, 0),
            customerName: "Ana Lopez",
            customerCedula: "V-12345678",
            totalUnits: 5m,
            deliveredUnits: 0m,
            unitPriceBsS: 3m,
            totalBsS: 15m,
            appliedRate: 2m);

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { partial, pending }, 2));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        Assert.Equal(2, vm.ActiveCustodyCount);
        Assert.Equal(1, vm.DistinctCustomersCount);
        Assert.Equal("1 cliente con retiros abiertos", vm.ActiveCustodySubtitle);
        Assert.Equal(11m, vm.UnitsToDeliver);
        Assert.Equal("2 líneas de producto por despachar", vm.UnitsToDeliverSubtitle);
        Assert.Equal(1, vm.PartialPickupsCount);
        Assert.Equal("1", vm.PartialPickupsValue);
        Assert.Equal("Facturas con retiro parcial en curso", vm.PartialPickupsSubtitle);
        Assert.Equal(75m, vm.CustodyValueBsS);
        Assert.Equal(37.5m, vm.CustodyValueUSD);
    }

    [Fact]
    public async Task Kpis_WhenNoPartialDeliveries_ReportNeutralPartialState()
    {
        var pickup = CreatePricedPickup(
            saleId: 601,
            invoiceNumber: 301,
            date: new DateTime(2026, 3, 1, 9, 0, 0),
            customerName: "Luis Diaz",
            customerCedula: "V-9",
            totalUnits: 4m,
            deliveredUnits: 0m,
            unitPriceBsS: 5m,
            totalBsS: 20m,
            appliedRate: 2m);

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { pickup }, 1));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        Assert.Equal(0, vm.PartialPickupsCount);
        Assert.Equal("0 parciales", vm.PartialPickupsValue);
        Assert.Equal("Sin entregas parciales registradas", vm.PartialPickupsSubtitle);
        Assert.Equal("1 cliente con retiros abiertos", vm.ActiveCustodySubtitle);
        Assert.Equal(4m, vm.UnitsToDeliver);
        Assert.Equal("1 línea de producto por despachar", vm.UnitsToDeliverSubtitle);
    }

    [Fact]
    public async Task DistinctCustomers_CountsSameCedulaOnceAndFallsBackToName()
    {
        var cedulaA = CreatePricedPickup(701, null, new DateTime(2026, 3, 4), "Cliente Uno", "V-111", 2m, 0m, 1m, 2m, 2m);
        var cedulaDuplicate = CreatePricedPickup(702, null, new DateTime(2026, 3, 3), "Otro Nombre", " v-111 ", 2m, 0m, 1m, 2m, 2m);
        var nameOnly = CreatePricedPickup(703, null, new DateTime(2026, 3, 2), "Maria Gil", string.Empty, 2m, 0m, 1m, 2m, 2m);
        var nameOnlyDuplicate = CreatePricedPickup(704, null, new DateTime(2026, 3, 1), " maria gil ", string.Empty, 2m, 0m, 1m, 2m, 2m);

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { cedulaA, cedulaDuplicate, nameOnly, nameOnlyDuplicate }, 4));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        Assert.Equal(2, vm.DistinctCustomersCount);
        Assert.Equal("2 clientes con retiros abiertos", vm.ActiveCustodySubtitle);
    }

    [Theory]
    [InlineData("290", true)]
    [InlineData("carlos", true)]
    [InlineData("V-1234", true)]
    [InlineData("harina", true)]
    [InlineData("Inexistente", false)]
    public void MatchesSearch_ByInvoiceCustomerCedulaAndProduct_ReturnsExpected(string search, bool expected)
    {
        var vm = CreateViewModel();
        vm.SearchQuery = search;

        var dto = new PendingPickupClientDto
        {
            SaleId = 55,
            InvoiceNumber = 290,
            CustomerName = "Carlos Perez",
            CustomerCedula = "V-12345678",
            Items = new List<PendingPickupItemDto>
            {
                new() { ProductName = "Harina de Maíz" }
            }
        };

        Assert.Equal(expected, vm.MatchesSearch(dto));
    }

    [Fact]
    public void MatchesSearch_WhenQueryIsBlank_ReturnsTrue()
    {
        var vm = CreateViewModel();
        vm.SearchQuery = "   ";

        Assert.True(vm.MatchesSearch(new PendingPickupClientDto { SaleId = 1 }));
    }

    [Fact]
    public async Task TextSearch_ByProductName_FiltersCollectionAndUpdatesLabels()
    {
        var withProduct = CreatePricedPickup(801, 401, new DateTime(2026, 3, 5), "Carlos", "V-1", 2m, 0m, 1m, 2m, 2m);
        withProduct.Items[0].ProductName = "Harina de Maíz";
        var withoutProduct = CreatePricedPickup(802, 402, new DateTime(2026, 3, 4), "Carlos", "V-2", 2m, 0m, 1m, 2m, 2m);
        withoutProduct.Items[0].ProductName = "Café molido";

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { withProduct, withoutProduct }, 2));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        vm.SearchQuery = "harina";

        Assert.Single(vm.FilteredPickups);
        Assert.Same(withProduct, vm.FilteredPickups[0]);
        Assert.Equal("Todas (1)", vm.AllFilterLabel);
        Assert.Equal("1 mostrada", vm.VisiblePickupCountLabel);
        Assert.False(vm.IsListEmpty);
    }

    [Fact]
    public async Task StatusFilter_AppliesDeliveryStateAndExposesSearchScopedCounts()
    {
        var notPickedUp = CreatePricedPickup(901, 501, new DateTime(2026, 3, 5), "Carlos", "V-1", 5m, 0m, 1m, 5m, 2m);
        var partial = CreatePricedPickup(902, 502, new DateTime(2026, 3, 4), "Carlos", "V-2", 5m, 2m, 1m, 5m, 2m, "PartiallyDelivered");
        var delivered = CreatePricedPickup(903, 503, new DateTime(2026, 3, 3), "Ana", "V-3", 5m, 5m, 1m, 5m, 2m, "Delivered");

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { notPickedUp, partial, delivered }, 3));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        Assert.Equal(3, vm.FilteredPickups.Count);
        Assert.Equal("Todas (3)", vm.AllFilterLabel);
        Assert.Equal("Sin retirar (1)", vm.NotPickedUpFilterLabel);
        Assert.Equal("Parciales (1)", vm.PartialFilterLabel);
        Assert.Equal("3 mostradas", vm.VisiblePickupCountLabel);

        vm.SetStatusFilterCommand.Execute(PickupStatusFilter.NotPickedUp);

        Assert.Single(vm.FilteredPickups);
        Assert.Same(notPickedUp, vm.FilteredPickups[0]);
        Assert.Equal("1 mostrada", vm.VisiblePickupCountLabel);

        vm.SetStatusFilterCommand.Execute(PickupStatusFilter.Partial);

        Assert.Single(vm.FilteredPickups);
        Assert.Same(partial, vm.FilteredPickups[0]);

        vm.SelectedStatusFilter = PickupStatusFilter.All;

        Assert.Equal(3, vm.FilteredPickups.Count);
    }

    [Fact]
    public async Task FilterLabels_CountSearchMatchesBeforeStatusFilter()
    {
        var carlosPending = CreatePricedPickup(1001, 601, new DateTime(2026, 3, 5), "Carlos", "V-1", 5m, 0m, 1m, 5m, 2m);
        var carlosPartial = CreatePricedPickup(1002, 602, new DateTime(2026, 3, 4), "Carlos", "V-2", 5m, 2m, 1m, 5m, 2m, "PartiallyDelivered");
        var anaPending = CreatePricedPickup(1003, 603, new DateTime(2026, 3, 3), "Ana", "V-3", 5m, 0m, 1m, 5m, 2m);

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { carlosPending, carlosPartial, anaPending }, 3));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        vm.SearchQuery = "carlos";

        Assert.Equal("Todas (2)", vm.AllFilterLabel);
        Assert.Equal("Sin retirar (1)", vm.NotPickedUpFilterLabel);
        Assert.Equal("Parciales (1)", vm.PartialFilterLabel);

        vm.SelectedStatusFilter = PickupStatusFilter.Partial;

        Assert.Single(vm.FilteredPickups);
        Assert.Equal("Todas (2)", vm.AllFilterLabel);
        Assert.Equal("Parciales (1)", vm.PartialFilterLabel);
    }

    [Fact]
    public void SortOptions_ExposeContractualLabelsInOrder()
    {
        var vm = CreateViewModel();

        Assert.Equal(
            new[] { "Más recientes primero", "Más antiguas primero", "Mayor cantidad pendiente" },
            vm.SortOptions.Select(option => option.Label));
        Assert.Same(vm.SortOptions[0], vm.SelectedSortOption);
        Assert.Equal(PickupStatusFilter.All, vm.SelectedStatusFilter);
    }

    [Fact]
    public async Task Sorting_AppliesNewestOldestAndMostPendingModes()
    {
        var newest = CreatePricedPickup(1101, 701, new DateTime(2026, 3, 3), "Cliente A", "V-1", 5m, 0m, 1m, 5m, 2m);
        var middle = CreatePricedPickup(1102, 702, new DateTime(2026, 3, 2), "Cliente B", "V-2", 5m, 0m, 1m, 5m, 2m);
        var oldest = CreatePricedPickup(1103, 703, new DateTime(2026, 3, 1), "Cliente C", "V-3", 100m, 0m, 1m, 100m, 2m);

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { newest, middle, oldest }, 3));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        Assert.Equal(new[] { 1101, 1102, 1103 }, vm.FilteredPickups.Select(p => p.SaleId));

        vm.SelectedSortOption = vm.SortOptions.Single(option => option.Mode == PickupSortMode.OldestFirst);

        Assert.Equal(new[] { 1103, 1102, 1101 }, vm.FilteredPickups.Select(p => p.SaleId));

        vm.SelectedSortOption = vm.SortOptions.Single(option => option.Mode == PickupSortMode.MostPendingUnits);

        Assert.Equal(new[] { 1103, 1101, 1102 }, vm.FilteredPickups.Select(p => p.SaleId));
    }

    [Fact]
    public async Task AutoSelection_AfterLoad_SelectsFirstFilteredPickup()
    {
        var newest = CreatePricedPickup(1201, 801, new DateTime(2026, 3, 3), "Cliente A", "V-1", 2m, 0m, 1m, 2m, 2m);
        var older = CreatePricedPickup(1202, 802, new DateTime(2026, 3, 2), "Cliente B", "V-2", 2m, 0m, 1m, 2m, 2m);

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { older, newest }, 2));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        Assert.Same(newest, vm.SelectedCustody);
        Assert.Same(vm.FilteredPickups[0], vm.SelectedCustody);
    }

    [Fact]
    public async Task AutoSelection_WhenSelectedFallsOutsideFilter_SelectsFirstVisible()
    {
        var notPickedUp = CreatePricedPickup(1301, 901, new DateTime(2026, 3, 3), "Cliente A", "V-1", 5m, 0m, 1m, 5m, 2m);
        var partial = CreatePricedPickup(1302, 902, new DateTime(2026, 3, 2), "Cliente B", "V-2", 5m, 2m, 1m, 5m, 2m, "PartiallyDelivered");

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { notPickedUp, partial }, 2));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        Assert.Same(notPickedUp, vm.SelectedCustody);

        vm.SelectedStatusFilter = PickupStatusFilter.Partial;

        Assert.Same(partial, vm.SelectedCustody);
        Assert.Same(partial, vm.FilteredPickups[0]);
    }

    [Fact]
    public async Task AutoSelection_WhenNoMatches_BecomesNull()
    {
        var pickup = CreatePricedPickup(1401, 1001, new DateTime(2026, 3, 3), "Cliente A", "V-1", 2m, 0m, 1m, 2m, 2m);

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { pickup }, 1));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        Assert.NotNull(vm.SelectedCustody);

        vm.SearchQuery = "sin coincidencias";

        Assert.Empty(vm.FilteredPickups);
        Assert.Null(vm.SelectedCustody);
    }

    [Fact]
    public async Task AutoSelection_AfterFullDelivery_SelectsNextVisiblePickup()
    {
        var delivered = CreatePricedPickup(1501, 1101, new DateTime(2026, 3, 3), "Cliente A", "V-1", 2m, 0m, 10m, 20m, 2m);
        var remaining = CreatePricedPickup(1502, 1102, new DateTime(2026, 3, 2), "Cliente B", "V-2", 3m, 0m, 10m, 30m, 2m);

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { delivered, remaining }, 2));
        _dialogServiceMock
            .Setup(d => d.ShowPartialDeliveryDialogAsync(It.IsAny<PendingPickupClientDto>()))
            .ReturnsAsync(CreateDialogResult(new PartialDeliveryItemRequestDto { SaleItemId = 2501, Quantity = 2m }));
        _salesServiceMock
            .Setup(s => s.DeliverPartialAsync(
                1501,
                It.IsAny<IReadOnlyList<PartialDeliveryItemRequestDto>>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateReceipt(1501, "Delivered"));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        Assert.Same(delivered, vm.SelectedCustody);

        await vm.ConfirmPickupCommand.ExecuteAsync(delivered);

        Assert.Single(vm.Pickups);
        Assert.Same(remaining, vm.SelectedCustody);
        Assert.Same(remaining, vm.FilteredPickups[0]);
    }

    [Fact]
    public async Task ClearSearchCommand_ClearsQueryAndRestoresFullList()
    {
        var first = CreatePricedPickup(1601, 1201, new DateTime(2026, 3, 3), "Cliente A", "V-1", 2m, 0m, 1m, 2m, 2m);
        var second = CreatePricedPickup(1602, 1202, new DateTime(2026, 3, 2), "Cliente B", "V-2", 2m, 0m, 1m, 2m, 2m);

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { first, second }, 2));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        vm.SearchQuery = "Cliente A";

        Assert.Single(vm.FilteredPickups);

        vm.ClearSearchCommand.Execute(null);

        Assert.Equal(string.Empty, vm.SearchQuery);
        Assert.Equal(2, vm.FilteredPickups.Count);
        Assert.False(vm.HasActiveFilters);
    }

    [Fact]
    public async Task ClearFiltersCommand_ResetsSearchStatusAndSort()
    {
        var carlosPending = CreatePricedPickup(1701, 1301, new DateTime(2026, 3, 5), "Carlos", "V-1", 5m, 0m, 1m, 5m, 2m);
        var carlosPartial = CreatePricedPickup(1702, 1302, new DateTime(2026, 3, 4), "Carlos", "V-2", 5m, 2m, 1m, 5m, 2m, "PartiallyDelivered");
        var anaPending = CreatePricedPickup(1703, 1303, new DateTime(2026, 3, 3), "Ana", "V-3", 5m, 0m, 1m, 5m, 2m);

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { carlosPending, carlosPartial, anaPending }, 3));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        vm.SearchQuery = "carlos";
        vm.SelectedStatusFilter = PickupStatusFilter.Partial;
        vm.SelectedSortOption = vm.SortOptions[2];

        Assert.True(vm.HasActiveFilters);

        vm.ClearFiltersCommand.Execute(null);

        Assert.Equal(string.Empty, vm.SearchQuery);
        Assert.Equal(PickupStatusFilter.All, vm.SelectedStatusFilter);
        Assert.Equal(vm.SortOptions[0], vm.SelectedSortOption);
        Assert.False(vm.HasActiveFilters);
        Assert.Equal(3, vm.FilteredPickups.Count);
        Assert.Equal("Todas (3)", vm.AllFilterLabel);
    }

    [Fact]
    public async Task CopyCustomerPhoneCommand_RaisesEventOnlyWhenPhoneIsPresent()
    {
        var pickup = CreatePricedPickup(
            saleId: 1801,
            invoiceNumber: 1401,
            date: new DateTime(2026, 3, 3),
            customerName: "Cliente A",
            customerCedula: "V-1",
            totalUnits: 2m,
            deliveredUnits: 0m,
            unitPriceBsS: 1m,
            totalBsS: 2m,
            appliedRate: 2m,
            customerPhone: "0412-2222222");

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { pickup }, 1));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        string? copiedPhone = null;
        vm.RequestClipboardCopy += phone => copiedPhone = phone;

        vm.CopyCustomerPhoneCommand.Execute(null);

        Assert.Equal("0412-2222222", copiedPhone);

        copiedPhone = null;
        vm.SelectedCustody!.CustomerPhone = "   ";
        vm.CopyCustomerPhoneCommand.Execute(null);

        Assert.Null(copiedPhone);
    }

    [Fact]
    public async Task EmptyState_WhenCollectionIsEmpty_ShowsCustodyEmptyCopy()
    {
        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto>(), 0));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        Assert.True(vm.IsListEmpty);
        Assert.False(vm.HasActiveFilters);
        Assert.Equal("No hay retiros pendientes", vm.EmptyStateTitle);
        Assert.False(string.IsNullOrWhiteSpace(vm.EmptyStateDescription));
        Assert.Null(vm.SelectedCustody);
        Assert.Equal("0 mostradas", vm.VisiblePickupCountLabel);
    }

    [Fact]
    public async Task EmptyState_WhenFilterExcludesAll_ShowsSearchEmptyCopy()
    {
        var pickup = CreatePricedPickup(1901, 1501, new DateTime(2026, 3, 3), "Cliente A", "V-1", 2m, 0m, 1m, 2m, 2m);

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { pickup }, 1));

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        vm.SearchQuery = "zzz";

        Assert.True(vm.IsListEmpty);
        Assert.True(vm.HasActiveFilters);
        Assert.Equal("Sin resultados para tu búsqueda", vm.EmptyStateTitle);
        Assert.False(string.IsNullOrWhiteSpace(vm.EmptyStateDescription));
    }

    [Fact]
    public void PendingPickupClientDto_WithPartialDelivery_ComputesPendingValuesAndDisplayLabels()
    {
        var dto = new PendingPickupClientDto
        {
            SaleId = 42,
            InvoiceNumber = 289,
            Date = new DateTime(2026, 11, 27, 1, 48, 0, DateTimeKind.Unspecified),
            TotalUSD = 50m,
            TotalBsS = 100m,
            AppliedRate = 2m,
            TotalUnits = 10m,
            DeliveredUnits = 4m,
            DeliveryStatus = "PartiallyDelivered",
            Items = new List<PendingPickupItemDto>
            {
                new()
                {
                    SaleItemId = 1,
                    ProductName = "Harina de Maíz",
                    Quantity = 10m,
                    DeliveredQuantity = 4m,
                    PendingQuantity = 6m,
                    UnitPriceBsS = 10m,
                    SubtotalBsS = 100m
                }
            }
        };

        Assert.Equal(6m, dto.PendingUnits);
        Assert.Equal(1, dto.PendingLineCount);
        Assert.Equal(60m, dto.PendingValueBsS);
        Assert.Equal(30m, dto.PendingValueUSD);
        Assert.Equal("Factura N° 00289", dto.InvoiceDisplay);
        Assert.Equal("Factura #00289", dto.InvoiceBadge);
        Assert.Equal("Parcial", dto.ShortStatusLabel);
        Assert.Equal("Retiro Parcial", dto.DetailStatusLabel);
        Assert.Equal("6 uds. pendientes", dto.PendingUnitsLabel);
        Assert.False(dto.HasLargePendingVolume);
        Assert.Equal("27/11/26", dto.DateDisplay);
        Assert.Equal("27/11/26 01:48", dto.PaymentDateDisplay);
        Assert.Equal("4 de 10 unidades entregadas (40%)", dto.DeliveryProgressLabel);
        Assert.Equal("1 producto · 6 unidades pendientes por entregar", dto.DispatchSummaryLabel);
    }

    [Fact]
    public void PendingPickupClientDto_WithoutPartialDelivery_UsesPendingLabels()
    {
        var dto = new PendingPickupClientDto
        {
            SaleId = 42,
            InvoiceNumber = null,
            TotalUnits = 179m,
            DeliveredUnits = 0m,
            DeliveryStatus = "PendingPickup"
        };

        Assert.Equal("Sin retirar", dto.ShortStatusLabel);
        Assert.Equal("Pendiente de Retiro", dto.DetailStatusLabel);
        Assert.Equal("179 uds. pendientes", dto.PendingUnitsLabel);
        Assert.True(dto.HasLargePendingVolume);
        Assert.Equal("Pedido #42", dto.InvoiceDisplay);
        Assert.Equal("Pedido #42", dto.InvoiceBadge);
        Assert.Equal("0 productos · 179 unidades pendientes por entregar", dto.DispatchSummaryLabel);
    }

    [Fact]
    public void PendingValueBsS_WhenItemsEmpty_FallsBackToProportionalTotal()
    {
        var dto = new PendingPickupClientDto
        {
            TotalBsS = 100m,
            TotalUnits = 10m,
            DeliveredUnits = 4m,
            Items = new List<PendingPickupItemDto>()
        };

        Assert.Equal(6m, dto.PendingUnits);
        Assert.Equal(60m, dto.PendingValueBsS);
        Assert.Equal(0, dto.PendingLineCount);
    }

    [Fact]
    public void PendingValueBsS_WhenItemPricesExceedInvoiceTotal_IsCappedAtTotal()
    {
        var dto = new PendingPickupClientDto
        {
            TotalUSD = 50m,
            TotalBsS = 100m,
            AppliedRate = 0m,
            TotalUnits = 1m,
            DeliveredUnits = 0m,
            Items = new List<PendingPickupItemDto>
            {
                new() { Quantity = 1m, PendingQuantity = 1m, UnitPriceBsS = 200m, SubtotalBsS = 200m }
            }
        };

        Assert.Equal(100m, dto.PendingValueBsS);
        Assert.Equal(50m, dto.PendingValueUSD);
    }

    [Fact]
    public void PendingValueUSD_WhenAppliedRateMissing_UsesEffectiveTotalProportion()
    {
        var dto = new PendingPickupClientDto
        {
            TotalUSD = 50m,
            TotalBsS = 100m,
            AppliedRate = 0m,
            TotalUnits = 10m,
            DeliveredUnits = 4m
        };

        Assert.Equal(60m, dto.PendingValueBsS);
        Assert.Equal(30m, dto.PendingValueUSD);
    }

    [Fact]
    public void PendingPickupClientDto_ComputesAgeAndStaleFlag()
    {
        var today = DateTime.Today;

        var todayPickup = new PendingPickupClientDto
        {
            Date = today.AddHours(8),
            TotalUnits = 10m,
            DeliveredUnits = 0m
        };

        Assert.Equal(0, todayPickup.DaysInCustody);
        Assert.Equal("Hoy", todayPickup.AgeLabel);
        Assert.False(todayPickup.IsStale);

        var oneDayPickup = new PendingPickupClientDto
        {
            Date = today.AddDays(-1).AddHours(8),
            TotalUnits = 10m,
            DeliveredUnits = 0m
        };

        Assert.Equal(1, oneDayPickup.DaysInCustody);
        Assert.Equal("Hace 1 día", oneDayPickup.AgeLabel);
        Assert.False(oneDayPickup.IsStale);

        var stalePickup = new PendingPickupClientDto
        {
            Date = today.AddDays(-9).AddHours(8),
            TotalUnits = 10m,
            DeliveredUnits = 2m
        };

        Assert.Equal(9, stalePickup.DaysInCustody);
        Assert.Equal("Hace 9 días", stalePickup.AgeLabel);
        Assert.True(stalePickup.IsStale);

        var oldButDelivered = new PendingPickupClientDto
        {
            Date = today.AddDays(-9).AddHours(8),
            TotalUnits = 10m,
            DeliveredUnits = 10m
        };

        Assert.Equal("Hace 9 días", oldButDelivered.AgeLabel);
        Assert.False(oldButDelivered.IsStale);
    }

    [Fact]
    public void PendingPickupClientDto_WithUtcDate_ConvertsToVenezuelaLocalTime()
    {
        var dto = new PendingPickupClientDto
        {
            Date = new DateTime(2026, 11, 27, 5, 48, 0, DateTimeKind.Utc)
        };

        Assert.Equal(new DateTime(2026, 11, 27, 1, 48, 0), dto.DateLocal);
        Assert.Equal("27/11/26", dto.DateDisplay);
        Assert.Equal("27/11/26 01:48", dto.PaymentDateDisplay);
    }

    private static PendingPickupClientDto CreatePricedPickup(
        int saleId,
        int? invoiceNumber,
        DateTime date,
        string customerName,
        string customerCedula,
        decimal totalUnits,
        decimal deliveredUnits,
        decimal unitPriceBsS,
        decimal totalBsS,
        decimal appliedRate,
        string deliveryStatus = "PendingPickup",
        string customerPhone = "")
    {
        decimal pendingUnits = Math.Max(0m, totalUnits - deliveredUnits);
        return new PendingPickupClientDto
        {
            SaleId = saleId,
            InvoiceNumber = invoiceNumber,
            Date = date,
            CustomerName = customerName,
            CustomerCedula = customerCedula,
            CustomerPhone = customerPhone,
            TotalUSD = appliedRate > 0m ? totalBsS / appliedRate : totalBsS,
            TotalBsS = totalBsS,
            AppliedRate = appliedRate,
            TotalUnits = totalUnits,
            DeliveredUnits = deliveredUnits,
            DeliveryStatus = deliveryStatus,
            Items = new List<PendingPickupItemDto>
            {
                new()
                {
                    SaleItemId = 1000 + saleId,
                    ProductId = 9,
                    ProductName = $"Producto {saleId}",
                    Quantity = totalUnits,
                    DeliveredQuantity = deliveredUnits,
                    PendingQuantity = pendingUnits,
                    UnitPriceBsS = unitPriceBsS,
                    SubtotalBsS = unitPriceBsS * totalUnits
                }
            }
        };
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
