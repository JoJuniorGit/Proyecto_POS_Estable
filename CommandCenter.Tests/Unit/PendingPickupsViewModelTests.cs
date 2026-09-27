using System;
using System.Collections.Generic;
using System.Linq;
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

    private PendingPickupsViewModel CreateViewModel()
    {
        return new PendingPickupsViewModel(_salesServiceMock.Object, _dialogServiceMock.Object);
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
    public async Task ConfirmPickupAsync_WhenUserConfirms_RemovesItemDirectlyFromPickupsCollection()
    {
        var pickup = new PendingPickupClientDto
        {
            SaleId = 42,
            InvoiceNumber = 205,
            CustomerName = "Carlos Perez",
            TotalUSD = 50m,
            Date = DateTime.UtcNow
        };

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { pickup }, 1));

        _dialogServiceMock
            .Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        _salesServiceMock
            .Setup(s => s.ConfirmPickupAsync(42))
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        Assert.Single(vm.Pickups);

        await vm.ConfirmPickupCommand.ExecuteAsync(pickup);

        _salesServiceMock.Verify(s => s.ConfirmPickupAsync(42), Times.Once);
        Assert.Empty(vm.Pickups);
        Assert.NotNull(vm.SuccessMessage);
        Assert.Contains("Factura N° 00205", vm.SuccessMessage);
    }

    [Fact]
    public async Task ConfirmPickupAsync_WhenUserCancels_DoesNotConfirmOrRemove()
    {
        var pickup = new PendingPickupClientDto
        {
            SaleId = 42,
            InvoiceNumber = 205,
            CustomerName = "Carlos Perez",
            TotalUSD = 50m,
            Date = DateTime.UtcNow
        };

        _salesServiceMock
            .Setup(s => s.GetPendingPickupsPagedAsync(It.IsAny<int>(), 0))
            .ReturnsAsync((new List<PendingPickupClientDto> { pickup }, 1));

        _dialogServiceMock
            .Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        var vm = CreateViewModel();
        await vm.EnsureLoadedAsync();

        await vm.ConfirmPickupCommand.ExecuteAsync(pickup);

        _salesServiceMock.Verify(s => s.ConfirmPickupAsync(It.IsAny<int>()), Times.Never);
        Assert.Single(vm.Pickups);
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
}
