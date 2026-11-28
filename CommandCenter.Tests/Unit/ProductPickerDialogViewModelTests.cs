using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// WU3: selector WPF de productos. Verifica que solo muestra productos contables (SKU/nombre,
/// sin stock ni costo), que la selección/chips respetan el tope y que confirmar/cancelar cierran
/// el diálogo.
/// </summary>
public sealed class ProductPickerDialogViewModelTests
{
    private readonly Mock<IProductService> _products = new();
    private readonly Mock<ICategoryService> _categories = new();

    public ProductPickerDialogViewModelTests()
    {
        _categories
            .Setup(service => service.GetCategoriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CategoryDto>
            {
                new(1, "Alimentos", null, 0, "Alimentos"),
                new(2, "Bebidas", null, 0, "Bebidas")
            });
    }

    [Fact]
    public async Task InitializeAsync_LoadsCategoriesAndOnlyCountableProducts()
    {
        SetupProducts(
            CreateProduct(10, "SKU-1", "Harina"),
            CreateProduct(11, "SKU-2", "Arroz"),
            CreateProduct(12, "SKU-CASH", "Adelanto", isCashAdvance: true),
            CreateProduct(13, "SKU-INACT", "Inactivo", isActive: false),
            CreateProduct(14, "SKU-VAR", "Variante compartida", parentProductId: 11, parentIsStockShared: true));

        var vm = CreateViewModel();
        await vm.InitializeAsync();

        Assert.Equal(2, vm.Results.Count);
        Assert.DoesNotContain(vm.Results, row => row.ProductId is 12 or 13 or 14);
        Assert.Equal(3, vm.Categories.Count); // Todas + Alimentos + Bebidas
        Assert.Equal("Todas las categorías", vm.Categories[0].Label);
        Assert.Equal(0, vm.SelectedCount);
        Assert.Equal("Seleccionados: 0 / 1000", vm.SelectionCounterText);
        Assert.Equal("Página 1 de 1", vm.PageText);
        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public async Task SelectingAndRemovingProducts_UpdatesChipsCounterAndConfirm()
    {
        SetupProducts(
            CreateProduct(10, "SKU-1", "Harina"),
            CreateProduct(11, "SKU-2", "Arroz"));
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        var first = vm.Results.Single(row => row.ProductId == 10);
        first.IsSelected = true;

        Assert.Equal(1, vm.SelectedCount);
        Assert.Single(vm.SelectedProducts);
        Assert.Equal("Harina", vm.SelectedProducts[0].Name);
        Assert.Equal("Seleccionados: 1 / 1000", vm.SelectionCounterText);
        Assert.Equal("Confirmar selección (1)", vm.ConfirmButtonText);
        Assert.Equal("#F5F3FF", vm.SelectionCounterBackground);
        Assert.Equal("#DDD6FE", vm.SelectionCounterBorder);
        Assert.True(vm.CanConfirm);

        first.IsSelected = false;

        Assert.Equal(0, vm.SelectedCount);
        Assert.Empty(vm.SelectedProducts);
        Assert.Equal("Confirmar selección", vm.ConfirmButtonText);
        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public async Task PageSelection_SelectsAndClearsOnlyVisibleRows()
    {
        SetupProducts(
            CreateProduct(10, "SKU-1", "Harina"),
            CreateProduct(11, "SKU-2", "Arroz"),
            CreateProduct(12, "SKU-3", "Café"));
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        vm.IsPageSelected = true;

        Assert.True(vm.IsPageSelected);
        Assert.Equal(3, vm.SelectedCount);
        Assert.All(vm.Results, row => Assert.True(row.IsSelected));
        Assert.Equal("Confirmar selección (3)", vm.ConfirmButtonText);

        vm.IsPageSelected = false;

        Assert.False(vm.IsPageSelected);
        Assert.Equal(0, vm.SelectedCount);
        Assert.All(vm.Results, row => Assert.False(row.IsSelected));
    }

    [Fact]
    public async Task PageSelectionState_IsIndeterminate_WhenOnlyPartOfThePageIsSelected()
    {
        SetupProducts(
            CreateProduct(10, "SKU-1", "Harina"),
            CreateProduct(11, "SKU-2", "Arroz"),
            CreateProduct(12, "SKU-3", "Café"));
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        vm.Results[0].IsSelected = true;

        Assert.Null(vm.PageSelectionState);
        Assert.False(vm.IsPageSelected);

        vm.PageSelectionState = true;

        Assert.True(vm.PageSelectionState);
        Assert.Equal(3, vm.SelectedCount);

        vm.PageSelectionState = false;

        Assert.False(vm.PageSelectionState);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task AddCategoryProducts_AddsOnlyCountableProductsFromTheSelectedCategory()
    {
        SetupProducts(
            new[]
            {
                CreateProduct(10, "SKU-1", "Harina"),
                CreateProduct(11, "SKU-2", "Arroz"),
                CreateProduct(12, "SKU-CASH", "Adelanto", isCashAdvance: true)
            },
            totalCount: 3);
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.SelectedCategory = vm.Categories.Single(category => category.Id == 1);
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.True(vm.CanAddCategory);

        await vm.AddCategoryProductsCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.SelectedCount);
        Assert.Contains(vm.SelectedProducts, product => product.Id == 10);
        Assert.Contains(vm.SelectedProducts, product => product.Id == 11);
        Assert.DoesNotContain(vm.SelectedProducts, product => product.Id == 12);
        Assert.All(vm.Results.Where(row => row.ProductId is 10 or 11), row => Assert.True(row.IsSelected));
        Assert.True(vm.PageSelectionState);
    }

    [Fact]
    public async Task ClearSelection_RemovesAllChipsAndVisibleMarks()
    {
        SetupProducts(
            CreateProduct(10, "SKU-1", "Harina"),
            CreateProduct(11, "SKU-2", "Arroz"));
        var vm = CreateViewModel(new[]
        {
            new ProductPickerSelection(10, "SKU-1", "Harina"),
            new ProductPickerSelection(11, "SKU-2", "Arroz")
        });
        await vm.InitializeAsync();

        vm.ClearSelectionCommand.Execute(null);

        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.HasSelection);
        Assert.All(vm.Results, row => Assert.False(row.IsSelected));
        Assert.Equal("Confirmar selección", vm.ConfirmButtonText);
    }

    [Fact]
    public async Task PreselectedProducts_AreReflectedInRowsAndChips()
    {
        SetupProducts(CreateProduct(10, "SKU-1", "Harina"));
        var preselected = new[] { new ProductPickerSelection(10, "SKU-1", "Harina") };
        var vm = CreateViewModel(preselected);

        await vm.InitializeAsync();

        var row = Assert.Single(vm.Results);
        Assert.True(row.IsSelected);
        var selected = Assert.Single(vm.SelectedProducts);
        Assert.Equal(10, selected.Id);
        Assert.True(vm.CanConfirm);
    }

    [Fact]
    public async Task RemoveSelected_ClearsVisibleRowSelection()
    {
        SetupProducts(CreateProduct(10, "SKU-1", "Harina"));
        var vm = new ProductPickerDialogViewModel(
            _products.Object,
            _categories.Object,
            new[] { new ProductPickerSelection(10, "SKU-1", "Harina") });
        await vm.InitializeAsync();

        vm.RemoveSelectedCommand.Execute(vm.SelectedProducts.Single());

        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.Results.Single().IsSelected);
    }

    [Fact]
    public async Task MaxSelection_IsEnforced()
    {
        SetupProducts(CreateProduct(1001, "SKU-1001", "Producto extra"));
        var preselected = Enumerable.Range(1, ProductPickerDialogViewModel.MaxSelection)
            .Select(id => new ProductPickerSelection(id, $"SKU-{id}", $"Producto {id}"))
            .ToList();
        var vm = CreateViewModel(preselected);
        await vm.InitializeAsync();

        var row = Assert.Single(vm.Results);
        row.IsSelected = true;

        Assert.False(row.IsSelected);
        Assert.Equal(ProductPickerDialogViewModel.MaxSelection, vm.SelectedCount);
        Assert.Contains("Máximo", vm.ErrorMessage);
        Assert.Equal("#FEF3C7", vm.SelectionCounterBackground);
        Assert.Equal("#B45309", vm.SelectionCounterForeground);
        Assert.Equal("Confirmar selección (1000)", vm.ConfirmButtonText);
    }

    [Fact]
    public async Task SearchCommand_UsesCurrentFilterCategoryAndFirstPage()
    {
        SetupProducts(CreateProduct(10, "SKU-1", "Harina"));
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.SearchText = "harina";
        vm.SelectedCategory = vm.Categories.Single(category => category.Id == 2);

        await vm.SearchCommand.ExecuteAsync(null);

        _products.Verify(service => service.GetPagedAsync(
            "harina",
            1,
            It.IsAny<int>(),
            "active",
            "name",
            false,
            It.IsAny<CancellationToken>(),
            2), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Pagination_ExposesCatalogControlsAndNavigates()
    {
        SetupProducts(
            new[]
            {
                CreateProduct(10, "SKU-1", "Harina"),
                CreateProduct(11, "SKU-2", "Arroz")
            },
            totalCount: 120);
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        Assert.Equal(1, vm.Page);
        Assert.Equal(3, vm.TotalPages);
        Assert.Equal(new[] { 1, 2, 3 }, vm.PageNumbers.Select(item => item.PageNumber).ToArray());
        Assert.True(vm.PageNumbers[0].IsActive);
        Assert.Equal("Página 1 de 3 · 120 productos", vm.PageSummary);
        Assert.False(vm.CanGoFirst);
        Assert.False(vm.CanGoPrevious);
        Assert.True(vm.CanGoNext);
        Assert.True(vm.CanGoLast);

        await vm.LastPageCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Page);
        Assert.Equal("Página 3 de 3 · 120 productos", vm.PageSummary);
        Assert.Equal(3, vm.PageNumbers.Single(item => item.IsActive).PageNumber);
        Assert.True(vm.CanGoFirst);
        Assert.True(vm.CanGoPrevious);
        Assert.False(vm.CanGoNext);
        Assert.False(vm.CanGoLast);

        await vm.GoToPageCommand.ExecuteAsync(2);
        Assert.Equal(2, vm.Page);

        vm.TargetPageInput = "1";
        await vm.SubmitGoToPageCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.Page);

        vm.TargetPageInput = "99";
        await vm.SubmitGoToPageCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Page);
        Assert.Equal("3", vm.TargetPageInput);

        vm.TargetPageInput = "abc";
        await vm.SubmitGoToPageCommand.ExecuteAsync(null);
        Assert.Equal("3", vm.TargetPageInput);
    }

    [Fact]
    public async Task ConfirmRequiresSelection_AndCancelAlwaysCloses()
    {
        SetupProducts(CreateProduct(10, "SKU-1", "Harina"));
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        bool? result = null;
        vm.RequestClose += value => result = value;

        vm.ConfirmCommand.Execute(null);
        Assert.Null(result);

        vm.CancelCommand.Execute(null);
        Assert.False(result);

        result = null;
        vm.Results.Single().IsSelected = true;
        vm.ConfirmCommand.Execute(null);
        Assert.True(result);
    }

    private ProductPickerDialogViewModel CreateViewModel(IEnumerable<ProductPickerSelection>? preselected = null) =>
        new(_products.Object, _categories.Object, preselected);

    private void SetupProducts(params ProductDto[] items) => SetupProducts(items, items.Length);

    private void SetupProducts(ProductDto[] items, int totalCount)
    {
        _products
            .Setup(service => service.GetPagedAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int?>()))
            .ReturnsAsync(new PagedResultDto<ProductDto>(items, totalCount));
    }

    private static ProductDto CreateProduct(
        int id,
        string sku,
        string name,
        bool isActive = true,
        bool isDeleted = false,
        bool isCashAdvance = false,
        bool isGroupHeader = false,
        bool isStockShared = false,
        int? parentProductId = null,
        bool parentIsStockShared = false) => new()
    {
        Id = id,
        SKU = sku,
        Name = name,
        IsActive = isActive,
        IsDeleted = isDeleted,
        IsCashAdvance = isCashAdvance,
        IsGroupHeader = isGroupHeader,
        IsStockShared = isStockShared,
        ParentProductId = parentProductId,
        ParentIsStockShared = parentIsStockShared
    };
}
