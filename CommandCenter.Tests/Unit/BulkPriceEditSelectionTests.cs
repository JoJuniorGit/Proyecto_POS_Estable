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

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.181-T2 (S5): modelo de selección múltiple del catálogo (checkboxes persistentes entre
/// páginas por ProductId, "seleccionar todos los filtrados" con el modo filtro del contrato) y
/// apertura del diálogo de edición masiva de precios.
/// </summary>
public class BulkPriceEditSelectionTests
{
    private readonly Mock<IProductService> _productServiceMock = new();
    private readonly Mock<IExchangeRateService> _exchangeRateServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();

    private string? _lastQueryText;
    private int? _lastCategoryId;
    private int _pagedCallCount;

    private InventoryViewModel CreateViewModel(UserSession? session = null)
    {
        _exchangeRateServiceMock.Setup(s => s.CurrentRate).Returns(36.5m);
        _exchangeRateServiceMock.Setup(s => s.GetCurrentRateAsync())
                                .ReturnsAsync((36.5m, (DateTime?)DateTime.UtcNow));

        var vm = new InventoryViewModel(
            _productServiceMock.Object,
            _exchangeRateServiceMock.Object,
            session,
            _dialogServiceMock.Object);

        // Aísla del bus global compartido entre pruebas (mensajes externos alteran el inventario).
        WeakReferenceMessenger.Default.UnregisterAll(vm);
        return vm;
    }

    private void SetupPagedCatalog(Func<int, IReadOnlyList<ProductDto>> itemsByPage, int totalCount)
    {
        _productServiceMock
            .Setup(s => s.GetPagedAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int?>()))
            .ReturnsAsync((string? filter, int page, int _, string? __, string? ___, bool ____, CancellationToken _____, int? categoryId) =>
            {
                _lastQueryText = filter;
                _lastCategoryId = categoryId;
                _pagedCallCount++;
                var items = itemsByPage(page).ToList();
                return new PagedResultDto<ProductDto>
                {
                    Items = items,
                    TotalCount = totalCount,
                    HasMore = false
                };
            });
    }

    private static ProductDto Product(int id) => new()
    {
        Id = id,
        Name = $"Producto {id}",
        SKU = $"SKU-{id:D4}",
        PriceUSD = 1m,
        PriceBsS = 36.5m,
        IsActive = true
    };

    private static List<ProductDto> PageOfRange(int firstId, int count)
        => Enumerable.Range(firstId, count).Select(Product).ToList();

    [Fact]
    public async Task TogglingRow_UpdatesSelectionCountAndSummary()
    {
        SetupPagedCatalog(_ => PageOfRange(1, 2), totalCount: 2);
        var vm = CreateViewModel();
        await WaitUntilAsync(() => vm.Products.Count == 2, TimeSpan.FromSeconds(5));

        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.HasSelection);
        Assert.False(vm.CanEditSelectedPrices);

        vm.Products[0].IsSelected = true;

        Assert.Equal(1, vm.SelectedCount);
        Assert.True(vm.HasSelection);
        Assert.True(vm.CanEditSelectedPrices);
        Assert.Contains("1", vm.SelectionSummary);

        vm.Products[0].IsSelected = false;

        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.HasSelection);
    }

    [Fact]
    public async Task TogglingCheckbox_DoesNotTriggerRowPersistence()
    {
        SetupPagedCatalog(_ => PageOfRange(1, 2), totalCount: 2);
        var vm = CreateViewModel();
        await WaitUntilAsync(() => vm.Products.Count == 2, TimeSpan.FromSeconds(5));

        vm.Products[0].IsSelected = true;
        await Task.Delay(100);

        // La columna de checkboxes no debe disparar el auto-save por fila ni recálculos de precio.
        _productServiceMock.Verify(s => s.UpdateAsync(It.IsAny<UpdateProductDto>()), Times.Never);
        _productServiceMock.Verify(s => s.GetByIdAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Selection_PersistsAcrossPages_ByProductId()
    {
        SetupPagedCatalog(page => PageOfRange((page - 1) * 25 + 1, 25), totalCount: 50);
        var vm = CreateViewModel();
        await WaitUntilAsync(() => vm.Products.Count == 25 && vm.Products[0].Id == 1, TimeSpan.FromSeconds(5));

        vm.Products[0].IsSelected = true; // id 1

        await vm.NextPageCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.Products.Count == 25 && vm.Products[0].Id == 26, TimeSpan.FromSeconds(5));

        Assert.All(vm.Products, p => Assert.False(p.IsSelected));
        vm.Products[10].IsSelected = true; // id 36

        await vm.PreviousPageCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.Products.Count == 25 && vm.Products[0].Id == 1, TimeSpan.FromSeconds(5));

        Assert.True(vm.Products[0].IsSelected);
        Assert.False(vm.Products[1].IsSelected);
        Assert.Equal(2, vm.SelectedCount);
    }

    [Fact]
    public async Task SelectAllFiltered_SelectsAllMatchingProductsAndMarksFilterMode()
    {
        SetupPagedCatalog(_ => PageOfRange(1, 25), totalCount: 120);
        var vm = CreateViewModel();
        await WaitUntilAsync(() => vm.Products.Count == 25, TimeSpan.FromSeconds(5));

        vm.SelectAllFilteredCommand.Execute(null);

        Assert.True(vm.IsAllFilteredSelected);
        Assert.Equal(120, vm.SelectedCount);
        Assert.True(vm.HasSelection);
        Assert.True(vm.CanEditSelectedPrices);
        Assert.Contains("120", vm.SelectionSummary);
        Assert.All(vm.Products, p => Assert.True(p.IsSelected));
    }

    [Fact]
    public async Task UncheckingRowWhileFilterModeActive_MaterializesExplicitSelection()
    {
        SetupPagedCatalog(_ => PageOfRange(1, 2), totalCount: 2);
        var vm = CreateViewModel();
        await WaitUntilAsync(() => vm.Products.Count == 2, TimeSpan.FromSeconds(5));

        vm.SelectAllFilteredCommand.Execute(null);
        Assert.True(vm.IsAllFilteredSelected);

        vm.Products[0].IsSelected = false;

        Assert.False(vm.IsAllFilteredSelected);
        Assert.Equal(1, vm.SelectedCount);
        Assert.False(vm.Products[0].IsSelected);
        Assert.True(vm.Products[1].IsSelected);
    }

    [Fact]
    public async Task ChangingStatusFilter_ClearsFilterModeSelection()
    {
        SetupPagedCatalog(_ => PageOfRange(1, 2), totalCount: 2);
        var vm = CreateViewModel();
        await WaitUntilAsync(() => vm.Products.Count == 2, TimeSpan.FromSeconds(5));

        vm.SelectAllFilteredCommand.Execute(null);
        Assert.True(vm.IsAllFilteredSelected);

        vm.SelectedStatusFilter = "all";

        Assert.False(vm.IsAllFilteredSelected);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task ChangingSearchText_ClearsFilterModeSelection()
    {
        SetupPagedCatalog(_ => PageOfRange(1, 2), totalCount: 2);
        var vm = CreateViewModel();
        await WaitUntilAsync(() => vm.Products.Count == 2, TimeSpan.FromSeconds(5));

        vm.SelectAllFilteredCommand.Execute(null);

        vm.SearchText = "arroz";

        Assert.False(vm.IsAllFilteredSelected);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task ChangingFilters_KeepsExplicitSelection()
    {
        SetupPagedCatalog(_ => PageOfRange(1, 2), totalCount: 2);
        var vm = CreateViewModel();
        await WaitUntilAsync(() => vm.Products.Count == 2, TimeSpan.FromSeconds(5));

        vm.Products[0].IsSelected = true;

        vm.SelectedStatusFilter = "all";
        await Task.Delay(50);

        Assert.Equal(1, vm.SelectedCount);
    }

    [Fact]
    public async Task ClearSelection_ResetsExplicitSelectionAndVisibleCheckboxes()
    {
        SetupPagedCatalog(_ => PageOfRange(1, 2), totalCount: 2);
        var vm = CreateViewModel();
        await WaitUntilAsync(() => vm.Products.Count == 2, TimeSpan.FromSeconds(5));

        vm.Products[0].IsSelected = true;
        vm.Products[1].IsSelected = true;

        vm.ClearSelectionCommand.Execute(null);

        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.HasSelection);
        Assert.All(vm.Products, p => Assert.False(p.IsSelected));
    }

    [Fact]
    public async Task OpenBulkPriceEdit_WithCashierSession_DoesNotOpenDialog()
    {
        SetupPagedCatalog(_ => PageOfRange(1, 2), totalCount: 2);
        var session = new UserSession();
        session.SetUser(new UserDto
        {
            Id = 9,
            Name = "Cajero",
            Role = UserRole.Cashier,
            Cedula = "V-1",
            IsActive = true
        });
        var vm = CreateViewModel(session);
        await WaitUntilAsync(() => vm.Products.Count == 2, TimeSpan.FromSeconds(5));

        vm.Products[0].IsSelected = true;

        await vm.OpenBulkPriceEditCommand.ExecuteAsync(null);

        _dialogServiceMock.Verify(
            d => d.ShowBulkPriceEditDialog(It.IsAny<BulkPriceEditDialogViewModel>()),
            Times.Never);
    }

    [Fact]
    public async Task OpenBulkPriceEdit_WithoutSelection_DoesNotOpenDialog()
    {
        SetupPagedCatalog(_ => PageOfRange(1, 2), totalCount: 2);
        var vm = CreateViewModel();
        await WaitUntilAsync(() => vm.Products.Count == 2, TimeSpan.FromSeconds(5));

        await vm.OpenBulkPriceEditCommand.ExecuteAsync(null);

        _dialogServiceMock.Verify(
            d => d.ShowBulkPriceEditDialog(It.IsAny<BulkPriceEditDialogViewModel>()),
            Times.Never);
    }

    [Fact]
    public async Task OpenBulkPriceEdit_WithSelection_PassesSelectionAndRefreshesCatalogWhenApplied()
    {
        SetupPagedCatalog(_ => PageOfRange(1, 2), totalCount: 2);
        var vm = CreateViewModel();
        await WaitUntilAsync(() => vm.Products.Count == 2, TimeSpan.FromSeconds(5));

        vm.Products[0].IsSelected = true;
        vm.Products[1].IsSelected = true;

        BulkPriceEditDialogViewModel? opened = null;
        _dialogServiceMock
            .Setup(d => d.ShowBulkPriceEditDialog(It.IsAny<BulkPriceEditDialogViewModel>()))
            .Callback<BulkPriceEditDialogViewModel>(dialogVm => opened = dialogVm)
            .Returns(true);

        await vm.OpenBulkPriceEditCommand.ExecuteAsync(null);

        Assert.NotNull(opened);
        Assert.False(opened!.IsSelectAllFiltered);
        Assert.Equal(2, opened.SelectedCount);

        BulkPriceUpdateRequestDto? captured = null;
        _productServiceMock
            .Setup(s => s.PreviewBulkPricesAsync(It.IsAny<BulkPriceUpdateRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback((BulkPriceUpdateRequestDto request, CancellationToken _) => captured = request)
            .ReturnsAsync(new BulkPriceUpdateResultDto());
        var operation = opened.Operations.Single(r => r.Field == BulkPriceField.RetailPrice);
        operation.IsEnabled = true;
        operation.ValueText = "2";
        await opened.PreviewCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal(new[] { 1, 2 }, captured!.ProductIds);

        _dialogServiceMock.Verify(
            d => d.ShowSuccessDialog(It.IsAny<string>(), It.IsAny<string?>()),
            Times.Once);
        Assert.True(_pagedCallCount >= 2);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task OpenBulkPriceEdit_AllFiltered_PassesVigentesFiltersToPreviewRequest()
    {
        SetupPagedCatalog(_ => PageOfRange(1, 25), totalCount: 30);
        var vm = CreateViewModel();
        await WaitUntilAsync(() => vm.Products.Count == 25, TimeSpan.FromSeconds(5));

        vm.SearchText = "arroz";
        await WaitUntilAsync(() => _lastQueryText == "arroz", TimeSpan.FromSeconds(5));

        vm.SelectedCategoryFilter = new CategoryOption(7, "Bebidas", null, 0, "Bebidas");
        await WaitUntilAsync(() => _lastCategoryId == 7, TimeSpan.FromSeconds(5));

        vm.SelectAllFilteredCommand.Execute(null);
        Assert.True(vm.IsAllFilteredSelected);
        Assert.Equal(30, vm.SelectedCount);

        BulkPriceEditDialogViewModel? opened = null;
        _dialogServiceMock
            .Setup(d => d.ShowBulkPriceEditDialog(It.IsAny<BulkPriceEditDialogViewModel>()))
            .Callback<BulkPriceEditDialogViewModel>(dialogVm => opened = dialogVm)
            .Returns(false);

        await vm.OpenBulkPriceEditCommand.ExecuteAsync(null);

        Assert.NotNull(opened);
        Assert.True(opened!.IsSelectAllFiltered);
        Assert.Equal(30, opened.SelectedCount);

        BulkPriceUpdateRequestDto? captured = null;
        _productServiceMock
            .Setup(s => s.PreviewBulkPricesAsync(It.IsAny<BulkPriceUpdateRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback((BulkPriceUpdateRequestDto request, CancellationToken _) => captured = request)
            .ReturnsAsync(new BulkPriceUpdateResultDto());

        var operation = opened.Operations.Single(r => r.Field == BulkPriceField.RetailPrice);
        operation.IsEnabled = true;
        operation.SelectedMode = BulkPriceMode.IncreasePercent;
        operation.ValueText = "10";
        await opened.PreviewCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.True(captured!.SelectAllFiltered);
        Assert.NotNull(captured.Filter);
        Assert.Equal("arroz", captured.Filter!.SearchText);
        Assert.Equal("active", captured.Filter.StatusFilter);
        Assert.Equal(7, captured.Filter.CategoryId);
    }

    [Fact]
    public async Task OpenBulkPriceEdit_ExplicitSelectionOverLimit_BlocksWithWarning()
    {
        const int pageSize = 25;
        const int totalProducts = 1025;
        SetupPagedCatalog(page => PageOfRange((page - 1) * pageSize + 1, pageSize), totalCount: totalProducts);
        var vm = CreateViewModel();
        await WaitUntilAsync(() => vm.Products.Count == pageSize && vm.Products[0].Id == 1, TimeSpan.FromSeconds(5));

        for (int page = 1; page <= 41; page++)
        {
            if (page > 1)
            {
                await vm.GoToPageCommand.ExecuteAsync(page);
                int firstId = (page - 1) * pageSize + 1;
                await WaitUntilAsync(
                    () => vm.CurrentPage == page && vm.Products.Count == pageSize && vm.Products[0].Id == firstId,
                    TimeSpan.FromSeconds(5));
            }

            foreach (var item in vm.Products)
            {
                item.IsSelected = true;
            }
        }

        Assert.Equal(totalProducts, vm.SelectedCount);

        await vm.OpenBulkPriceEditCommand.ExecuteAsync(null);

        _dialogServiceMock.Verify(
            d => d.ShowWarning(It.IsAny<string>(), It.Is<string>(m => m.Contains("1000"))),
            Times.Once);
        _dialogServiceMock.Verify(
            d => d.ShowBulkPriceEditDialog(It.IsAny<BulkPriceEditDialogViewModel>()),
            Times.Never);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "La condición esperada no se cumplió dentro del plazo.");
    }
}
