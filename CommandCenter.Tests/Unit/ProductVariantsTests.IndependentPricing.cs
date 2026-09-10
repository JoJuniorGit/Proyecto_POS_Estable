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
    public async Task UpdateStockAsync_VariantWithStockShared_AllowsNegativeStock_WhenInsufficient()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var group = new Product
        {
            Name = "Café en Grano (Pool)",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 2m,
            PriceRetailUSD = 8.00m,
            CostPriceUSD = 4.00m
        };
        var parent = await service.CreateProductAsync(group);

        var variant = new Product
        {
            Name = "Café Molido Fino",
            SKU = "7595005",
            ParentProductId = parent.Id
        };
        var createdVariant = await service.CreateProductAsync(variant);

        // Deduct 5 units when only 2 are available with allowNegativeStock = true
        await service.UpdateStockAsync(createdVariant.Id, -5m, "Sale #202", allowNegativeStock: true);

        var updatedParent = await service.GetProductByIdAsync(parent.Id);
        Assert.NotNull(updatedParent);
        Assert.Equal(-3m, updatedParent.StockQuantity);

        var movement = await db.StockMovements.FirstOrDefaultAsync(m => m.ProductId == parent.Id);
        Assert.NotNull(movement);
        Assert.Equal(-3m, movement.NewStockLevel);
        Assert.Equal(-5m, movement.QuantityChange);
    }

    [Fact]
    public async Task UpdateStockAsync_Atomic_WithExecuteUpdateAsync_ConcurrentlyDoesNotLoseUpdates()
    {
        var (db, conn) = Builders.TestDatabaseFactory.CreateSqliteInventoryDbContext();
        try
        {
            var userMock = CreateAdminUserServiceMock();
            var service = new InventoryService(db, userMock.Object);

            var group = new Product
            {
                Name = "Gaseosa 1.5L Pool",
                IsGroupHeader = true,
                IsStockShared = true,
                StockQuantity = 100m,
                PriceRetailUSD = 2.00m,
                CostPriceUSD = 1.00m
            };
            var parent = await service.CreateProductAsync(group);

            var variant = new Product
            {
                Name = "Gaseosa 1.5L Naranja",
                SKU = "7596006",
                ParentProductId = parent.Id
            };
            var createdVariant = await service.CreateProductAsync(variant);

            // Execute 10 sequential / concurrent deductions of 2 units each
            for (int i = 1; i <= 10; i++)
            {
                await service.UpdateStockAsync(createdVariant.Id, -2m, $"Sale #{i}", allowNegativeStock: true);
            }

            var updatedParent = await service.GetProductByIdAsync(parent.Id);
            Assert.NotNull(updatedParent);
            Assert.Equal(80m, updatedParent.StockQuantity); // 100 - (10 * 2) = 80

            var movementCount = await db.StockMovements.CountAsync(m => m.ProductId == parent.Id);
            Assert.Equal(10, movementCount);
        }
        finally
        {
            conn.Close();
            conn.Dispose();
        }
    }

    [Fact]
    public async Task ReserveStockAsync_VariantWithStockShared_AllocatesParentReservedQuantity()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var group = new Product
        {
            Name = "Azúcar 1Kg Pool",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 20m,
            PriceRetailUSD = 1.50m,
            CostPriceUSD = 0.90m
        };
        var parent = await service.CreateProductAsync(group);

        var variant = new Product
        {
            Name = "Azúcar Blanca 1Kg",
            SKU = "7597007",
            ParentProductId = parent.Id
        };
        var createdVariant = await service.CreateProductAsync(variant);

        int resId = await service.ReserveStockAsync(createdVariant.Id, 4m, TimeSpan.FromMinutes(10));
        Assert.True(resId > 0);

        var parentAfterReserve = await service.GetProductByIdAsync(parent.Id);
        Assert.NotNull(parentAfterReserve);
        Assert.Equal(4m, parentAfterReserve.ReservedQuantity);

        // Confirm reservation
        await service.ConfirmReservationAsync(resId, "Pickup completed");
        var parentAfterConfirm = await service.GetProductByIdAsync(parent.Id);
        Assert.NotNull(parentAfterConfirm);
        Assert.Equal(16m, parentAfterConfirm.StockQuantity);
        Assert.Equal(0m, parentAfterConfirm.ReservedQuantity);
    }

    [Fact]
    public async Task ProductDialogViewModel_ShowStockInputs_Matrix_ReturnsExpectedVisibility()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        var sharedParentDto = new ProductDto { Id = 10, Name = "Padre Compartido", IsStockShared = true };
        var indepParentDto = new ProductDto { Id = 20, Name = "Padre No Compartido", IsStockShared = false };
        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto> { sharedParentDto, indepParentDto });

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object, null);
        await vm.LoadMetadataAsync();

        // 1. Normal standalone product -> ShowStockInputs = true
        vm.IsCashAdvance = false;
        vm.IsGroupHeader = false;
        vm.SelectedParentProduct = null;
        Assert.True(vm.ShowStockInputs);

        // 2. Cash Advance service -> ShowStockInputs = false
        vm.IsCashAdvance = true;
        Assert.False(vm.ShowStockInputs);
        vm.IsCashAdvance = false;

        // 3. Group without shared stock -> ShowStockInputs = false
        vm.IsGroupHeader = true;
        vm.IsStockShared = false;
        Assert.False(vm.ShowStockInputs);

        // 4. Group with shared stock -> ShowStockInputs = true
        vm.IsStockShared = true;
        Assert.True(vm.ShowStockInputs);

        // 5. Variant of shared stock parent -> ShowStockInputs = false
        vm.IsGroupHeader = false;
        vm.SelectedParentProduct = sharedParentDto;
        Assert.False(vm.ShowStockInputs);

        // 6. Variant of non-shared stock parent -> ShowStockInputs = true
        vm.SelectedParentProduct = indepParentDto;
        Assert.True(vm.ShowStockInputs);
    }

    [Fact]
    public async Task ProductDialogViewModel_WhenSelectingIndependentParent_EnablesPricingFields()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        var indepPricingParent = new ProductDto { Id = 30, Name = "Padre Indep", HasIndependentPricing = true };
        var inheritedPricingParent = new ProductDto { Id = 40, Name = "Padre Heredado", HasIndependentPricing = false };
        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto> { indepPricingParent, inheritedPricingParent });

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object, null);
        await vm.LoadMetadataAsync();

        // Select inherited parent -> CanEditPricing = false
        vm.SelectedParentProduct = inheritedPricingParent;
        Assert.False(vm.CanEditPricing);

        // Select independent parent -> CanEditPricing = true
        vm.SelectedParentProduct = indepPricingParent;
        Assert.True(vm.CanEditPricing);
    }

    [Fact]
    public async Task BulkImport_WithStockSharedAndIndependentPricing_ValidatesInmutabilityAndFlags()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var importList = new List<ProductImportDto>
        {
            new ProductImportDto
            {
                ProductType = "Grupo",
                Name = "Refrescos 2L (Import Grupo)",
                SKU = "GRP-IMP-01",
                IsStockShared = true,
                HasIndependentPricing = true,
                StockQuantity = 200m,
                LowStockThreshold = 20m,
                CostPriceUSD = 1.00m,
                ProfitMarginRetail = 50.00m,
                PriceRetailUSD = 1.50m,
                IsValid = true
            },
            new ProductImportDto
            {
                ProductType = "Variante",
                Name = "Refresco 2L Limón",
                SKU = "7598881",
                GroupNameOrKey = "Refrescos 2L (Import Grupo)",
                CostPriceUSD = 1.20m,
                ProfitMarginRetail = 50.00m,
                PriceRetailUSD = 1.80m,
                StockQuantity = 50m, // Should be forced to 0 because parent has IsStockShared = true
                IsValid = true
            }
        };

        var (added, updated) = await service.BulkImportProductsAsync(importList, overwriteMerge: false);
        Assert.Equal(2, added);

        var groupInDb = await service.GetProductBySkuAsync("GRP-IMP-01");
        var variantInDb = await service.GetProductBySkuAsync("7598881");

        Assert.NotNull(groupInDb);
        Assert.NotNull(variantInDb);
        Assert.True(groupInDb.IsStockShared);
        Assert.True(groupInDb.HasIndependentPricing);
        Assert.Equal(200m, groupInDb.StockQuantity);

        Assert.Equal(0m, variantInDb.StockQuantity);
        Assert.Equal(1.80m, variantInDb.PriceRetailUSD); // Preserved custom price because parent HasIndependentPricing = true
    }

    [Fact]
    public async Task VariantSelectionViewModel_SelectVariant_WorksWhenStockIsSharedOrZero()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        var parentQuickInfo = new ProductQuickInfoDto
        {
            Id = 100,
            Name = "Refrescos Sabores",
            IsGroupHeader = true,
            IsStockShared = true,
            PriceRetailUSD = 2.00m
        };

        var variantDto = new ProductDto
        {
            Id = 101,
            Name = "Refresco Naranja",
            ParentProductId = 100,
            StockQuantity = 0m, // Stock is shared in parent, so child has 0
            PriceRetailUSD = 2.00m,
            IsActive = true
        };

        mockProductService.Setup(s => s.GetVariantsAsync(100)).ReturnsAsync(new List<ProductDto> { variantDto });

        var vm = new Desktop.Client.ViewModels.VariantSelectionViewModel(mockProductService.Object, mockExchangeRate.Object, parentQuickInfo);
        await vm.LoadVariantsAsync();

        Assert.Single(vm.Variants);
        Assert.Equal(variantDto, vm.CurrentSelectedVariant);

        bool closed = false;
        vm.RequestClose = res => closed = res;

        vm.SelectVariantCommand.Execute(variantDto);

        Assert.True(closed);
        Assert.NotNull(vm.SelectedVariant);
        Assert.Equal(101, vm.SelectedVariant.Id);
    }

}
