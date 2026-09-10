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

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
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
    public async Task BulkImportProducts_ExistingVariant_LinksToNewGroupInSameImport()
    {
        var db = CreateInMemoryInventoryDb(Guid.NewGuid().ToString());
        var userMock = CreateAdminUserServiceMock();
        var service = new InventoryService(db, userMock.Object);

        db.Products.Add(new Product
        {
            SKU = "759888001",
            Name = "Yogurt Fresa 1L",
            IsActive = true,
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 100m,
            PriceRetailUSD = 2.00m
        });
        await db.SaveChangesAsync();

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
            }
        };

        var result = await service.BulkImportProductsAsync(importDtos, overwriteMerge: true);

        Assert.Equal(1, result.added);
        Assert.Equal(1, result.updated);

        var groupProduct = await db.Products.FirstOrDefaultAsync(p => p.SKU == "GRP-YOGURT");
        Assert.NotNull(groupProduct);

        var variant = await db.Products.FirstOrDefaultAsync(p => p.SKU == "759888001");
        Assert.NotNull(variant);
        Assert.Equal(groupProduct.Id, variant!.ParentProductId);
    }

}
