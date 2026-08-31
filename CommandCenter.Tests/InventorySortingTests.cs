using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CommandCenter.Tests;

public class InventorySortingTests
{
    private InventoryDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    [Fact]
    public async Task InventoryService_Sorting_ByNameAscendingAndDescending()
    {
        using var db = CreateInMemoryDbContext();
        db.Products.AddRange(
            new Product { Name = "Zanahoria", SKU = "001", PriceUSD = 1.0m },
            new Product { Name = "Arroz", SKU = "002", PriceUSD = 2.0m },
            new Product { Name = "Manzana", SKU = "003", PriceUSD = 1.5m },
            new Product { Name = "Banana", SKU = "004", PriceUSD = 0.8m }
        );
        await db.SaveChangesAsync();

        var service = new InventoryService(db);

        // Ascending
        var ascResult = await service.GetProductsPagedAsync(null, 1, 10, sortBy: "name", isDescending: false);
        var ascNames = ascResult.Items.Select(p => p.Name).ToList();
        Assert.Equal(new[] { "Arroz", "Banana", "Manzana", "Zanahoria" }, ascNames);

        // Descending
        var descResult = await service.GetProductsPagedAsync(null, 1, 10, sortBy: "name", isDescending: true);
        var descNames = descResult.Items.Select(p => p.Name).ToList();
        Assert.Equal(new[] { "Zanahoria", "Manzana", "Banana", "Arroz" }, descNames);
    }

    [Fact]
    public async Task InventoryService_Sorting_ByPriceWithFallback()
    {
        using var db = CreateInMemoryDbContext();
        db.Products.AddRange(
            new Product { Name = "Item A", SKU = "001", PriceRetailUSD = 10.0m, PriceUSD = 10.0m },
            new Product { Name = "Item B", SKU = "002", PriceRetailUSD = 0m, PriceUSD = 25.0m }, // Fallback to PriceUSD
            new Product { Name = "Item C", SKU = "003", PriceRetailUSD = 5.0m, PriceUSD = 5.0m },
            new Product { Name = "Item D", SKU = "004", PriceRetailUSD = 50.0m, PriceUSD = 50.0m }
        );
        await db.SaveChangesAsync();

        var service = new InventoryService(db);

        // Ascending Price: 5 (C) -> 10 (A) -> 25 (B) -> 50 (D)
        var ascResult = await service.GetProductsPagedAsync(null, 1, 10, sortBy: "price", isDescending: false);
        var ascNames = ascResult.Items.Select(p => p.Name).ToList();
        Assert.Equal(new[] { "Item C", "Item A", "Item B", "Item D" }, ascNames);

        // Descending Price: 50 (D) -> 25 (B) -> 10 (A) -> 5 (C)
        var descResult = await service.GetProductsPagedAsync(null, 1, 10, sortBy: "price", isDescending: true);
        var descNames = descResult.Items.Select(p => p.Name).ToList();
        Assert.Equal(new[] { "Item D", "Item B", "Item A", "Item C" }, descNames);
    }

    [Fact]
    public async Task InventoryService_Sorting_ByCostWithFallback()
    {
        using var db = CreateInMemoryDbContext();
        db.Products.AddRange(
            new Product { Name = "Item A", SKU = "001", CostPriceUSD = 8.0m, Cost = 8.0m },
            new Product { Name = "Item B", SKU = "002", CostPriceUSD = 0m, Cost = 20.0m }, // Fallback to Cost
            new Product { Name = "Item C", SKU = "003", CostPriceUSD = 2.0m, Cost = 2.0m }
        );
        await db.SaveChangesAsync();

        var service = new InventoryService(db);

        // Ascending Cost: 2 (C) -> 8 (A) -> 20 (B)
        var ascResult = await service.GetProductsPagedAsync(null, 1, 10, sortBy: "cost", isDescending: false);
        var ascNames = ascResult.Items.Select(p => p.Name).ToList();
        Assert.Equal(new[] { "Item C", "Item A", "Item B" }, ascNames);

        // Descending Cost: 20 (B) -> 8 (A) -> 2 (C)
        var descResult = await service.GetProductsPagedAsync(null, 1, 10, sortBy: "cost", isDescending: true);
        var descNames = descResult.Items.Select(p => p.Name).ToList();
        Assert.Equal(new[] { "Item B", "Item A", "Item C" }, descNames);
    }

    [Fact]
    public async Task InventoryService_Sorting_ByStockQuantity()
    {
        using var db = CreateInMemoryDbContext();
        db.Products.AddRange(
            new Product { Name = "Item A", SKU = "001", StockQuantity = 100m },
            new Product { Name = "Item B", SKU = "002", StockQuantity = 0m },
            new Product { Name = "Item C", SKU = "003", StockQuantity = 15m },
            new Product { Name = "Item D", SKU = "004", StockQuantity = 50m }
        );
        await db.SaveChangesAsync();

        var service = new InventoryService(db);

        // Ascending: 0 (B) -> 15 (C) -> 50 (D) -> 100 (A)
        var ascResult = await service.GetProductsPagedAsync(null, 1, 10, sortBy: "stock", isDescending: false);
        var ascNames = ascResult.Items.Select(p => p.Name).ToList();
        Assert.Equal(new[] { "Item B", "Item C", "Item D", "Item A" }, ascNames);

        // Descending: 100 (A) -> 50 (D) -> 15 (C) -> 0 (B)
        var descResult = await service.GetProductsPagedAsync(null, 1, 10, sortBy: "stock", isDescending: true);
        var descNames = descResult.Items.Select(p => p.Name).ToList();
        Assert.Equal(new[] { "Item A", "Item D", "Item C", "Item B" }, descNames);
    }

    [Fact]
    public async Task InventoryService_Sorting_BySkuLength_WithLexicographicalTieBreakAndNullProtection()
    {
        using var db = CreateInMemoryDbContext();
        db.Products.AddRange(
            new Product { Name = "P_Null", SKU = "", PriceUSD = 1m },             // Length 0
            new Product { Name = "P_Single9", SKU = "9", PriceUSD = 1m },         // Length 1
            new Product { Name = "P_Single5", SKU = "5", PriceUSD = 1m },         // Length 1
            new Product { Name = "P_Two99", SKU = "99", PriceUSD = 1m },          // Length 2
            new Product { Name = "P_Two01", SKU = "01", PriceUSD = 1m },          // Length 2
            new Product { Name = "P_Two10", SKU = "10", PriceUSD = 1m },          // Length 2
            new Product { Name = "P_Three100", SKU = "100", PriceUSD = 1m },      // Length 3
            new Product { Name = "P_Eight", SKU = "12345678", PriceUSD = 1m }     // Length 8
        );
        await db.SaveChangesAsync();

        var service = new InventoryService(db);

        // Ascending: Length 0 (""), Length 1 ("5", "9"), Length 2 ("01", "10", "99"), Length 3 ("100"), Length 8 ("12345678")
        var ascResult = await service.GetProductsPagedAsync(null, 1, 20, sortBy: "sku", isDescending: false);
        var ascSkus = ascResult.Items.Select(p => p.SKU).ToList();
        Assert.Equal(new[] { "", "5", "9", "01", "10", "99", "100", "12345678" }, ascSkus);

        // Descending: Length 8 ("12345678"), Length 3 ("100"), Length 2 ("99", "10", "01"), Length 1 ("9", "5"), Length 0 ("")
        var descResult = await service.GetProductsPagedAsync(null, 1, 20, sortBy: "sku", isDescending: true);
        var descSkus = descResult.Items.Select(p => p.SKU).ToList();
        Assert.Equal(new[] { "12345678", "100", "99", "10", "01", "9", "5", "" }, descSkus);
    }

    [Fact]
    public async Task InventoryViewModel_SortCommand_TogglesDirectionAndResetsToPageOne()
    {
        var productMock = new Mock<IProductService>();
        var rateMock = new Mock<IExchangeRateService>();

        productMock.Setup(s => s.GetPagedAsync(
            It.IsAny<string?>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResultDto<ProductDto>
            {
                TotalCount = 50,
                Items = new List<ProductDto>()
            });

        rateMock.Setup(s => s.CurrentRate).Returns(36.5m);
        rateMock.Setup(s => s.GetCurrentRateAsync()).ReturnsAsync((36.5m, (DateTime?)DateTime.UtcNow));

        var vm = new InventoryViewModel(productMock.Object, rateMock.Object);
        vm.CurrentPage = 3;

        // 1. Initial defaults
        Assert.Equal("name", vm.SortBy);
        Assert.False(vm.IsSortDescending);
        Assert.True(vm.IsSortedByName);

        // 2. Sort by "sku" -> SortBy = "sku", IsSortDescending = false, CurrentPage reset to 1
        await vm.SortCommand.ExecuteAsync("sku");
        Assert.Equal("sku", vm.SortBy);
        Assert.False(vm.IsSortDescending);
        Assert.True(vm.IsSortedBySku);
        Assert.False(vm.IsSortedByName);
        Assert.Equal(1, vm.CurrentPage);

        // 3. Sort by "sku" again -> Toggles to descending
        await vm.SortCommand.ExecuteAsync("sku");
        Assert.Equal("sku", vm.SortBy);
        Assert.True(vm.IsSortDescending);

        // 4. Sort by "price" -> SortBy = "price", IsSortDescending = false
        await vm.SortCommand.ExecuteAsync("price");
        Assert.Equal("price", vm.SortBy);
        Assert.False(vm.IsSortDescending);
        Assert.True(vm.IsSortedByPrice);
        Assert.False(vm.IsSortedBySku);
    }
}
