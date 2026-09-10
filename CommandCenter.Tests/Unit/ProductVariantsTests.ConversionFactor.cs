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
    public async Task ProductDialogViewModel_SaveAsync_ForcesSkuVerification_ForNonGroups()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);
        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(new List<ProductDto>());
        
        // Setup SKU 77777 as already existing
        mockProductService.Setup(s => s.GetQuickInfoAsync("77777")).ReturnsAsync(new Core.DTOs.ProductQuickInfoDto { Id = 999, SKU = "77777" });

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object, null);
        await vm.LoadMetadataAsync();

        vm.Name = "Producto Duplicado";
        vm.Sku = "77777";
        vm.CostPriceUSD = 1.00m;
        vm.PriceRetailUSD = 2.00m;

        bool closed = false;
        vm.RequestClose = res => closed = res;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.False(vm.IsSkuValid);
        Assert.Contains("already exists", vm.SkuVerificationMessage, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public async Task CreateGroup_WithStockShared_AllowsInitialStockOnParent()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var group = new Product
        {
            Name = "Camisetas Deportivas (Grupo)",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 100m,
            LowStockThreshold = 10m,
            PriceRetailUSD = 15.00m,
            CostPriceUSD = 8.00m
        };

        var created = await service.CreateProductAsync(group);
        Assert.True(created.IsGroupHeader);
        Assert.True(created.IsStockShared);
        Assert.Equal(100m, created.StockQuantity);
        Assert.Equal(10m, created.LowStockThreshold);
    }

    [Fact]
    public async Task CreateGroup_WithoutStockShared_ForcesParentStockToZero()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var group = new Product
        {
            Name = "Zapatos Casuales (Grupo)",
            IsGroupHeader = true,
            IsStockShared = false,
            StockQuantity = 50m,
            LowStockThreshold = 5m,
            PriceRetailUSD = 30.00m,
            CostPriceUSD = 15.00m
        };

        var created = await service.CreateProductAsync(group);
        Assert.True(created.IsGroupHeader);
        Assert.False(created.IsStockShared);
        Assert.Equal(0m, created.StockQuantity);
        Assert.Equal(0m, created.LowStockThreshold);
    }

    [Fact]
    public async Task UpdateGroup_AttemptingToChangeStockShared_ThrowsInvalidOperationException()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var group = new Product
        {
            Name = "Pinturas (Grupo)",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 40m,
            PriceRetailUSD = 12.00m,
            CostPriceUSD = 6.00m
        };
        var created = await service.CreateProductAsync(group);

        created.IsStockShared = false; // Attempt to mutate immutable flag

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateProductAsync(created));
        Assert.Contains("Stock Compartido", ex.Message);
    }

    [Fact]
    public async Task UpdateGroup_AttemptingToChangeIndependentPricing_ThrowsInvalidOperationException()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var group = new Product
        {
            Name = "Pinturas (Grupo 2)",
            IsGroupHeader = true,
            HasIndependentPricing = false,
            PriceRetailUSD = 12.00m,
            CostPriceUSD = 6.00m
        };
        var created = await service.CreateProductAsync(group);

        created.HasIndependentPricing = true; // Attempt to mutate immutable flag

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateProductAsync(created));
        Assert.Contains("Precios Independientes", ex.Message);
    }

    [Fact]
    public async Task CreateChildVariant_UnderStockSharedParent_ForcesChildStockToZero()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var group = new Product
        {
            Name = "Harina Pan Pool",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 500m,
            PriceRetailUSD = 1.20m,
            CostPriceUSD = 0.80m
        };
        var parent = await service.CreateProductAsync(group);

        var child = new Product
        {
            Name = "Harina Pan Tradicional",
            SKU = "7591001",
            ParentProductId = parent.Id,
            StockQuantity = 100m // Should be forced to 0
        };
        var createdChild = await service.CreateProductAsync(child);

        Assert.Equal(0m, createdChild.StockQuantity);
        Assert.Equal(0m, createdChild.LowStockThreshold);
    }

    [Fact]
    public async Task CreateChildVariant_UnderIndependentPricingParent_PreservesCustomPrices()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var group = new Product
        {
            Name = "Ropa Colección",
            IsGroupHeader = true,
            HasIndependentPricing = true,
            PriceRetailUSD = 20.00m,
            CostPriceUSD = 10.00m
        };
        var parent = await service.CreateProductAsync(group);

        var child = new Product
        {
            Name = "Ropa Talla XL (Edición Especial)",
            SKU = "7592002",
            ParentProductId = parent.Id,
            CostPriceUSD = 14.00m,
            ProfitMarginRetail = 50.00m,
            PriceRetailUSD = 21.00m
        };
        var createdChild = await service.CreateProductAsync(child);

        Assert.Equal(14.00m, createdChild.CostPriceUSD);
        Assert.Equal(21.00m, createdChild.PriceRetailUSD);
    }

    [Fact]
    public async Task UpdateParentPrices_WhenIndependentPricing_DoesNotOverwriteVariantPrices()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var group = new Product
        {
            Name = "Helados Artesanales",
            IsGroupHeader = true,
            HasIndependentPricing = true,
            PriceRetailUSD = 3.00m,
            CostPriceUSD = 1.50m
        };
        var parent = await service.CreateProductAsync(group);

        var variant = new Product
        {
            Name = "Helado Pistacho Premium",
            SKU = "7593003",
            ParentProductId = parent.Id,
            CostPriceUSD = 2.50m,
            ProfitMarginRetail = 60.00m,
            PriceRetailUSD = 4.00m
        };
        var createdVariant = await service.CreateProductAsync(variant);

        // Update parent price to 5.00
        parent.PriceRetailUSD = 5.00m;
        parent.CostPriceUSD = 2.50m;
        await service.UpdateProductAsync(parent);

        var fetchedVariant = await service.GetProductByIdAsync(createdVariant.Id);
        Assert.NotNull(fetchedVariant);
        Assert.Equal(4.00m, fetchedVariant.PriceRetailUSD); // Maintained independent price
    }

    [Fact]
    public async Task UpdateStockAsync_VariantWithStockShared_DeductsFromParentStock_AndLogsMovement()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var group = new Product
        {
            Name = "Pintura Galón (Pool)",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 50m,
            PriceRetailUSD = 25.00m,
            CostPriceUSD = 15.00m
        };
        var parent = await service.CreateProductAsync(group);

        var variant = new Product
        {
            Name = "Pintura Galón Blanco",
            SKU = "7594004",
            ParentProductId = parent.Id
        };
        var createdVariant = await service.CreateProductAsync(variant);

        // Deduct 5 units from variant
        await service.UpdateStockAsync(createdVariant.Id, -5m, "Venta #101");

        var updatedParent = await service.GetProductByIdAsync(parent.Id);
        var updatedVariant = await service.GetProductByIdAsync(createdVariant.Id);

        Assert.NotNull(updatedParent);
        Assert.NotNull(updatedVariant);
        Assert.Equal(45m, updatedParent.StockQuantity);
        Assert.Equal(0m, updatedVariant.StockQuantity);

        var movements = await db.StockMovements.Where(m => m.ProductId == parent.Id).ToListAsync();
        Assert.Single(movements);
        Assert.Contains("Pintura Galón Blanco", movements[0].Reason);
        Assert.Equal(-5m, movements[0].QuantityChange);
        Assert.Equal(45m, movements[0].NewStockLevel);
    }

}
