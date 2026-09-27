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
            .ReturnsAsync(new Sales.Module.DTOs.CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var service = new SalesService(context, inventoryMock.Object, mediatorMock.Object, cashDrawerMock.Object, settingsMock.Object);
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var product = new ProductBuilder().WithId(77).WithSku("SKU-77").WithName("Grano Fino").WithCostAndMargin(0.60m, 35.00m).BuildSaleProductInfo();
        inventoryMock.Setup(i => i.GetSaleProductByIdAsync(77)).ReturnsAsync(product);

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
    public async Task ProductsController_Create_WhenValid_DelegatesToInventoryService()
    {
        CreateProductDto? capturedDto = null;
        var mockInventory = new Mock<IInventoryService>();
        var mockService = new Mock<IProductManagementService>();
        mockService.Setup(s => s.CreateProductFromDtoAsync(It.IsAny<CreateProductDto>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync((CreateProductDto dto, System.Threading.CancellationToken ct) =>
            {
                capturedDto = dto;
                return new ProductDto { Id = 1, PriceBsS = 682.20m, Name = dto.Name, SKU = dto.SKU ?? string.Empty };
            });
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(true);

        var controller = new ProductsController(mockInventory.Object, mockService.Object, mockUser.Object);
        var request = new CreateProductDto
        {
            Name = "Grano Fino",
            SKU = "GRANO001",
            PriceRetailUSD = 0.81m,
            PriceBsS = 681.19m
        };

        var result = await controller.Create(request);

        Assert.IsType<Microsoft.AspNetCore.Mvc.CreatedAtActionResult>(result.Result);
        Assert.NotNull(capturedDto);
        Assert.Equal("GRANO001", capturedDto.SKU);
    }

    [Fact]
    public async Task InventoryService_CreateProductFromDto_WhenClientSendsWrongBsPrice_PersistsCanonicalCeiling()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        context.ExchangeRateHistory.Add(new ExchangeRateHistory
        {
            Date = Core.Helpers.TimeZoneHelper.GetVenezuelaDate(),
            Rate = 842.21m,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var userMock = new Mock<ICurrentUserService>();
        userMock.Setup(u => u.CanMutateCatalog).Returns(true);
        var service = new Inventory.Module.Services.InventoryService(context, userMock.Object);

        var request = new CreateProductDto
        {
            Name = "Grano Fino",
            SKU = "GRANO001",
            PriceRetailUSD = 0.81m,
            PriceBsS = 681.19m
        };

        var created = await service.CreateProductFromDtoAsync(request);

        Assert.Equal(682.20m, created.PriceBsS);
    }

    [Fact]
    public async Task InventoryService_CreateProductFromDto_WithInitialStock_PersistsStockQuantity()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        context.ExchangeRateHistory.Add(new ExchangeRateHistory
        {
            Date = Core.Helpers.TimeZoneHelper.GetVenezuelaDate(),
            Rate = 842.21m,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var userMock = new Mock<ICurrentUserService>();
        userMock.Setup(u => u.CanMutateCatalog).Returns(true);
        var service = new Inventory.Module.Services.InventoryService(context, userMock.Object);

        var request = new CreateProductDto
        {
            Name = "Harina PAN",
            SKU = "HARINA001",
            PriceRetailUSD = 1.20m,
            StockQuantity = 24.5m
        };

        var created = await service.CreateProductFromDtoAsync(request);

        var persisted = await context.Products.FindAsync(created.Id);
        Assert.NotNull(persisted);
        Assert.Equal(24.5m, persisted.StockQuantity);
    }

    [Fact]
    public async Task ProductsController_Update_WhenValid_DelegatesToInventoryService()
    {
        UpdateProductDto? capturedDto = null;
        var mockInventory = new Mock<IInventoryService>();
        var mockService = new Mock<IProductManagementService>();
        mockService.Setup(s => s.UpdateProductFromDtoAsync(1, It.IsAny<UpdateProductDto>(), It.IsAny<System.Threading.CancellationToken>()))
            .Returns((int id, UpdateProductDto dto, System.Threading.CancellationToken ct) =>
            {
                capturedDto = dto;
                return Task.CompletedTask;
            });
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(true);

        var controller = new ProductsController(mockInventory.Object, mockService.Object, mockUser.Object);
        var request = new UpdateProductDto
        {
            Id = 1,
            Name = "Grano Fino",
            PriceRetailUSD = 0.80m,
            PriceBsS = 681.19m
        };

        var result = await controller.Update(1, request);

        Assert.IsType<Microsoft.AspNetCore.Mvc.NoContentResult>(result);
        Assert.NotNull(capturedDto);
        Assert.Equal(0.80m, capturedDto.PriceRetailUSD);
    }

    [Fact]
    public async Task InventoryService_UpdateProductFromDto_WhenClientSendsWrongBsPrice_RecalculatesCanonicalCeiling()
    {
        var context = TestDatabaseFactory.CreateInventoryDbContext();
        context.ExchangeRateHistory.Add(new ExchangeRateHistory
        {
            Date = Core.Helpers.TimeZoneHelper.GetVenezuelaDate(),
            Rate = 842.21m,
            UpdatedAt = DateTime.UtcNow
        });
        var existing = new Product { SKU = "GRANO001", Name = "Grano Fino", PriceRetailUSD = 0.81m, PriceBsS = 682.20m, StockQuantity = 10m };
        context.Products.Add(existing);
        await context.SaveChangesAsync();

        var userMock = new Mock<ICurrentUserService>();
        userMock.Setup(u => u.CanMutateCatalog).Returns(true);
        var service = new Inventory.Module.Services.InventoryService(context, userMock.Object);

        var request = new UpdateProductDto
        {
            Id = existing.Id,
            Name = "Grano Fino",
            PriceRetailUSD = 0.80m,
            PriceBsS = 681.19m
        };

        await service.UpdateProductFromDtoAsync(existing.Id, request);

        var updated = await context.Products.FindAsync(existing.Id);
        Assert.NotNull(updated);
        Assert.Equal(673.77m, updated.PriceBsS);
    }
}