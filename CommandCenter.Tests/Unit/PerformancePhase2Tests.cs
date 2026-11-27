using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class PerformancePhase2Tests
{
    [Fact]
    public void VariantManagementViewModel_ImplementsIDisposable()
    {
        var productMock = new Mock<IProductService>();
        var rateMock = new Mock<IExchangeRateService>();
        var dialogMock = new Mock<IDialogService>();
        var productDto = new ProductDto { Id = 1, Name = "Padre Test", SKU = "PADRE-1" };

        var vm = new VariantManagementViewModel(productMock.Object, rateMock.Object, dialogMock.Object, productDto);
        Assert.IsAssignableFrom<IDisposable>(vm);

        // Calling Dispose() should not throw and safely cleanup CancellationTokenSource
        var exception = Record.Exception(() => vm.Dispose());
        Assert.Null(exception);

        // Double dispose should also be safe
        var doubleDisposeEx = Record.Exception(() => vm.Dispose());
        Assert.Null(doubleDisposeEx);
    }

    [Fact]
    public void CustomerPickerViewModel_ImplementsIDisposable()
    {
        var salesMock = new Mock<Desktop.Client.Services.ISalesService>();
        var vm = new CustomerPickerViewModel(salesMock.Object);
        Assert.IsAssignableFrom<IDisposable>(vm);

        var exception = Record.Exception(() => vm.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public async Task UnlinkVariantAsync_ReadsParentWithAsNoTracking()
    {
        var options = new DbContextOptionsBuilder<Inventory.Module.Data.InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var db = new Inventory.Module.Data.InventoryDbContext(options);
        var parent = new Product
        {
            Id = 10,
            Name = "Camisa Padre",
            SKU = "CAM-PADRE",
            IsGroupHeader = true,
            IsStockShared = false,
            StockQuantity = 10m
        };
        var child = new Product
        {
            Id = 11,
            Name = "Camisa Azul M",
            SKU = "CAM-AZUL-M",
            ParentProductId = 10,
            StockQuantity = 5m
        };

        db.Products.AddRange(parent, child);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();

        var currentUser = new Mock<Core.Interfaces.ICurrentUserService>();
        currentUser.Setup(u => u.CanMutateCatalog).Returns(true);
        var service = new InventoryService(db, currentUser.Object);

        var unlinked = await service.UnlinkVariantAsync(10, 11);

        Assert.NotNull(unlinked);
        Assert.Null(unlinked.ParentProductId);

        // Parent should NOT be tracked in ChangeTracker because it was read AsNoTracking
        var parentEntry = db.ChangeTracker.Entries<Product>().FirstOrDefault(e => e.Entity.Id == 10);
        Assert.Null(parentEntry);
    }

    [Fact]
    public async Task SystemSettingsService_NegativeCaching_CachesNullSentinelAndInvalidatesOnSet()
    {
        var options = new DbContextOptionsBuilder<Inventory.Module.Data.InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var db = new Inventory.Module.Data.InventoryDbContext(options);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new SystemSettingsService(db, cache);

        // 1. Missing key returns null
        var value = await service.GetSettingAsync("UnconfiguredOptionalKey");
        Assert.Null(value);

        // 2. Negative cache entry must be present with the null sentinel
        var cacheKey = "system_setting_UnconfiguredOptionalKey";
        Assert.True(cache.TryGetValue(cacheKey, out string? cachedSentinel));
        Assert.Equal("__SYSTEM_SETTING_NULL__", cachedSentinel);

        // 3. Second query hits negative cache without hitting DB
        var cachedValue = await service.GetSettingAsync("UnconfiguredOptionalKey");
        Assert.Null(cachedValue);

        // 4. Setting the value invalidates cache and returns the new value
        await service.SetSettingAsync("UnconfiguredOptionalKey", "ConfiguredValue");
        var freshValue = await service.GetSettingAsync("UnconfiguredOptionalKey");
        Assert.Equal("ConfiguredValue", freshValue);
    }

    [Fact]
    public async Task SystemSettingsService_SetSettingsAsync_InvalidatesEveryBatchKey()
    {
        var options = new DbContextOptionsBuilder<Inventory.Module.Data.InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var db = new Inventory.Module.Data.InventoryDbContext(options);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new SystemSettingsService(db, cache);
        await service.SetSettingAsync("Batch.Key.A", "1");
        await service.SetSettingAsync("Batch.Key.B", "2");
        await service.GetSettingAsync("Batch.Key.A");
        await service.GetSettingAsync("Batch.Key.B");

        Assert.True(cache.TryGetValue("system_setting_Batch.Key.A", out _));
        Assert.True(cache.TryGetValue("system_setting_Batch.Key.B", out _));

        await service.SetSettingsAsync(new Dictionary<string, string>
        {
            ["Batch.Key.A"] = "10",
            ["Batch.Key.B"] = "20"
        });

        Assert.False(cache.TryGetValue("system_setting_Batch.Key.A", out _));
        Assert.False(cache.TryGetValue("system_setting_Batch.Key.B", out _));
    }
    [Fact]
    public async Task BulkImportProducts_CategoriesLoadedAsNoTracking_DoesNotTrackExistingCategories()
    {
        var options = new DbContextOptionsBuilder<Inventory.Module.Data.InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var db = new Inventory.Module.Data.InventoryDbContext(options);
        var existingCategory = new Category
        {
            Id = 42,
            Name = "Bebidas",
            NormalizedName = "BEBIDAS"
        };
        db.Categories.Add(existingCategory);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var currentUser = new Mock<Core.Interfaces.ICurrentUserService>();
        currentUser.Setup(u => u.CanMutateCatalog).Returns(true);
        var service = new InventoryService(db, currentUser.Object);

        var importDtos = new List<ProductImportDto>
        {
            new ProductImportDto
            {
                Name = "Refresco Cola",
                SKU = "REF-COLA",
                CostPriceUSD = 1m,
                PriceRetailUSD = 2m,
                StockQuantity = 10m,
                CategoryPath = "Bebidas"
            }
        };

        var (added, _) = await service.BulkImportProductsAsync(importDtos, overwriteMerge: false);
        Assert.Equal(1, added);

        // Existing category should not be attached to ChangeTracker as Unchanged or Modified
        var categoryEntry = db.ChangeTracker.Entries<Category>().FirstOrDefault(e => e.Entity.Id == 42);
        Assert.Null(categoryEntry);
    }
}


