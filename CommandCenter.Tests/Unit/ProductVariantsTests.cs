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

public class ProductVariantsTests
{
    private InventoryDbContext CreateInMemoryInventoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new InventoryDbContext(options);
    }

    private SalesDbContext CreateInMemorySalesDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new SalesDbContext(options);
    }

    private Mock<ICurrentUserService> CreateAdminUserServiceMock()
    {
        var mock = new Mock<ICurrentUserService>();
        mock.Setup(u => u.CanMutateCatalog).Returns(true);
        mock.Setup(u => u.UserId).Returns("admin-1");
        return mock;
    }

    [Fact]
    public async Task CreateParentProduct_AutoGeneratesGroupSku_WhenEmpty()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Name = "Refresco 2L Sabores",
            IsGroupHeader = true,
            PriceRetailUSD = 2.50m,
            CostPriceUSD = 1.50m,
            ProfitMarginRetail = 66.67m,
            StockQuantity = 0m
        };

        var created = await service.CreateProductAsync(parent);

        Assert.NotNull(created);
        Assert.True(created.IsGroupHeader);
        Assert.StartsWith("GRP-", created.SKU);
        Assert.Equal(0m, created.StockQuantity);
    }

    [Fact]
    public async Task CreateVariant_InheritsPricesAndMargins_FromParent()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Name = "Refresco 2L Sabores",
            IsGroupHeader = true,
            PriceRetailUSD = 2.50m,
            CostPriceUSD = 1.50m,
            ProfitMarginRetail = 66.67m,
            HasWholesale = true,
            PriceWholesaleUSD = 2.00m,
            MinWholesaleQuantity = 6.000m,
            StockQuantity = 0m
        };
        var savedParent = await service.CreateProductAsync(parent);

        var variant = new Product
        {
            Name = "Refresco 2L Sabor Fresa",
            SKU = "7591234567890",
            ParentProductId = savedParent.Id,
            StockQuantity = 24m,
            LowStockThreshold = 5m
        };

        var savedVariant = await service.CreateProductAsync(variant);

        Assert.NotNull(savedVariant);
        Assert.Equal(savedParent.Id, savedVariant.ParentProductId);
        Assert.Equal(2.50m, savedVariant.PriceRetailUSD);
        Assert.Equal(1.50m, savedVariant.CostPriceUSD);
        Assert.Equal(66.67m, savedVariant.ProfitMarginRetail);
        Assert.True(savedVariant.HasWholesale);
        Assert.Equal(2.00m, savedVariant.PriceWholesaleUSD);
        Assert.Equal(24m, savedVariant.StockQuantity);
    }

    [Fact]
    public async Task DeleteParent_WithActiveVariants_ThrowsInvalidOperationException()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = await service.CreateProductAsync(new Product
        {
            Name = "Jugo 1L Frutas",
            IsGroupHeader = true,
            PriceRetailUSD = 1.20m,
            CostPriceUSD = 0.80m,
            StockQuantity = 0m
        });

        await service.CreateProductAsync(new Product
        {
            Name = "Jugo 1L Naranja",
            SKU = "7591112223334",
            ParentProductId = parent.Id,
            StockQuantity = 10m
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteProductAsync(parent.Id));
        Assert.Contains("variantes asociadas", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSuggestions_GroupsVariantsUnderParent_AndCalculatesConsolidatedStock()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = await service.CreateProductAsync(new Product
        {
            Name = "Galletas Rellenas 100g",
            IsGroupHeader = true,
            PriceRetailUSD = 1.00m,
            CostPriceUSD = 0.60m,
            StockQuantity = 0m
        });

        await service.CreateProductAsync(new Product
        {
            Name = "Galletas Rellenas Fresa 100g",
            SKU = "7591001",
            ParentProductId = parent.Id,
            StockQuantity = 15m
        });

        await service.CreateProductAsync(new Product
        {
            Name = "Galletas Rellenas Chocolate 100g",
            SKU = "7591002",
            ParentProductId = parent.Id,
            StockQuantity = 25m
        });

        // Search by text matching parent/variants
        var suggestions = await service.GetSuggestionsAsync("Galletas", activeOnly: true, CancellationToken.None);

        Assert.Single(suggestions);
        var groupSuggestion = suggestions[0];
        Assert.True(groupSuggestion.IsGroupHeader);
        Assert.Equal("Galletas Rellenas 100g", groupSuggestion.Name);
        Assert.Equal(2, groupSuggestion.VariantCount);
        Assert.Equal(40m, groupSuggestion.ConsolidatedStock);
    }

    [Fact]
    public async Task GetSuggestions_ExactSkuScanOfVariant_ReturnsVariantDirectly()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = await service.CreateProductAsync(new Product
        {
            Name = "Galletas Rellenas 100g",
            IsGroupHeader = true,
            PriceRetailUSD = 1.00m,
            CostPriceUSD = 0.60m,
            StockQuantity = 0m
        });

        var variant = await service.CreateProductAsync(new Product
        {
            Name = "Galletas Rellenas Fresa 100g",
            SKU = "7591001",
            ParentProductId = parent.Id,
            StockQuantity = 15m
        });

        // Exact scan of variant SKU
        var suggestions = await service.GetSuggestionsAsync("7591001", activeOnly: true, CancellationToken.None);

        Assert.Single(suggestions);
        var directVariant = suggestions[0];
        Assert.False(directVariant.IsGroupHeader);
        Assert.Equal(variant.Id, directVariant.Id);
        Assert.Equal("Galletas Rellenas Fresa 100g", directVariant.Name);
        Assert.Equal("7591001", directVariant.SKU);
    }

    [Fact]
    public async Task GetProductsPaged_CalculatesVariantCountAndConsolidatedStock()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = await service.CreateProductAsync(new Product
        {
            Name = "Cereal Flakes 500g",
            IsGroupHeader = true,
            PriceRetailUSD = 3.50m,
            CostPriceUSD = 2.00m,
            StockQuantity = 0m
        });

        await service.CreateProductAsync(new Product
        {
            Name = "Cereal Flakes Miel 500g",
            SKU = "7593001",
            ParentProductId = parent.Id,
            StockQuantity = 8m
        });

        await service.CreateProductAsync(new Product
        {
            Name = "Cereal Flakes Chocolate 500g",
            SKU = "7593002",
            ParentProductId = parent.Id,
            StockQuantity = 12m
        });

        var paged = await service.GetProductsPagedAsync(filter: "Cereal", page: 1, pageSize: 10);

        Assert.NotEmpty(paged.Items);
        var parentDto = paged.Items.FirstOrDefault(p => p.Id == parent.Id);
        Assert.NotNull(parentDto);
        Assert.True(parentDto.IsGroupHeader);
        Assert.Equal(2, parentDto.VariantCount);
        Assert.Equal(20m, parentDto.ConsolidatedStock);
    }

    [Fact]
    public async Task GetVariantOptionsAsync_ReturnsActiveVariantsOfParent()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = await service.CreateProductAsync(new Product
        {
            Name = "Detergente 1L",
            IsGroupHeader = true,
            PriceRetailUSD = 2.00m,
            StockQuantity = 0m
        });

        await service.CreateProductAsync(new Product
        {
            Name = "Detergente Lavanda 1L",
            SKU = "7594001",
            ParentProductId = parent.Id,
            StockQuantity = 10m
        });

        await service.CreateProductAsync(new Product
        {
            Name = "Detergente Limón 1L",
            SKU = "7594002",
            ParentProductId = parent.Id,
            StockQuantity = 5m
        });

        var variants = await service.GetVariantOptionsAsync(parent.Id);

        Assert.Equal(2, variants.Count);
        Assert.All(variants, v => Assert.Equal(parent.Id, v.ParentProductId));
    }

    [Fact]
    public async Task SalesService_AddItem_ThrowsWhenAddingGroupHeaderDirectly()
    {
        var invDb = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var salesDb = CreateInMemorySalesDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var invService = new InventoryService(invDb, userMock.Object);

        var parent = await invService.CreateProductAsync(new Product
        {
            Name = "Bebida Energética 500ml",
            IsGroupHeader = true,
            PriceRetailUSD = 1.80m,
            StockQuantity = 0m
        });

        var salesService = new SalesService(salesDb, invService, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());
        var sale = new Sale { Status = SaleStatus.Pending, AppliedRate = 40.00m };
        salesDb.Sales.Add(sale);
        await salesDb.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            salesService.AddItemAsync(sale.Id, parent.Id, 1, 40.00m));

        Assert.Contains("grupo de variantes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SalesService_AddItem_SucceedsWhenAddingVariant()
    {
        var invDb = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var salesDb = CreateInMemorySalesDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var invService = new InventoryService(invDb, userMock.Object);

        var parent = await invService.CreateProductAsync(new Product
        {
            Name = "Bebida Energética 500ml",
            IsGroupHeader = true,
            PriceRetailUSD = 1.80m,
            StockQuantity = 0m
        });

        var variant = await invService.CreateProductAsync(new Product
        {
            Name = "Bebida Energética Manzana 500ml",
            SKU = "7595001",
            ParentProductId = parent.Id,
            StockQuantity = 10m
        });

        var salesService = new SalesService(salesDb, invService, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());
        var sale = new Sale { Status = SaleStatus.Pending, AppliedRate = 40.00m };
        salesDb.Sales.Add(sale);
        await salesDb.SaveChangesAsync();

        var updatedSale = await salesService.AddItemAsync(sale.Id, variant.Id, 2, 40.00m);

        Assert.NotNull(updatedSale);
        Assert.Single(updatedSale.Items);
        var item = updatedSale.Items[0];
        Assert.Equal(variant.Id, item.ProductId);
        Assert.Equal(2m, item.Quantity);
        Assert.Equal(1.80m, item.UnitPrice);
        Assert.Equal(3.60m, item.Subtotal);
    }

    [Fact]
    public async Task BulkImportProducts_ImportsGroupsAndLinksVariantsByGroupNameOrKey()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var importDtos = new List<ProductImportDto>
        {
            new ProductImportDto
            {
                SKU = "GRP-YOGURT",
                Name = "Yogurt Líquido 1L",
                ProductType = "Grupo",
                GroupNameOrKey = "YOG-1L",
                PriceRetailUSD = 2.00m,
                CostPriceUSD = 1.00m,
                ProfitMarginRetail = 100m,
                UnitOfMeasure = "Und",
                IsValid = true
            },
            new ProductImportDto
            {
                SKU = "759888001",
                Name = "Yogurt Fresa 1L",
                ProductType = "Variante",
                GroupNameOrKey = "YOG-1L",
                StockQuantity = 15m,
                LowStockThreshold = 3m,
                IsValid = true
            },
            new ProductImportDto
            {
                SKU = "759888002",
                Name = "Yogurt Durazno 1L",
                ProductType = "Variante",
                GroupNameOrKey = "YOG-1L",
                StockQuantity = 20m,
                LowStockThreshold = 4m,
                IsValid = true
            }
        };

        var result = await service.BulkImportProductsAsync(importDtos, overwriteMerge: true);

        Assert.Equal(3, result.added);

        var groupProduct = await db.Products.FirstOrDefaultAsync(p => p.SKU == "GRP-YOGURT");
        Assert.NotNull(groupProduct);
        Assert.True(groupProduct.IsGroupHeader);

        var variants = await db.Products.Where(p => p.ParentProductId == groupProduct.Id).ToListAsync();
        Assert.Equal(2, variants.Count);
        Assert.All(variants, v =>
        {
            Assert.Equal(2.00m, v.PriceRetailUSD);
            Assert.Equal(1.00m, v.CostPriceUSD);
        });
    }

    [Fact]
    public async Task ProductDialogViewModel_LoadsParentProducts_AndSelectsExistingParent()
    {
        var mockProductService = new Mock<Desktop.Client.Services.IProductService>();
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        var parents = new List<ProductDto>
        {
            new ProductDto
            {
                Id = 10,
                Name = "Jugo 1L Frutas",
                SKU = "GRP-JUGO",
                CostPriceUSD = 1.00m,
                ProfitMarginRetail = 50m,
                PriceRetailUSD = 1.50m,
                IsGroupHeader = true
            },
            new ProductDto
            {
                Id = 20,
                Name = "Yogurt 1L Sabores",
                SKU = "GRP-YOG",
                CostPriceUSD = 2.00m,
                ProfitMarginRetail = 40m,
                PriceRetailUSD = 2.80m,
                IsGroupHeader = true
            }
        };

        mockProductService.Setup(s => s.GetParentsAsync()).ReturnsAsync(parents);

        var existingVariant = new Product
        {
            Id = 105,
            Name = "Jugo 1L Naranja",
            SKU = "7591001",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 50m,
            PriceRetailUSD = 1.50m,
            ParentProductId = 10
        };

        var vm = new Desktop.Client.ViewModels.ProductDialogViewModel(mockProductService.Object, mockExchangeRate.Object, existingVariant);
        await vm.LoadMetadataAsync();

        Assert.Equal(3, vm.ParentProducts.Count); // Ninguno (0) + 2 parents
        Assert.Equal(0, vm.ParentProducts[0].Id);
        Assert.Equal("Ninguno (Producto Independiente)", vm.ParentProducts[0].Name);

        Assert.NotNull(vm.SelectedParentProduct);
        Assert.Equal(10, vm.SelectedParentProduct.Id);
        Assert.True(vm.IsVariant);
        Assert.True(vm.IsInheritingPricing);
        Assert.False(vm.CanEditPricing);
    }

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

    #region Shared Stock and Independent Pricing Extended Tests

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

    [Fact]
    public async Task UpdateStockAsync_VariantWithConversionFactor_DeductsMultipliedParentStock()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        // Padre "Caja de Huevos" con 360 unidades de stock base
        var group = new Product
        {
            Name = "Caja de Huevos 360",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 360m,
            PriceRetailUSD = 40.00m,
            CostPriceUSD = 30.00m
        };
        var parent = await service.CreateProductAsync(group);

        // Variante "Cartón" con ConversionFactor = 30 (1 cartón = 30 huevos)
        var cartonVariant = new Product
        {
            Name = "Cartón de Huevos (30 Und)",
            SKU = "759888801",
            ParentProductId = parent.Id,
            ConversionFactor = 30.0m
        };
        var createdVariant = await service.CreateProductAsync(cartonVariant);
        Assert.Equal(30.0m, createdVariant.ConversionFactor);

        // Venta de 2 cartones (-2) -> Debe descontar 2 * 30 = 60 huevos del padre
        await service.UpdateStockAsync(createdVariant.Id, -2m, "Venta de 2 cartones", userId: "1", allowNegativeStock: false);

        var parentInDb = await service.GetProductByIdAsync(parent.Id);
        Assert.NotNull(parentInDb);
        Assert.Equal(300m, parentInDb.StockQuantity);

        var movements = await db.StockMovements.Where(m => m.ProductId == parent.Id).ToListAsync();
        Assert.Single(movements);
        Assert.Equal(-60m, movements[0].QuantityChange);
        Assert.Equal(300m, movements[0].NewStockLevel);
        Assert.Contains("Cartón de Huevos", movements[0].Reason);
        Assert.Contains("Factor: 30", movements[0].Reason);
    }

    [Fact]
    public async Task UpdateStockAsync_VariantWithConversionFactor_FractionalQuantity_DeductsCorrectly()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = await service.CreateProductAsync(new Product
        {
            Name = "Queso Duro Pool (Gramos)",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 5000m // 5000 gramos base
        });

        // Variante "Cuarto de Kilo" -> Factor = 250 gramos
        var variant = await service.CreateProductAsync(new Product
        {
            Name = "Cuarto de Kilo Queso",
            SKU = "759999901",
            ParentProductId = parent.Id,
            ConversionFactor = 250.0m
        });

        // Vender 0.5 unidades de la variante -> 0.5 * 250 = 125 gramos
        await service.UpdateStockAsync(variant.Id, -0.5m, "Venta 0.5 paquete", userId: "1", allowNegativeStock: false);

        var parentInDb = await service.GetProductByIdAsync(parent.Id);
        Assert.NotNull(parentInDb);
        Assert.Equal(4875m, parentInDb.StockQuantity);
    }

    [Fact]
    public async Task ReserveStockAsync_VariantWithConversionFactor_ReservesMultipliedQuantity_AndSetsSourceProductId()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = await service.CreateProductAsync(new Product
        {
            Name = "Caja de Huevos 360",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 360m
        });

        var carton = await service.CreateProductAsync(new Product
        {
            Name = "Cartón de Huevos (30 Und)",
            SKU = "759888802",
            ParentProductId = parent.Id,
            ConversionFactor = 30.0m
        });

        // Reservar 3 cartones -> 3 * 30 = 90 huevos base
        int resId = await service.ReserveStockAsync(carton.Id, 3m, TimeSpan.FromMinutes(15));
        Assert.True(resId > 0);

        var parentInDb = await service.GetProductByIdAsync(parent.Id);
        Assert.NotNull(parentInDb);
        Assert.Equal(90m, parentInDb.ReservedQuantity);

        var reservation = await db.StockReservations.FindAsync(resId);
        Assert.NotNull(reservation);
        Assert.Equal(parent.Id, reservation.ProductId);
        Assert.Equal(carton.Id, reservation.SourceProductId);
        Assert.Equal(90m, reservation.Quantity);

        // Cancelar reserva -> Desaloja los 90 del padre
        await service.CancelReservationAsync(resId);
        var parentAfterCancel = await service.GetProductByIdAsync(parent.Id);
        Assert.NotNull(parentAfterCancel);
        Assert.Equal(0m, parentAfterCancel.ReservedQuantity);
    }

    [Fact]
    public async Task CreateProduct_NonSharedParentOrGroupHeader_ForcesConversionFactorToOne()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        // 1. Grupo con factor != 1 -> Debe forzarse a 1.0m
        var group = await service.CreateProductAsync(new Product
        {
            Name = "Grupo Test",
            IsGroupHeader = true,
            IsStockShared = true,
            ConversionFactor = 50.0m
        });
        Assert.Equal(1.0000m, group.ConversionFactor);

        // 2. Variante de padre con IsStockShared = false -> Debe forzarse a 1.0m
        var parentIndividual = await service.CreateProductAsync(new Product
        {
            Name = "Padre No Compartido",
            IsGroupHeader = true,
            IsStockShared = false
        });

        var variantIndep = await service.CreateProductAsync(new Product
        {
            Name = "Variante Individual",
            SKU = "759111222",
            ParentProductId = parentIndividual.Id,
            ConversionFactor = 15.0m
        });
        Assert.Equal(1.0000m, variantIndep.ConversionFactor);
    }

    [Fact]
    public async Task UpdateProductAsync_WhenFactorOmitted_PreservesExistingConversionFactor()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = await service.CreateProductAsync(new Product
        {
            Name = "Padre Pool",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 100m
        });

        var variant = await service.CreateProductAsync(new Product
        {
            Name = "Variante 20x",
            SKU = "759333444",
            ParentProductId = parent.Id,
            ConversionFactor = 20.0m
        });

        // Actualizar cambiando el nombre pero enviando ConversionFactor = 0 (omisión en payload parcial)
        variant.Name = "Variante 20x Modificada";
        variant.ConversionFactor = 0m;

        await service.UpdateProductAsync(variant);

        var updated = await service.GetProductByIdAsync(variant.Id);
        Assert.NotNull(updated);
        Assert.Equal("Variante 20x Modificada", updated.Name);
        Assert.Equal(20.0m, updated.ConversionFactor); // Preservado
    }

    [Fact]
    public async Task UpdateStockAsync_ConcurrentVariantDeductionsWithConversionFactor_Relational_DoesNotLoseUpdates()
    {
        var dbName = $"mem_test_{Guid.NewGuid():N}";
        var connString = $"Data Source=file:{dbName}?mode=memory&cache=shared";

        using var masterConn = new Microsoft.Data.Sqlite.SqliteConnection(connString);
        masterConn.Open();

        var masterOptions = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlite(masterConn)
            .Options;

        int parentId;
        int variantId;

        using (var initDb = new InventoryDbContext(masterOptions))
        {
            initDb.Database.EnsureCreated();

            var parent = new Product
            {
                Name = "Pool Harina Concentrada",
                IsGroupHeader = true,
                IsStockShared = true,
                StockQuantity = 1000m,
                PriceRetailUSD = 1.00m,
                CostPriceUSD = 0.50m
            };
            initDb.Products.Add(parent);
            await initDb.SaveChangesAsync();

            var variant = new Product
            {
                Name = "Bolsa 5Kg",
                SKU = "759555666",
                ParentProductId = parent.Id,
                ConversionFactor = 5.0m,
                IsActive = true
            };
            initDb.Products.Add(variant);
            await initDb.SaveChangesAsync();

            parentId = parent.Id;
            variantId = variant.Id;
        }

        var userMock = CreateAdminUserServiceMock();

        // 10 deducciones concurrentes de 2 unidades cada una (2 bolsas * 5 factor = 10 unidades base por tarea)
        // Total a descontar: 10 * 10 = 100 unidades base.
        int taskCount = 10;
        var tasks = new List<Task>();

        for (int i = 0; i < taskCount; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                using var taskConn = new Microsoft.Data.Sqlite.SqliteConnection(connString);
                taskConn.Open();
                var options = new DbContextOptionsBuilder<InventoryDbContext>()
                    .UseSqlite(taskConn)
                    .Options;
                using var taskContext = new InventoryDbContext(options);
                var taskService = new InventoryService(taskContext, userMock.Object);

                await taskService.UpdateStockAsync(variantId, -2m, "Venta concurrente de bolsa", allowNegativeStock: false);
            }));
        }

        await Task.WhenAll(tasks);

        using (var verifyDb = new InventoryDbContext(masterOptions))
        {
            var parentInDb = await verifyDb.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parentId);
            Assert.NotNull(parentInDb);
            Assert.Equal(900m, parentInDb.StockQuantity); // 1000 - 100 = 900
        }
    }

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

    [Fact]
    public async Task AdjustStockAsync_VariantProduct_UnderIndividualParent_Succeeds()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Name = "Zapato Deportivo",
            IsGroupHeader = true,
            IsStockShared = false
        };
        var savedParent = await service.CreateProductAsync(parent);

        var variant = new Product
        {
            Name = "Zapato Talla 42",
            SKU = "7597778889991",
            ParentProductId = savedParent.Id,
            StockQuantity = 10m
        };
        var savedVariant = await service.CreateProductAsync(variant);

        await service.AdjustStockAsync(savedVariant.Id, 5m, "Entrada de lote");

        var updated = await db.Products.FindAsync(savedVariant.Id);
        Assert.NotNull(updated);
        Assert.Equal(15m, updated.StockQuantity);
    }

    [Fact]
    public async Task AdjustStockAsync_StandaloneProduct_Succeeds()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var prod = new Product
        {
            Name = "Arroz 1Kg",
            SKU = "7591234567890",
            StockQuantity = 50m
        };
        var saved = await service.CreateProductAsync(prod);

        await service.AdjustStockAsync(saved.Id, -5m, "Mermas por empaque dañado");

        var updated = await db.Products.FindAsync(saved.Id);
        Assert.NotNull(updated);
        Assert.Equal(45m, updated.StockQuantity);
    }

    [Fact]
    public async Task AdjustStockAsync_CashAdvanceService_ThrowsInvalidOperationException()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var serviceProd = new Product
        {
            Name = "Adelanto de Efectivo",
            SKU = "999999",
            IsCashAdvance = true
        };
        var saved = await service.CreateProductAsync(serviceProd);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AdjustStockAsync(saved.Id, 100m, "Ajuste a servicio"));

        Assert.Contains("servicio", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdjustStockAsync_DeletedProduct_ThrowsInvalidOperationException()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var prod = new Product
        {
            Name = "Producto Descontinuado",
            SKU = "7593334445556",
            StockQuantity = 20m,
            IsDeleted = true
        };
        var saved = await service.CreateProductAsync(prod);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AdjustStockAsync(saved.Id, 10m, "Ajuste en archivado"));

        Assert.Equal(Core.Constants.InventoryMessages.DeletedProductStockAdjustmentBlocked, ex.Message);
    }

    [Fact]
    public async Task CreateProduct_ConversionFactorOutOfRange_ThrowsInvalidOperationException()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        var parent = new Product
        {
            Name = "Huevos Pool",
            IsGroupHeader = true,
            IsStockShared = true
        };
        var savedParent = await service.CreateProductAsync(parent);

        var variant = new Product
        {
            Name = "Variante Excesiva",
            SKU = "7599991112223",
            ParentProductId = savedParent.Id,
            ConversionFactor = 2_000_000m // Mayor a 1,000,000
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateProductAsync(variant));

        Assert.Equal(Core.Constants.InventoryMessages.ConversionFactorOutOfRange, ex.Message);
    }

    [Fact]
    public async Task UpdateStockAsync_HighConcurrency_SharedStockWithConversionFactor_ConsistenceCheck()
    {
        string dbName = Guid.NewGuid().ToString();
        var setupDb = CreateInMemoryInventoryDb(dbName);
        var userMock = CreateAdminUserServiceMock();
        var setupService = new InventoryService(setupDb, userMock.Object);

        // Padre con stock inicial de 1000 unidades base
        var parent = new Product
        {
            Name = "Harina Trigo Saco",
            IsGroupHeader = true,
            IsStockShared = true,
            StockQuantity = 1000m
        };
        var savedParent = await setupService.CreateProductAsync(parent);

        // Variante de 5 Kg (factor 5)
        var variant5k = new Product
        {
            Name = "Harina Trigo 5Kg",
            SKU = "7595556667771",
            ParentProductId = savedParent.Id,
            ConversionFactor = 5.0m
        };
        var savedVariant = await setupService.CreateProductAsync(variant5k);

        // Simulación de 20 peticiones concurrentes (cada una con su propio DbContext scoped)
        var lockObj = new object();
        var tasks = Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() =>
            {
                using var threadDb = CreateInMemoryInventoryDb(dbName);
                var threadService = new InventoryService(threadDb, userMock.Object);
                lock (lockObj)
                {
                    threadService.UpdateStockAsync(savedVariant.Id, -2m, "Venta POS Concurrente").GetAwaiter().GetResult();
                }
            })
        );

        await Task.WhenAll(tasks);

        using var verifyDb = CreateInMemoryInventoryDb(dbName);
        var updatedParent = await verifyDb.Products.FindAsync(savedParent.Id);
        Assert.NotNull(updatedParent);
        Assert.Equal(800m, updatedParent.StockQuantity);
    }

    [Fact]
    public void ProductItemViewModel_CanAdjustStock_MatrixValidation()
    {
        var mockExchangeRate = new Mock<Desktop.Client.Services.IExchangeRateService>();
        mockExchangeRate.Setup(e => e.CurrentRate).Returns(36.50m);

        // 1. Padre con Stock Compartido -> Permite
        var p1 = new ProductDto { Id = 1, Name = "Padre Compartido", IsGroupHeader = true, IsStockShared = true };
        var vm1 = new Desktop.Client.ViewModels.ProductItemViewModel(p1, mockExchangeRate.Object);
        Assert.True(vm1.CanAdjustStock);
        Assert.Equal(Core.Constants.InventoryMessages.TooltipAdjustStockAllowed, vm1.AdjustStockToolTip);

        // 2. Padre con Stock Individual -> Bloqueado
        var p2 = new ProductDto { Id = 2, Name = "Padre Individual", IsGroupHeader = true, IsStockShared = false };
        var vm2 = new Desktop.Client.ViewModels.ProductItemViewModel(p2, mockExchangeRate.Object);
        Assert.False(vm2.CanAdjustStock);
        Assert.Equal(Core.Constants.InventoryMessages.TooltipGroupIndividualBlocked, vm2.AdjustStockToolTip);

        // 3. Variante de Padre Compartido -> Bloqueado
        var p3 = new ProductDto { Id = 3, Name = "Variante Compartida", ParentProductId = 1, ParentIsStockShared = true };
        var vm3 = new Desktop.Client.ViewModels.ProductItemViewModel(p3, mockExchangeRate.Object);
        Assert.False(vm3.CanAdjustStock);
        Assert.Equal(Core.Constants.InventoryMessages.TooltipVariantSharedBlocked, vm3.AdjustStockToolTip);

        // 4. Variante de Padre Individual -> Permite
        var p4 = new ProductDto { Id = 4, Name = "Variante Individual", ParentProductId = 2, ParentIsStockShared = false };
        var vm4 = new Desktop.Client.ViewModels.ProductItemViewModel(p4, mockExchangeRate.Object);
        Assert.True(vm4.CanAdjustStock);
        Assert.Equal(Core.Constants.InventoryMessages.TooltipAdjustStockAllowed, vm4.AdjustStockToolTip);

        // 5. Producto Independiente -> Permite
        var p5 = new ProductDto { Id = 5, Name = "Producto Estandar" };
        var vm5 = new Desktop.Client.ViewModels.ProductItemViewModel(p5, mockExchangeRate.Object);
        Assert.True(vm5.CanAdjustStock);
        Assert.Equal(Core.Constants.InventoryMessages.TooltipAdjustStockAllowed, vm5.AdjustStockToolTip);

        // 6. Servicio / Adelanto de Efectivo -> Bloqueado
        var p6 = new ProductDto { Id = 6, Name = "Adelanto", IsCashAdvance = true };
        var vm6 = new Desktop.Client.ViewModels.ProductItemViewModel(p6, mockExchangeRate.Object);
        Assert.False(vm6.CanAdjustStock);
        Assert.Equal(Core.Constants.InventoryMessages.TooltipCashAdvance, vm6.AdjustStockToolTip);

        // 7. Producto Eliminado -> Bloqueado
        var p7 = new ProductDto { Id = 7, Name = "Archivado", IsDeleted = true };
        var vm7 = new Desktop.Client.ViewModels.ProductItemViewModel(p7, mockExchangeRate.Object);
        Assert.False(vm7.CanAdjustStock);
        Assert.Equal(Core.Constants.InventoryMessages.TooltipDeleted, vm7.AdjustStockToolTip);
    }

    [Fact]
    public async Task ProductsController_AdjustStock_ReturnsExpectedStatusCodes()
    {
        var mockService = new Mock<IInventoryService>();
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.CanMutateCatalog).Returns(true);

        var controller = new ProductsController(mockService.Object, mockUser.Object);

        // 1. Éxito -> 204 NoContent
        mockService.Setup(s => s.AdjustStockAsync(1, 10m, "Ajuste OK", null)).Returns(Task.CompletedTask);
        var result204 = await controller.AdjustStock(1, new AdjustStockDto { QuantityChange = 10m, Reason = "Ajuste OK" });
        Assert.IsType<NoContentResult>(result204);

        // 2. Operación Inválida (Bloqueo de inventario) -> 400 BadRequest
        mockService.Setup(s => s.AdjustStockAsync(2, 10m, "Ajuste Bloqueado", null))
            .ThrowsAsync(new InvalidOperationException(Core.Constants.InventoryMessages.GroupIndividualStockAdjustmentBlocked));
        var result400 = await controller.AdjustStock(2, new AdjustStockDto { QuantityChange = 10m, Reason = "Ajuste Bloqueado" });
        Assert.IsType<BadRequestObjectResult>(result400);

        // 3. Usuario sin permisos (Cajero) -> 403 Forbidden
        mockUser.Setup(u => u.CanMutateCatalog).Returns(false);
        var result403 = await controller.AdjustStock(1, new AdjustStockDto { QuantityChange = 10m, Reason = "Ajuste Cajero" });
        var status403 = Assert.IsType<ObjectResult>(result403);
        Assert.Equal(StatusCodes.Status403Forbidden, status403.StatusCode);

        // 4. Producto Inexistente -> 404 NotFound
        mockUser.Setup(u => u.CanMutateCatalog).Returns(true);
        mockService.Setup(s => s.AdjustStockAsync(999, 10m, "No Existe", null))
            .ThrowsAsync(new KeyNotFoundException());
        var result404 = await controller.AdjustStock(999, new AdjustStockDto { QuantityChange = 10m, Reason = "No Existe" });
        Assert.IsType<NotFoundResult>(result404);
    }

    #endregion
}



