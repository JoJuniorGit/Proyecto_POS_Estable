using System.Linq;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.DTOs;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Moq;
using Sales.Module.Data;
using Sales.Module.Services;
using Xunit;
using SalesService = Sales.Module.Services.SalesService;
using ICashDrawerService = Sales.Module.Interfaces.ICashDrawerService;
using CashDrawerStatus = Sales.Module.Entities.CashDrawerStatus;
using IInventoryService = Core.Interfaces.IInventoryService;

namespace CommandCenter.Tests.Unit;

public class BsPriceCeilingStandardTests
{
    [Fact]
    public void ProductDialogViewModel_CalculatePricing_WhenBsSHasThirdDecimal_RoundsCeilingTo2Decimals()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(842.21m);

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object);

        vm.CostPriceUSD = 0.60m;
        vm.ProfitMarginRetail = 35.00m;
        vm.RecalculatePricing("Cost");

        Assert.Equal(0.81m, vm.PriceRetailUSD);
        Assert.Equal(682.20m, vm.PriceRetailBsS);
        Assert.Equal(682.20m, vm.PriceBsS);
    }

    [Fact]
    public async Task ProductDialogViewModel_Save_PersistsCeilingPriceBsS()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(842.21m);
        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new System.Collections.Generic.List<Core.DTOs.ProductDto>());

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object);
        await vm.LoadMetadataAsync();

        vm.Name = "Grano Fino";
        vm.Sku = "GRANO001";
        vm.CostPriceUSD = 0.60m;
        vm.ProfitMarginRetail = 35.00m;

        bool closed = false;
        vm.RequestClose = res => closed = res;
        vm.SaveCommand.Execute(null);

        Assert.True(closed);
        Assert.Equal(682.20m, vm.ResultProduct.PriceBsS);
    }

    [Fact]
    public async Task AddItemAsync_WhenUnitPriceHasThirdDecimalBsS_UnitPriceAndSubtotalUseCeiling()
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        var inventoryMock = new Mock<IInventoryService>();
        var mediatorMock = new Mock<MediatR.IMediator>();
        var cashDrawerMock = new Mock<ICashDrawerService>();
        var settingsMock = new Mock<Core.Interfaces.ISystemSettingsService>();

        cashDrawerMock
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new Sales.Module.Entities.CashDrawerSession { Id = 1, Status = CashDrawerStatus.Open });

        var service = new SalesService(context, inventoryMock.Object, mediatorMock.Object, cashDrawerMock.Object, settingsMock.Object);
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var product = new ProductBuilder().WithId(77).WithSku("SKU-77").WithName("Grano Fino").WithCostAndMargin(0.60m, 35.00m).Build();
        inventoryMock.Setup(i => i.GetProductByIdAsync(77)).ReturnsAsync(product);

        var sale = await service.StartSaleAsync();
        var updated = await service.AddItemAsync(sale.Id, 77, 1, 842.21m);

        var item = updated.Items.Single(i => i.ProductId == 77);
        Assert.Equal(0.81m, item.UnitPrice);
        Assert.Equal(682.20m, item.UnitPriceBsS);
        Assert.Equal(682.20m, item.SubtotalBsS);

        var updatedTwo = await service.AddItemAsync(sale.Id, 77, 2, 842.21m);
        var itemTwo = updatedTwo.Items.Single(i => i.ProductId == 77);
        Assert.Equal(3m, itemTwo.Quantity);
        Assert.Equal(2046.60m, itemTwo.SubtotalBsS);
    }

[Fact]
    public void EditableSaleItemVm_WhenUnitPriceHasThirdDecimalBsS_UsesCeilingPerUnitAndQuantitySum()
    {
        var vm = new Desktop.Client.ViewModels.EditableSaleItemVm
        {
            UnitPriceRetailUSD = 0.81m,
            ExchangeRate = 842.21m,
            Quantity = 2m
        };

        Assert.Equal(682.20m, vm.UnitPriceBsS);
        Assert.Equal(1364.40m, vm.SubtotalBsS);
    }

    [Fact]
    public async Task ProductsController_Create_WhenClientSendsWrongBsPrice_PersistsCanonicalCeiling()
    {
        var captured = new Product();
        var mockService = new Mock<IInventoryService>();
        mockService.Setup(s => s.CreateProductAsync(It.IsAny<Product>())).ReturnsAsync((Product p) => { captured = p; return p; });
        mockService.Setup(s => s.GetTodayExchangeRateAsync()).ReturnsAsync(842.21m);
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(true);

        var controller = new ProductsController(mockService.Object, mockUser.Object);
        var request = new CreateProductDto
        {
            Name = "Grano Fino",
            SKU = "GRANO001",
            PriceRetailUSD = 0.81m,
            PriceBsS = 681.19m
        };

        var result = await controller.Create(request);

        Assert.IsType<Microsoft.AspNetCore.Mvc.CreatedAtActionResult>(result.Result);
        Assert.Equal(682.20m, captured.PriceBsS);
    }

    [Fact]
    public async Task ProductsController_Update_WhenClientSendsWrongBsPrice_RecalculatesCanonicalCeiling()
    {
        var existing = new Product { Id = 1, Name = "Grano Fino", SKU = "GRANO001", PriceRetailUSD = 0.81m, PriceBsS = 682.20m };
        var mockService = new Mock<IInventoryService>();
        mockService.Setup(s => s.GetProductByIdAsync(1)).ReturnsAsync(existing);
        mockService.Setup(s => s.UpdateProductAsync(existing)).Returns(Task.CompletedTask);
        mockService.Setup(s => s.GetTodayExchangeRateAsync()).ReturnsAsync(842.21m);
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(true);

        var controller = new ProductsController(mockService.Object, mockUser.Object);
        var request = new UpdateProductDto
        {
            Id = 1,
            Name = "Grano Fino",
            PriceRetailUSD = 0.80m,
            PriceBsS = 681.19m
        };

await controller.Update(1, request);

        Assert.Equal(673.77m, existing.PriceBsS);
    }
}