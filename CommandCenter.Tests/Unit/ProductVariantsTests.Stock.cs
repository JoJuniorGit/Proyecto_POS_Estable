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
    public async Task ProductDialogViewModel_WhenIsCashAdvance_DisablesGroupAndVariantSelection()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);
        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto>());

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object);
        await vm.LoadMetadataAsync();

        vm.IsGroupHeader = true;
        Assert.True(vm.IsGroupHeader);

        vm.IsCashAdvance = true;
        Assert.False(vm.IsGroupHeader);
        Assert.False(vm.CanEditGroupHeader);

        // Try setting IsGroupHeader while IsCashAdvance is active
        vm.IsGroupHeader = true;
        Assert.False(vm.IsCashAdvance); // Mutually exclusive switch
    }

    [Fact]
    public async Task ProductDialogViewModel_RapidCashAdvanceSwitching_MaintainsCorrectStateAndReactivity()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);
        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto>());

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object);
        await vm.LoadMetadataAsync();

        for (int i = 0; i < 10; i++)
        {
            vm.IsCashAdvance = true;
            Assert.True(vm.IsCashAdvance);
            Assert.False(vm.ShowStockInputs);
            Assert.False(vm.CanEditGroupHeader);

            vm.IsCashAdvance = false;
            Assert.False(vm.IsCashAdvance);
            Assert.True(vm.ShowStockInputs);
            Assert.True(vm.CanEditGroupHeader);
        }
    }

    [Fact]
    public async Task ProductDialogViewModel_EditMode_MaintainsIsGroupHeader_AndPreservesValidState()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);
        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto>());

        var existingGroup = new Product
        {
            Id = 55,
            Name = "Refrescos 2L (Grupo)",
            SKU = "GRP-63859000000000",
            IsGroupHeader = true,
            CostPriceUSD = 1.20m,
            PriceRetailUSD = 2.00m,
            ProfitMarginRetail = 66.67m,
            IsActive = true
        };

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object, existingGroup);
        await vm.LoadMetadataAsync();

        // Checkbox must retain IsGroupHeader = true
        Assert.True(vm.IsGroupHeader);
        Assert.True(vm.CanEditGroupHeader);
        Assert.True(vm.IsSkuValid);
        Assert.Empty(vm.SkuVerificationMessage);

        bool closed = false;
        vm.RequestClose = res => closed = res;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.True(vm.ResultProduct.IsGroupHeader);
        Assert.Equal(55, vm.ResultProduct.Id);
        Assert.Equal("Refrescos 2L (Grupo)", vm.ResultProduct.Name);
    }

    [Fact]
    public async Task ProductDialogViewModel_ManageVariantsCommand_OpensVariantManagementDialog_WhenEditingGroup()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        var mockDialogService = new Mock<Desktop.Client.Services.IDialogService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        var existingGroup = new Product
        {
            Id = 55,
            Name = "Refrescos 2L (Grupo)",
            SKU = "GRP-12345",
            IsGroupHeader = true,
            IsStockShared = true,
            PriceRetailUSD = 2.00m
        };

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(
            mockProductService.Object,
            mockExchangeRate.Object,
            existingGroup,
            dialogService: mockDialogService.Object);

        Assert.True(vm.ShowManageVariantsButton);

        await vm.ManageVariantsCommand.ExecuteAsync(null);

        mockDialogService.Verify(d => d.ShowVariantManagementDialogAsync(It.Is<ProductDto>(p => p.Id == 55 && p.IsGroupHeader)), Times.Once);
    }

    [Fact]
    public async Task InventoryService_UpdateProduct_WhenUncheckingGroupHeaderWithVariants_ThrowsException()
    {
        var invDb = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(invDb, userMock.Object);

        var parent = await service.CreateProductAsync(new Product
        {
            Name = "Galletas Surtidas",
            IsGroupHeader = true,
            PriceRetailUSD = 1.00m,
            StockQuantity = 0m
        });

        await service.CreateProductAsync(new Product
        {
            Name = "Galletas Chocolate",
            SKU = "99001",
            ParentProductId = parent.Id,
            StockQuantity = 10m
        });

        parent.IsGroupHeader = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateProductAsync(parent));
        Assert.Contains("No se puede desmarcar el grupo", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("variantes asociadas", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InventoryService_GroupHeader_AutoGeneratesGroupKeyFromName_WhenEmpty()
    {
        var invDb = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(invDb, userMock.Object);

        var created = await service.CreateProductAsync(new Product
        {
            Name = "Helados 1L",
            IsGroupHeader = true,
            GroupKey = null,
            PriceRetailUSD = 3.50m
        });

        Assert.Equal("Helados 1L", created.GroupKey);
    }

    [Fact]
    public async Task ProductDialogViewModel_WithActiveVariants_DisablesGroupHeaderCheckbox()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);
        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto>());
        mockProductService.Setup(s => s.GetVariantsAsync(10)).ReturnsAsync(new List<ProductDto>
        {
            new ProductDto { Id = 11, Name = "Variante 1", ParentProductId = 10, IsDeleted = false }
        });

        var existingGroup = new Product
        {
            Id = 10,
            Name = "Jugos 1L (Grupo)",
            SKU = "GRP-12345",
            IsGroupHeader = true,
            IsActive = true
        };

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object, existingGroup);
        await vm.LoadMetadataAsync();

        Assert.True(vm.IsGroupHeader);
        Assert.True(vm.HasActiveVariants);
        Assert.Equal(1, vm.ActiveVariantsCount);
        Assert.False(vm.CanEditGroupHeader);
        Assert.False(vm.CanSelectParentProduct);
        Assert.Contains("1 variante(s) asociada(s)", vm.GroupHeaderToolTip);
    }

    [Fact]
    public async Task ProductDialogViewModel_WhenIsCashAdvance_DisablesParentProductComboBox()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);
        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto>());

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object, null);
        await vm.LoadMetadataAsync();

        Assert.True(vm.CanSelectParentProduct);
        vm.IsCashAdvance = true;
        Assert.False(vm.CanSelectParentProduct);
        Assert.False(vm.CanEditGroupHeader);
    }

    [Fact]
    public async Task ProductDialogViewModel_WhenHasVariants_PreventsCashAdvanceActivation()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);
        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto>());
        mockProductService.Setup(s => s.GetVariantsAsync(20)).ReturnsAsync(new List<ProductDto>
        {
            new ProductDto { Id = 21, Name = "Variante A", ParentProductId = 20, IsDeleted = false }
        });

        var existingGroup = new Product { Id = 20, Name = "Grupo A", IsGroupHeader = true, SKU = "GRP-20" };
        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object, existingGroup);
        await vm.LoadMetadataAsync();

        vm.IsCashAdvance = true;
        Assert.False(vm.IsCashAdvance);
        Assert.True(vm.IsError);
        Assert.Contains("variantes asociadas", vm.ErrorMessage);
    }

    [Fact]
    public async Task ProductDialogViewModel_NewProduct_CanMarkAsGroupHeader_AndSaveWithoutSku()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);
        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto>());

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object, null);
        await vm.LoadMetadataAsync();

        vm.Name = "Pizzas Familiares (Grupo)";
        vm.IsGroupHeader = true;
        vm.CostPriceUSD = 5.00m;
        vm.ProfitMarginRetail = 40.00m;
        vm.PriceRetailUSD = 7.00m;

        bool closed = false;
        vm.RequestClose = res => closed = res;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.True(vm.ResultProduct.IsGroupHeader);
        Assert.Equal("Pizzas Familiares (Grupo)", vm.ResultProduct.Name);
        Assert.Equal("Pizzas Familiares (Grupo)", vm.ResultProduct.GroupKey);
    }

    [Fact]
    public async Task ProductDialogViewModel_TogglingGroupHeader_RestoresPricingSnapshot()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        var parentDto = new ProductDto
        {
            Id = 99,
            Name = "Padre Dulces",
            CostPriceUSD = 2.00m,
            PriceRetailUSD = 4.00m,
            ProfitMarginRetail = 100.00m
        };
        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto> { parentDto });

        var initial = new Product
        {
            Id = 5,
            Name = "Chupeta",
            CostPriceUSD = 0.50m,
            PriceRetailUSD = 1.00m,
            ProfitMarginRetail = 100.00m,
            SKU = "1005"
        };

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object, initial);
        await vm.LoadMetadataAsync();

        // Select parent -> inherits 2.00 / 4.00
        vm.SelectedParentProduct = vm.ParentProducts.First(p => p.Id == 99);
        Assert.Equal(2.00m, vm.CostPriceUSD);
        Assert.Equal(4.00m, vm.PriceRetailUSD);

        // Toggle IsGroupHeader = true -> restores snapshot (0.50 / 1.00)
        vm.IsGroupHeader = true;
        Assert.Equal(0.50m, vm.CostPriceUSD);
        Assert.Equal(1.00m, vm.PriceRetailUSD);
    }

}
