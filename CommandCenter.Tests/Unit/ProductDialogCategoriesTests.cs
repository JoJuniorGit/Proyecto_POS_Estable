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
/// 8.179-T2 (S3): tarjeta "Categorías" del diálogo de producto — árbol aplanado, preselección al
/// editar, envío de la lista completa (reemplazo) y refresco tras la gestión preservando selección.
/// </summary>
public class ProductDialogCategoriesTests
{
    [Fact]
    public async Task EditProduct_PreselectsAssignedCategories_AndFlattensTreeWithPathAndDepth()
    {
        var fixture = CreateFixture();
        var product = CreateProductWithCategories(2);

        var vm = CreateViewModel(fixture, product);
        await vm.CategoriesLoadTask;

        Assert.Equal(new[] { 1, 2, 3 }, vm.CategoryOptions.Select(o => o.Id));
        var milky = vm.CategoryOptions.Single(o => o.Id == 2);
        Assert.Equal(1, milky.Depth);
        Assert.Equal("Alimentos > Lácteos", milky.Path);
        Assert.True(milky.IsSelected);
        Assert.False(vm.CategoryOptions.Single(o => o.Id == 1).IsSelected);
        Assert.Equal(1, vm.SelectedCategoriesCount);
        Assert.Equal(new[] { 2 }, vm.ResultProduct.CategoryIds);
    }

    [Fact]
    public async Task EditProduct_UnselectAll_SavesEmptyCategoryListForReplacement()
    {
        var fixture = CreateFixture();
        var vm = CreateViewModel(fixture, CreateProductWithCategories(2));
        await vm.CategoriesLoadTask;

        bool closed = false;
        vm.RequestClose = _ => closed = true;

        vm.CategoryOptions.Single(o => o.Id == 2).IsSelected = false;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.NotNull(vm.ResultProduct.CategoryIds);
        Assert.Empty(vm.ResultProduct.CategoryIds!);

        var update = ProductClientMapping.ToUpdateProductDto(vm.ResultProduct, vm.ResultProductId);
        Assert.NotNull(update.CategoryIds);
        Assert.Empty(update.CategoryIds!);
    }

    [Fact]
    public async Task EditProduct_SelectAdditionalCategory_SavesCompleteReplacementList()
    {
        var fixture = CreateFixture();
        var vm = CreateViewModel(fixture, CreateProductWithCategories(2));
        await vm.CategoriesLoadTask;

        vm.CategoryOptions.Single(o => o.Id == 3).IsSelected = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new[] { 2, 3 }, vm.ResultProduct.CategoryIds);
        var update = ProductClientMapping.ToUpdateProductDto(vm.ResultProduct, vm.ResultProductId);
        Assert.Equal(new[] { 2, 3 }, update.CategoryIds);
    }

    [Fact]
    public async Task ManageCategories_WhenDialogReportsChanges_RefreshesTreePreservingSelectionById()
    {
        var fixture = CreateFixture();
        var vm = CreateViewModel(fixture, CreateProductWithCategories(2));
        await vm.CategoriesLoadTask;

        // La gestión renombra la categoría seleccionada y agrega una raíz nueva.
        fixture.Categories
            .Setup(s => s.GetCategoriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CategoryDto>
            {
                new(1, "Alimentos", null, 0, "Alimentos"),
                new(2, "Lácteos y Cremería", 1, 1, "Alimentos > Lácteos y Cremería"),
                new(3, "Bebidas", null, 0, "Bebidas"),
                new(4, "Limpieza", null, 0, "Limpieza"),
            });
        fixture.Dialog
            .Setup(d => d.ShowCategoryManagementDialog(It.IsAny<CategoryManagementDialogViewModel>()))
            .Returns(true);

        await vm.ManageCategoriesCommand.ExecuteAsync(null);

        var renamed = vm.CategoryOptions.Single(o => o.Id == 2);
        Assert.Equal("Lácteos y Cremería", renamed.Name);
        Assert.True(renamed.IsSelected);
        Assert.Equal(1, vm.SelectedCategoriesCount);
        Assert.Equal(new[] { 2 }, vm.ResultProduct.CategoryIds);
        Assert.Contains(vm.CategoryOptions, o => o.Id == 4);
        fixture.Categories.Verify(s => s.GetCategoriesAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    [Fact]
    public async Task LoadCategories_WhenServerFails_KeepsUpdatePayloadUntouched()
    {
        // Sin árbol no se pisa la asignación del servidor: CategoryIds null = "no tocar".
        var fixture = CreateFixture();
        fixture.Categories
            .Setup(s => s.GetCategoriesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection refused"));
        var vm = CreateViewModel(fixture, CreateProductWithCategories(2));
        await vm.CategoriesLoadTask;

        Assert.True(vm.HasCategoriesError);
        Assert.Null(vm.ResultProduct.CategoryIds);
    }

    private static ProductDto CreateProductWithCategories(params int[] categoryIds) => new()
    {
        Id = 7,
        Name = "Queso Blanco",
        SKU = "QUE-001",
        Categories = categoryIds.Select(id => new CategoryRefDto(id, $"Cat-{id}")).ToList(),
    };

    private static List<CategoryDto> SampleTree() => new()
    {
        new CategoryDto(1, "Alimentos", null, 0, "Alimentos"),
        new CategoryDto(2, "Lácteos", 1, 1, "Alimentos > Lácteos"),
        new CategoryDto(3, "Bebidas", null, 0, "Bebidas"),
    };

    private sealed record Fixture(
        Mock<IProductService> ProductService,
        Mock<IExchangeRateService> ExchangeRate,
        Mock<ICategoryService> Categories,
        Mock<IDialogService> Dialog);

    private static Fixture CreateFixture()
    {
        var productService = new Mock<IProductService>();
        productService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto>());
        productService.Setup(s => s.GetQuickInfoAsync(It.IsAny<string>())).ReturnsAsync((ProductQuickInfoDto?)null);

        var exchangeRate = new Mock<IExchangeRateService>();
        exchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        var categories = new Mock<ICategoryService>();
        categories.Setup(s => s.GetCategoriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(SampleTree());

        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowCategoryManagementDialog(It.IsAny<CategoryManagementDialogViewModel>())).Returns(false);

        return new Fixture(productService, exchangeRate, categories, dialog);
    }

    private static ProductDialogViewModel CreateViewModel(Fixture fixture, ProductDto? product)
    {
        var vm = new ProductDialogViewModel(
            fixture.ProductService.Object,
            fixture.ExchangeRate.Object,
            product,
            userSession: null,
            dialogService: fixture.Dialog.Object)
        {
            // WpfDialogService adjunta el cliente antes de mostrar el diálogo; el setter inicia la carga.
            CategoryClient = fixture.Categories.Object,
        };
        return vm;
    }
}

/// <summary>
/// 8.179-T2 (S2): gestión completa de categorías — persistencia inmediata, recarga del árbol y
/// errores del servidor (409 legibles) sin romper el diálogo.
/// </summary>
public class CategoryManagementDialogViewModelTests
{
    [Fact]
    public async Task Create_PersistsAndReloads_AndReportsChanges()
    {
        var service = new Mock<ICategoryService>();
        service.Setup(s => s.GetCategoriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(SampleTree());
        service.Setup(s => s.CreateAsync(It.IsAny<CreateCategoryRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CategoryDto(9, "Limpieza", null, 0, "Limpieza"));
        var vm = new CategoryManagementDialogViewModel(service.Object, new Mock<IDialogService>().Object);
        await vm.LoadTask;

        vm.BeginCreateCommand.Execute(null);
        vm.CategoryName = "Limpieza";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.HasChanges);
        Assert.Equal(string.Empty, vm.ErrorMessage);
        service.Verify(
            s => s.CreateAsync(
                It.Is<CreateCategoryRequestDto>(r => r.Name == "Limpieza" && r.ParentCategoryId == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Create_WhenServerRejectsDuplicate_ShowsReadableErrorWithoutThrowing()
    {
        var service = new Mock<ICategoryService>();
        service.Setup(s => s.GetCategoriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(SampleTree());
        service.Setup(s => s.CreateAsync(It.IsAny<CreateCategoryRequestDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Ya existe una categoría con ese nombre en el mismo nivel."));
        var vm = new CategoryManagementDialogViewModel(service.Object, new Mock<IDialogService>().Object);
        await vm.LoadTask;

        vm.BeginCreateCommand.Execute(null);
        vm.CategoryName = "Bebidas";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("Ya existe una categoría", vm.ErrorMessage);
        Assert.False(vm.HasChanges);
        Assert.NotEmpty(vm.Categories);
    }

    [Fact]
    public async Task DeleteSelected_WhenServerRejects_ShowsReadableErrorAndKeepsDialogAlive()
    {
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        var service = new Mock<ICategoryService>();
        service.Setup(s => s.GetCategoriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(SampleTree());
        service.Setup(s => s.DeleteAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("No se puede eliminar la categoría porque tiene productos asignados."));
        var vm = new CategoryManagementDialogViewModel(service.Object, dialog.Object);
        await vm.LoadTask;

        vm.SelectedCategory = vm.Categories.Single(c => c.Id == 1);
        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Contains("productos asignados", vm.ErrorMessage);
        Assert.False(vm.HasChanges);
        Assert.NotEmpty(vm.Categories);
    }

    [Fact]
    public async Task DeleteSelected_WhenConfirmedAndAccepted_ReloadsAndReportsChanges()
    {
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        var service = new Mock<ICategoryService>();
        service.Setup(s => s.GetCategoriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(SampleTree());
        service.Setup(s => s.DeleteAsync(3, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var vm = new CategoryManagementDialogViewModel(service.Object, dialog.Object);
        await vm.LoadTask;

        vm.SelectedCategory = vm.Categories.Single(c => c.Id == 3);
        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.True(vm.HasChanges);
        Assert.Equal(string.Empty, vm.ErrorMessage);
        service.Verify(s => s.DeleteAsync(3, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Manage_RenameChildAfterSelectingParent_SendsRealParentId()
    {
        // Fix verificación B1: el selector de padre debe re-anclarse a la categoría seleccionada;
        // antes, renombrar una hija con el combo stale la movía en silencio (p. ej. a raíz).
        var service = new Mock<ICategoryService>();
        service.Setup(s => s.GetCategoriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CategoryDto>
            {
                new(1, "Alimentos", null, 0, "Alimentos"),
                new(2, "Lácteos", 1, 1, "Alimentos > Lácteos"),
            });
        service.Setup(s => s.UpdateAsync(2, It.IsAny<UpdateCategoryRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CategoryDto(2, "Lácteos y quesos", 1, 1, "Alimentos > Lácteos y quesos"));
        var vm = new CategoryManagementDialogViewModel(service.Object, new Mock<IDialogService>().Object);
        await vm.LoadTask;

        vm.SelectedCategory = vm.Categories.Single(c => c.Id == 1); // padre
        vm.SelectedCategory = vm.Categories.Single(c => c.Id == 2); // hija: debe re-anclar
        Assert.Equal(1, vm.SelectedParent?.Id);

        vm.CategoryName = "Lácteos y quesos";
        await vm.SaveCommand.ExecuteAsync(null);

        service.Verify(
            s => s.UpdateAsync(
                2,
                It.Is<UpdateCategoryRequestDto>(r => r.Name == "Lácteos y quesos" && r.ParentCategoryId == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Manage_SwitchingBetweenChildrenOfDifferentParents_ReanchorsParentOnEachSelection()
    {
        var service = new Mock<ICategoryService>();
        service.Setup(s => s.GetCategoriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CategoryDto>
            {
                new(1, "Alimentos", null, 0, "Alimentos"),
                new(2, "Lácteos", 1, 1, "Alimentos > Lácteos"),
                new(3, "Bebidas", null, 0, "Bebidas"),
                new(4, "Jugos", 3, 1, "Bebidas > Jugos"),
            });
        var vm = new CategoryManagementDialogViewModel(service.Object, new Mock<IDialogService>().Object);
        await vm.LoadTask;

        vm.SelectedCategory = vm.Categories.Single(c => c.Id == 2);
        Assert.Equal(1, vm.SelectedParent?.Id);

        vm.SelectedCategory = vm.Categories.Single(c => c.Id == 4);
        Assert.Equal(3, vm.SelectedParent?.Id);

        vm.SelectedCategory = vm.Categories.Single(c => c.Id == 1); // raíz → "Sin categoría padre"
        Assert.Equal(0, vm.SelectedParent?.Id);
    }

    private static List<CategoryDto> SampleTree() => new()
    {
        new CategoryDto(1, "Alimentos", null, 0, "Alimentos"),
        new CategoryDto(2, "Lácteos", 1, 1, "Alimentos > Lácteos"),
        new CategoryDto(3, "Bebidas", null, 0, "Bebidas"),
    };
}

/// <summary>
/// 8.179-T2 (S5-UI): la columna "Categoría" del importador mapea a la ruta jerárquica
/// (<see cref="Core.DTOs.ProductImportDto.CategoryPath"/>).
/// </summary>
public class ImportCategoryHeaderMappingTests
{
    [Theory]
    [InlineData("Categoría")]
    [InlineData("Categoria")]
    [InlineData("Categorías")]
    [InlineData("Categorias")]
    [InlineData("category")]
    [InlineData("CategoryPath")]
    public void MapHeaderToProperty_MapsCategoryHeadersToCategoryPath(string header)
    {
        Assert.Equal("CategoryPath", ImportProductsViewModel.MapHeaderToProperty(header));
    }

    [Theory]
    [InlineData("SKU", "SKU")]
    [InlineData("Nombre", "Name")]
    [InlineData("StockActual", "StockQuantity")]
    [InlineData("Grupo", "GroupNameOrKey")]
    public void MapHeaderToProperty_KeepsExistingMappingsIntact(string header, string expected)
    {
        Assert.Equal(expected, ImportProductsViewModel.MapHeaderToProperty(header));
    }

    [Fact]
    public void AvailableProperties_OffersCategoryPathForManualMapping()
    {
        var vm = new ImportProductsViewModel(
            Mock.Of<IProductImportService>(),
            Mock.Of<IExchangeRateService>(),
            new UserSession());

        Assert.Contains("CategoryPath", vm.AvailableProperties);
    }
}
