using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.179-T2 (S4 WPF): filtro "Categoría" del catálogo — viaja a GetPagedAsync solo cuando hay una
/// categoría seleccionada (null para "Todas") y el árbol se aplana preservando profundidad/ruta.
/// </summary>
public class InventoryCategoryFilterTests
{
    [Fact]
    public async Task CategoryFilter_WhenAllSelected_LoadsWithoutCategoryFilter()
    {
        var captured = new List<int?>();
        var fixture = new Fixture(captured);
        var vm = fixture.CreateViewModel();

        await vm.CategoryFilterLoadTask;
        await WaitUntilAsync(() => captured.Count >= 1);

        Assert.Null(captured[0]);
        Assert.Equal("Todas", vm.CategoryFilterOptions[0].Path);
        Assert.Equal(0, vm.CategoryFilterOptions[0].Id);
    }

    [Fact]
    public async Task CategoryFilterOptions_FlattenTreePreservingDepthAndPath()
    {
        var fixture = new Fixture(new List<int?>());
        var vm = fixture.CreateViewModel();

        await vm.CategoryFilterLoadTask;

        Assert.Equal(new[] { 1, 2, 3 }, vm.CategoryFilterOptions.Where(o => o.Id > 0).Select(o => o.Id));
        var milky = vm.CategoryFilterOptions.Single(o => o.Id == 2);
        Assert.Equal(1, milky.Depth);
        Assert.Equal("Alimentos > Lácteos", milky.Path);
        Assert.True(milky.IndentWidth > 0);
    }

    [Fact]
    public async Task CategoryFilter_WhenCategorySelected_TravelsToServiceAndClearsBackToNull()
    {
        var captured = new List<int?>();
        var fixture = new Fixture(captured);
        var vm = fixture.CreateViewModel();
        await vm.CategoryFilterLoadTask;
        await WaitUntilAsync(() => captured.Count >= 1);

        vm.SelectedCategoryFilter = vm.CategoryFilterOptions.Single(o => o.Id == 2);
        await WaitUntilAsync(() => captured.Count >= 2);
        Assert.Equal(2, captured[^1]);

        vm.SelectedCategoryFilter = vm.CategoryFilterOptions.Single(o => o.Id == 0);
        await WaitUntilAsync(() => captured.Count >= 3);
        Assert.Null(captured[^1]);
    }

    [Fact]
    public async Task EnsureLoadedAsync_OnFreshLogin_LoadsCategoryFilterOptions()
    {
        // Fix verificación B2: con login fresco el ctor corre sin sesión (no carga el filtro);
        // EnsureLoadedAsync al entrar a la vista debe cargar las opciones.
        var fixture = new Fixture(new List<int?>());
        var session = new UserSession();
        var vm = fixture.CreateViewModelWithSession(session);

        Assert.Empty(vm.CategoryFilterOptions);

        session.SetUser(new UserDto { Id = 1, Name = "Manager", Role = UserRole.Manager });
        await vm.EnsureLoadedAsync();

        Assert.Equal(new[] { 0, 1, 2, 3 }, vm.CategoryFilterOptions.Select(o => o.Id));
        Assert.Equal("Alimentos > Lácteos", vm.CategoryFilterOptions.Single(o => o.Id == 2).Path);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "La condición esperada no se cumplió dentro del tiempo límite.");
    }

    private sealed class Fixture
    {
        private readonly List<int?> _captured;
        private readonly Mock<IProductService> _productService = new();
        private readonly Mock<IExchangeRateService> _exchangeRate = new();
        private readonly Mock<IDialogService> _dialog = new();
        private readonly Mock<ICategoryService> _categories = new();

        public Fixture(List<int?> captured)
        {
            _captured = captured;

            _productService
                .Setup(s => s.GetPagedAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<int?>()))
                .Callback<string?, int, int, string?, string?, bool, CancellationToken, int?>(
                    (_, _, _, _, _, _, _, categoryId) => _captured.Add(categoryId))
                .ReturnsAsync(new PagedResultDto<ProductDto> { Items = new List<ProductDto>(), TotalCount = 0 });

            _exchangeRate.Setup(e => e.CurrentRate).Returns(50.00m);
            _exchangeRate.Setup(e => e.GetCurrentRateAsync()).ReturnsAsync((50.00m, DateTime.UtcNow));

            _categories
                .Setup(s => s.GetCategoriesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<CategoryDto>
                {
                    new(1, "Alimentos", null, 0, "Alimentos"),
                    new(2, "Lácteos", 1, 1, "Alimentos > Lácteos"),
                    new(3, "Bebidas", null, 0, "Bebidas"),
                });
        }

        public InventoryViewModel CreateViewModel() =>
            new(
                _productService.Object,
                _exchangeRate.Object,
                userSession: null,
                dialogService: _dialog.Object,
                dispatcherInvoker: null,
                categoryService: _categories.Object);

        public InventoryViewModel CreateViewModelWithSession(UserSession session) =>
            new(
                _productService.Object,
                _exchangeRate.Object,
                userSession: session,
                dialogService: _dialog.Object,
                dispatcherInvoker: null,
                categoryService: _categories.Object);
    }
}

/// <summary>
/// 8.179-T2 (S4 WPF): el builder de URL de <see cref="ProductService"/> agrega categoryId solo
/// cuando tiene valor, sin romper las llamadas existentes.
/// </summary>
public class ProductServiceCategoryFilterQueryTests
{
    [Fact]
    public async Task GetPagedAsync_WithCategoryId_AppendsCategoryIdToQueryString()
    {
        var handler = new StubHttpMessageHandler();
        var service = CreateService(handler);

        await service.GetPagedAsync(
            filter: null,
            page: 1,
            pageSize: 25,
            statusFilter: "active",
            sortBy: "name",
            isDescending: false,
            categoryId: 5);

        Assert.Contains("categoryId=5", handler.RequestPaths[0]);
    }

    [Fact]
    public async Task GetPagedAsync_WithoutCategoryId_DoesNotSendCategoryParameter()
    {
        var handler = new StubHttpMessageHandler();
        var service = CreateService(handler);

        await service.GetPagedAsync(filter: null, page: 1, pageSize: 25, statusFilter: "active");

        Assert.DoesNotContain("categoryId", handler.RequestPaths[0]);
    }

    private static ProductService CreateService(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") });

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestPaths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            await Task.CompletedTask;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[],"totalCount":0}""", Encoding.UTF8, "application/json")
            };
        }
    }
}
