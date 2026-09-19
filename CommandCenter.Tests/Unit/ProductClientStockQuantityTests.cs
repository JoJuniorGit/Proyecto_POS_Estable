using System.Text;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ProductClientStockQuantityTests
{
    [Fact]
    public async Task ProductDialogViewModel_Create_MapsStockQuantityIntoCreateProductDto()
    {
        var mockProductService = new Mock<IProductService>();
        var mockExchangeRate = new Mock<IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);
        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto>());

        var vm = new ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object)
        {
            Name = "Harina P.A.N.",
            Sku = "759000123",
            CostPriceUSD = 4.50m,
            ProfitMarginRetail = 30m,
            StockQuantity = 42m
        };
        await vm.LoadMetadataAsync();

        bool closed = false;
        vm.RequestClose = res => closed = res;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Equal(42m, vm.ResultProduct.StockQuantity);
    }

    [Fact]
    public async Task ProductService_CreateAsync_SendsStockQuantityInRequestBody()
    {
        string? capturedBody = null;
        var client = new HttpClient(new CapturingHandler(body => capturedBody = body))
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };
        var service = new ProductService(client);

        await service.CreateAsync(new CreateProductDto
        {
            Name = "Harina P.A.N.",
            SKU = "759000123",
            CostPriceUSD = 4.50m,
            ProfitMarginRetail = 30m,
            StockQuantity = 42m
        });

        Assert.NotNull(capturedBody);
        Assert.Contains("\"stockQuantity\":42", capturedBody);
    }

    [Fact]
    public async Task InventoryViewModel_AutoSave_AfterProfitPercentageEdit_SendsEditedMarginInUpdateProductDto()
    {
        // Separate instances: the grid item must not alias the server re-read snapshot.
        var listedProduct = new ProductDto
        {
            Id = 7,
            Name = "Harina P.A.N.",
            SKU = "759000123",
            Cost = 4.50m,
            CostPriceUSD = 4.50m,
            ProfitMarginRetail = 30m,
            ProfitPercentage = 30m
        };
        var serverProduct = new ProductDto
        {
            Id = 7,
            Name = "Harina P.A.N.",
            SKU = "759000123",
            Cost = 4.50m,
            CostPriceUSD = 4.50m,
            ProfitMarginRetail = 30m,
            ProfitPercentage = 30m
        };

        var sentDto = new TaskCompletionSource<UpdateProductDto>(TaskCreationOptions.RunContinuationsAsynchronously);

        var mockProductService = new Mock<IProductService>();
        mockProductService
            .Setup(s => s.GetPagedAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResultDto<ProductDto> { Items = new List<ProductDto> { listedProduct }, TotalCount = 1 });
        mockProductService.Setup(s => s.GetByIdAsync(serverProduct.Id)).ReturnsAsync(serverProduct);
        mockProductService
            .Setup(s => s.UpdateAsync(It.IsAny<UpdateProductDto>()))
            .Callback<UpdateProductDto>(dto => sentDto.TrySetResult(dto))
            .Returns(Task.CompletedTask);

        var mockExchangeRate = new Mock<IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        var vm = new InventoryViewModel(mockProductService.Object, mockExchangeRate.Object);
        await vm.EnsureLoadedAsync();
        var item = Assert.Single(vm.Products);

        // The in-grid edit drives the real auto-save callback wired by InventoryViewModel.
        item.ProfitPercentage = 45m;

        var update = await sentDto.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 45 differs from the server snapshot (30) and from the UpdateProductDto default (0),
        // so only the auto-save pass-through can send this value.
        Assert.Equal(45m, update.ProfitMarginRetail);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Action<string> _onBody;

        public CapturingHandler(Action<string> onBody) => _onBody = onBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            _onBody(body);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":1,\"name\":\"Harina P.A.N.\",\"stockQuantity\":42}", Encoding.UTF8, "application/json")
            };
        }
    }
}
