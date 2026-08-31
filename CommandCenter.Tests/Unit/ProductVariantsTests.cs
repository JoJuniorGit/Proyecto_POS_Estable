using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using MediatR;
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
}


