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
        Assert.Equal("0 / 1000", vm.SelectionCounterText);
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
        Assert.Equal("1 / 1000", vm.SelectionCounterText);
        Assert.True(vm.CanConfirm);

        first.IsSelected = false;

        Assert.Equal(0, vm.SelectedCount);
        Assert.Empty(vm.SelectedProducts);
        Assert.False(vm.CanConfirm);
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

    private void SetupProducts(params ProductDto[] items)
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
            .ReturnsAsync(new PagedResultDto<ProductDto>(items, items.Length));
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
