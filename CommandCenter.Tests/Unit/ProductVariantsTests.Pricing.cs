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
    public async Task ProductDialogViewModel_SelectingParent_InheritsPricing_AndDisablesPriceEditing()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(40.00m);

        var parent = new ProductDto
        {
            Id = 50,
            Name = "Camisa Polo Algodón",
            SKU = "GRP-POLO",
            CostPriceUSD = 8.50m,
            ProfitMarginRetail = 50m,
            PriceRetailUSD = 12.75m,
            HasWholesale = true,
            ProfitMarginWholesale = 30m,
            PriceWholesaleUSD = 11.05m,
            MinWholesaleQuantity = 12m,
            IsFractional = false,
            UnitOfMeasure = UnitOfMeasureType.Und
        };

        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto> { parent });

        var newProduct = new Product
        {
            CostPriceUSD = 3.00m,
            ProfitMarginRetail = 20m,
            PriceRetailUSD = 3.60m
        };

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object, newProduct);
        await vm.LoadMetadataAsync();

        // Initially Ninguno selected
        Assert.Equal(0, vm.SelectedParentProduct?.Id);
        Assert.False(vm.IsInheritingPricing);
        Assert.True(vm.CanEditPricing);

        // Select Parent
        var parentOption = vm.ParentProducts.First(p => p.Id == 50);
        vm.SelectedParentProduct = parentOption;

        // Verify explicit inheritance field by field
        Assert.Equal(50, vm.ParentProductId);
        Assert.True(vm.IsInheritingPricing);
        Assert.False(vm.CanEditPricing);
        Assert.False(vm.CanEditWholesale);

        Assert.Equal(8.50m, vm.CostPriceUSD);
        Assert.Equal(50m, vm.ProfitMarginRetail);
        Assert.Equal(12.75m, vm.PriceRetailUSD);
        Assert.True(vm.HasWholesale);
        Assert.Equal(30m, vm.ProfitMarginWholesale);
        Assert.Equal(11.05m, vm.PriceWholesaleUSD);
        Assert.Equal(12m, vm.MinWholesaleQuantity);
        Assert.False(vm.IsFractional);
        Assert.Equal(UnitOfMeasureType.Und, vm.UnitOfMeasureType);
        Assert.Equal(510.00m, vm.PriceRetailBsS); // 12.75 * 40.00
    }

    [Fact]
    public async Task ProductDialogViewModel_SelectingNone_RestoresOriginalPricing_AndReenablesPriceEditing()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(35.00m);

        var parent = new ProductDto
        {
            Id = 55,
            Name = "Galletas Surtidas",
            CostPriceUSD = 5.00m,
            ProfitMarginRetail = 40m,
            PriceRetailUSD = 7.00m
        };

        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto> { parent });

        var initialProduct = new Product
        {
            CostPriceUSD = 2.50m,
            ProfitMarginRetail = 30m,
            PriceRetailUSD = 3.25m,
            HasWholesale = false
        };

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object, initialProduct);
        await vm.LoadMetadataAsync();

        // 1. Manually edit before choosing a parent
        vm.CostPriceUSD = 4.00m;
        vm.ProfitMarginRetail = 25m;
        vm.RecalculatePricing("Cost");
        Assert.Equal(5.00m, vm.PriceRetailUSD);

        // 2. Select Parent
        vm.SelectedParentProduct = vm.ParentProducts.First(p => p.Id == 55);
        Assert.Equal(5.00m, vm.CostPriceUSD);
        Assert.Equal(40m, vm.ProfitMarginRetail);
        Assert.Equal(7.00m, vm.PriceRetailUSD);
        Assert.False(vm.CanEditPricing);

        // 3. Switch back to Ninguno
        vm.SelectedParentProduct = vm.ParentProducts.First(p => p.Id == 0);
        Assert.Null(vm.ParentProductId);
        Assert.False(vm.IsInheritingPricing);
        Assert.True(vm.CanEditPricing);

        // Verify restoration of the previous manual edit
        Assert.Equal(4.00m, vm.CostPriceUSD);
        Assert.Equal(25m, vm.ProfitMarginRetail);
        Assert.Equal(5.00m, vm.PriceRetailUSD);
    }

    [Fact]
    public async Task ProductDialogViewModel_RapidParentSwitching_MaintainsCorrectStateAndPricing()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(30.00m);

        var parentA = new ProductDto { Id = 1, Name = "Parent A", CostPriceUSD = 10m, ProfitMarginRetail = 50m, PriceRetailUSD = 15m };
        var parentB = new ProductDto { Id = 2, Name = "Parent B", CostPriceUSD = 20m, ProfitMarginRetail = 25m, PriceRetailUSD = 25m };

        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto> { parentA, parentB });

        var initialProduct = new Product { CostPriceUSD = 5m, ProfitMarginRetail = 20m, PriceRetailUSD = 6m };
        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object, initialProduct);
        await vm.LoadMetadataAsync();

        // Rapid switches
        for (int i = 0; i < 5; i++)
        {
            vm.SelectedParentProduct = vm.ParentProducts.First(p => p.Id == 1);
            Assert.Equal(10m, vm.CostPriceUSD);
            Assert.Equal(15m, vm.PriceRetailUSD);

            vm.SelectedParentProduct = vm.ParentProducts.First(p => p.Id == 2);
            Assert.Equal(20m, vm.CostPriceUSD);
            Assert.Equal(25m, vm.PriceRetailUSD);

            vm.SelectedParentProduct = vm.ParentProducts.First(p => p.Id == 0);
            Assert.Equal(5m, vm.CostPriceUSD);
            Assert.Equal(6m, vm.PriceRetailUSD);
        }

        Assert.Null(vm.ParentProductId);
        Assert.False(vm.IsInheritingPricing);
        Assert.True(vm.CanEditPricing);
    }

    [Fact]
    public async Task ProductDialogViewModel_Save_PersistsSelectedUnitOfMeasure()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);
        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto>());

        // 1. Create a product with Kg
        var vmNew = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object);
        await vmNew.LoadMetadataAsync();

        vmNew.Name = "Queso Llanero";
        vmNew.Sku = "759000123";
        vmNew.CostPriceUSD = 4.50m;
        vmNew.ProfitMarginRetail = 30m;
        vmNew.UnitOfMeasureType = UnitOfMeasureType.Kg;
        vmNew.IsFractional = true;

        bool closeResult = false;
        vmNew.RequestClose = res => closeResult = res;
        vmNew.SaveCommand.Execute(null);

        Assert.True(closeResult);
        Assert.Equal(UnitOfMeasureType.Kg, vmNew.ResultProduct.UnitOfMeasure);
        Assert.Equal("Queso Llanero", vmNew.ResultProduct.Name);

        // 2. Edit an existing product with Lt
        var existing = new Product
        {
            Id = 42,
            Name = "Aceite de Oliva 1L",
            SKU = "759999888",
            CostPriceUSD = 8.00m,
            ProfitMarginRetail = 25m,
            PriceRetailUSD = 10.00m,
            UnitOfMeasure = UnitOfMeasureType.Lt,
            IsFractional = true
        };

        var vmEdit = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object, existing);
        await vmEdit.LoadMetadataAsync();

        Assert.Equal(UnitOfMeasureType.Lt, vmEdit.UnitOfMeasureType);

        // Change from Lt to Ml
        vmEdit.UnitOfMeasureType = UnitOfMeasureType.Ml;
        closeResult = false;
        vmEdit.RequestClose = res => closeResult = res;
        vmEdit.SaveCommand.Execute(null);

        Assert.True(closeResult);
        Assert.Equal(UnitOfMeasureType.Ml, vmEdit.ResultProduct.UnitOfMeasure);
    }

    [Fact]
    public void ProductItemViewModel_WhenIsCashAdvance_RendersServicioLabel_AndIsNotStockCritical()
    {
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        var dto = new ProductDto
        {
            Id = 99,
            Name = "Adelanto de Efectivo",
            SKU = "ADV-001",
            Cost = 0m,
            StockQuantity = 0m,
            IsCashAdvance = true,
            IsActive = true
        };

        var vm = new Desktop.Client.ViewModels.ProductItemViewModel(dto, mockExchangeRate.Object);

        Assert.True(vm.IsCashAdvance);
        Assert.Equal("Servicio", vm.FormattedStockQuantity);
        Assert.False(vm.IsStockCritical);
    }

    [Fact]
    public async Task ProductDialogViewModel_WhenIsCashAdvance_ForcesUndAndNonFractional_AndResetsStock()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);
        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto>());

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object);
        await vm.LoadMetadataAsync();

        vm.Name = "Adelanto Especial";
        vm.Sku = "99887766";
        vm.IsFractional = true;
        vm.UnitOfMeasureType = UnitOfMeasureType.Kg;
        vm.StockQuantity = 50m;
        vm.LowStockThreshold = 10m;

        // Activate Cash Advance
        vm.IsCashAdvance = true;

        Assert.False(vm.IsFractional);
        Assert.Equal(UnitOfMeasureType.Und, vm.UnitOfMeasureType);
        Assert.Equal(0m, vm.StockQuantity);
        Assert.Equal(0m, vm.LowStockThreshold);
        Assert.False(vm.ShowStockInputs);
        Assert.False(vm.CanEditFractional);
        Assert.False(vm.CanEditGroupHeader);

        bool closed = false;
        vm.RequestClose = res => closed = res;
        vm.SaveCommand.Execute(null);

        Assert.True(closed);
        Assert.True(vm.ResultProduct.IsCashAdvance);
        Assert.False(vm.ResultProduct.IsFractional);
        Assert.Equal(UnitOfMeasureType.Und, vm.ResultProduct.UnitOfMeasure);
        Assert.Equal(0m, vm.ResultProduct.StockQuantity);
        Assert.Equal(0m, vm.ResultProduct.LowStockThreshold);
        Assert.Null(vm.ResultProduct.ParentProductId);
        Assert.False(vm.ResultProduct.IsGroupHeader);
    }

}
