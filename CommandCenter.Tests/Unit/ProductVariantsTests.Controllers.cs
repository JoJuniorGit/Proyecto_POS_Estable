using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.DTOs;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public partial class ProductVariantsTests
{
    [Fact]
    public async Task VariantManagementViewModel_BatchEdit_SavesConversionFactorsCorrectly()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        var mockDialogService = new Mock<Desktop.Client.Services.IDialogService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        var parentDto = new ProductDto
        {
            Id = 50,
            Name = "Grupo Cervezas",
            IsGroupHeader = true,
            IsStockShared = true,
            HasIndependentPricing = false
        };

        var variant1 = new ProductDto { Id = 51, Name = "Botella 330ml", SKU = "1001", ConversionFactor = 1.0m, IsActive = true };
        var variant2 = new ProductDto { Id = 52, Name = "Six Pack", SKU = "1002", ConversionFactor = 6.0m, IsActive = true };

        mockProductService.Setup(s => s.GetVariantsAsync(50)).ReturnsAsync(new List<ProductDto> { variant1, variant2 });

        var entity1 = new Product { Id = 51, Name = "Botella 330ml", ConversionFactor = 1.0m, ParentProductId = 50 };
        var entity2 = new Product { Id = 52, Name = "Six Pack", ConversionFactor = 6.0m, ParentProductId = 50 };

        mockProductService.Setup(s => s.GetByIdAsync(51)).ReturnsAsync(entity1);
        mockProductService.Setup(s => s.GetByIdAsync(52)).ReturnsAsync(entity2);

        var vm = new Desktop.Client.ViewModels.VariantManagementViewModel(
            mockProductService.Object,
            mockExchangeRate.Object,
            mockDialogService.Object,
            parentDto);

        await vm.LoadVariantsAsync();
        Assert.Equal(2, vm.Variants.Count);

        // Modificar el factor de six pack de 6 a 8
        vm.Variants[1].ConversionFactor = 8.0m;
        Assert.True(vm.Variants[1].IsModified);

        await vm.SaveBatchAsync();

        mockProductService.Verify(s => s.UpdateAsync(It.Is<Product>(p => p.Id == 52 && p.ConversionFactor == 8.0m)), Times.Once);
    }

    [Fact]
    public void ProductDialogViewModel_ShowConversionFactorInput_VisibilityMatrix()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(
            mockProductService.Object,
            mockExchangeRate.Object);

        // 1. Producto independiente -> False
        Assert.False(vm.ShowConversionFactorInput);

        // 2. Grupo -> False
        vm.IsGroupHeader = true;
        Assert.False(vm.ShowConversionFactorInput);
        vm.IsGroupHeader = false;

        // 3. Variante bajo padre con IsStockShared = false -> False
        vm.SelectedParentProduct = new ProductDto { Id = 1, Name = "Padre Individual", IsStockShared = false };
        Assert.False(vm.ShowConversionFactorInput);

        // 4. Variante bajo padre con IsStockShared = true -> True
        vm.SelectedParentProduct = new ProductDto { Id = 2, Name = "Padre Stock Compartido", IsStockShared = true };
        Assert.True(vm.ShowConversionFactorInput);

        // 5. Si es servicio de adelanto de efectivo -> False
        vm.IsCashAdvance = true;
        Assert.False(vm.ShowConversionFactorInput);
    }

    [Fact]
    public async Task CreateParentProduct_WithIndependentPricing_ZeroesParentPricesAndCostCleanly()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Name = "Zapato Deportivo Varias Tallas",
            IsGroupHeader = true,
            HasIndependentPricing = true,
            PriceRetailUSD = 99.99m,
            CostPriceUSD = 50.00m,
            ProfitMarginRetail = 99.98m,
            HasWholesale = true,
            PriceWholesaleUSD = 80.00m
        };

        var created = await service.CreateProductAsync(parent);

        Assert.NotNull(created);
        Assert.True(created.IsGroupHeader);
        Assert.True(created.HasIndependentPricing);
        Assert.Equal(0m, created.PriceRetailUSD);
        Assert.Equal(0m, created.PriceUSD);
        Assert.Equal(0m, created.CostPriceUSD);
        Assert.Equal(0m, created.Cost);
        Assert.Equal(0m, created.ProfitMarginRetail);
        Assert.Equal(0m, created.ProfitPercentage);
        Assert.False(created.HasWholesale);
        Assert.Equal(0m, created.PriceWholesaleUSD);
    }

    [Fact]
    public async Task UpdateParentProduct_WithIndependentPricing_DoesNotOverwriteVariantPrices()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Name = "Zapato Deportivo Varias Tallas",
            IsGroupHeader = true,
            HasIndependentPricing = true
        };
        var savedParent = await service.CreateProductAsync(parent);

        var variant = new Product
        {
            Name = "Zapato Deportivo Talla 42",
            SKU = "7590001112223",
            ParentProductId = savedParent.Id,
            PriceRetailUSD = 65.00m,
            CostPriceUSD = 30.00m,
            ProfitMarginRetail = 116.67m,
            StockQuantity = 10m
        };
        var savedVariant = await service.CreateProductAsync(variant);

        // Actualizar el nombre del padre
        savedParent.Name = "Zapato Deportivo Edición 2026";
        await service.UpdateProductAsync(savedParent);

        var refreshedVariant = await db.Products.FindAsync(savedVariant.Id);
        Assert.NotNull(refreshedVariant);
        Assert.Equal(65.00m, refreshedVariant.PriceRetailUSD);
        Assert.Equal(30.00m, refreshedVariant.CostPriceUSD);
    }

    [Fact]
    public async Task UpdateParentProduct_AttemptChangeIndependentPricing_ThrowsInvalidOperationException()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Name = "Camisa Polo Colores",
            IsGroupHeader = true,
            HasIndependentPricing = true
        };
        var savedParent = await service.CreateProductAsync(parent);

        // Intentar cambiar HasIndependentPricing de true a false
        savedParent.HasIndependentPricing = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateProductAsync(savedParent));
        Assert.Contains("No se permite cambiar las banderas", ex.Message);
    }

    [Fact]
    public void ProductItemViewModel_DisplaysDashForAllPriceColumns_WhenGroupHeaderWithIndependentPricing()
    {
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        // 1. Agrupador con Precios Individuales -> "—" en todo
        var parentDto = new ProductDto
        {
            Id = 1,
            Name = "Camisa Polo",
            SKU = "GRP-12345",
            IsGroupHeader = true,
            HasIndependentPricing = true,
            Cost = 0m,
            PriceUSD = 0m,
            PriceBsS = 0m
        };
        var parentVm = new Desktop.Client.ViewModels.ProductItemViewModel(parentDto, mockExchangeRate.Object);

        Assert.Equal("—", parentVm.DisplayCost);
        Assert.Equal("—", parentVm.DisplayRetailPrice);
        Assert.Equal("—", parentVm.DisplayWholesalePrice);

        // 2. Variante con Precios Propios -> Precios numéricos formateados
        var variantDto = new ProductDto
        {
            Id = 2,
            Name = "Camisa Polo Talla M",
            SKU = "7591112223334",
            ParentProductId = 1,
            IsGroupHeader = false,
            HasIndependentPricing = false,
            Cost = 15.00m,
            PriceUSD = 25.00m,
            PriceBsS = 912.50m
        };
        var variantVm = new Desktop.Client.ViewModels.ProductItemViewModel(variantDto, mockExchangeRate.Object);

        Assert.Equal(string.Format("${0:N2}", 15.00m), variantVm.DisplayCost);
        Assert.Equal(string.Format("Bs.S {0:N2}", 912.50m), variantVm.DisplayRetailPrice);
    }

    [Fact]
    public void ProductDialogViewModel_WithIndependentPricing_DisablesPricingInputsAndShowsNotice()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(
            mockProductService.Object,
            mockExchangeRate.Object);

        // Al marcar Agrupador y Precios Individuales
        vm.IsGroupHeader = true;
        vm.HasIndependentPricing = true;

        Assert.False(vm.ShowPricingInputs);
        Assert.True(vm.ShowIndependentPricingNotice);
        Assert.False(vm.CanEditPricing);
        Assert.False(vm.CanEditWholesale);

        // Al desmarcar Precios Individuales
        vm.HasIndependentPricing = false;
        Assert.True(vm.ShowPricingInputs);
        Assert.False(vm.ShowIndependentPricingNotice);
        Assert.True(vm.CanEditPricing);
    }

    [Fact]
    public async Task AdjustStockAsync_ParentProduct_WithStockShared_Succeeds()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Name = "Huevos Tipo A Pool",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 100m
        };
        var savedParent = await service.CreateProductAsync(parent);

        await service.AdjustStockAsync(savedParent.Id, 50m, "Reabastecimiento de bodega central");

        var updated = await db.Products.FindAsync(savedParent.Id);
        Assert.NotNull(updated);
        Assert.Equal(150m, updated.StockQuantity);

        var movement = await db.StockMovements.FirstOrDefaultAsync(m => m.ProductId == savedParent.Id);
        Assert.NotNull(movement);
        Assert.Contains("Ajuste Manual: Reabastecimiento", movement.Reason);
    }

    [Fact]
    public async Task AdjustStockAsync_ParentProduct_WithIndividualStock_ThrowsInvalidOperationException()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Name = "Camisa Polo Tallas",
            IsGroupHeader = true,
            IsStockShared = false
        };
        var savedParent = await service.CreateProductAsync(parent);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AdjustStockAsync(savedParent.Id, 10m, "Intento de ajuste directo en padre"));

        Assert.Equal(Core.Constants.InventoryMessages.GroupIndividualStockAdjustmentBlocked, ex.Message);
    }

    [Fact]
    public async Task AdjustStockAsync_VariantProduct_UnderSharedParent_ThrowsInvalidOperationException()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Name = "Cerveza Artesanal Pool",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 500m
        };
        var savedParent = await service.CreateProductAsync(parent);

        var variant = new Product
        {
            Name = "Cerveza Six Pack",
            SKU = "7598889990001",
            ParentProductId = savedParent.Id,
            ConversionFactor = 6.0m
        };
        var savedVariant = await service.CreateProductAsync(variant);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AdjustStockAsync(savedVariant.Id, 2m, "Intento de ajuste directo en six pack"));

        Assert.Equal(Core.Constants.InventoryMessages.VariantSharedStockAdjustmentBlocked, ex.Message);
    }

}
