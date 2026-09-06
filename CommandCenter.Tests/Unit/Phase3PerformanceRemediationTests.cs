using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.Hubs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Interfaces;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class Phase3PerformanceRemediationTests
{
    private InventoryDbContext CreateInMemoryInventoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    [Fact]
    public async Task GetHistory_ClampsLimit_RespectsClampingAndTakesRecords()
    {
        // Arrange
        using var context = CreateInMemoryInventoryDbContext();
        var baseDate = new DateOnly(2026, 1, 1);
        for (int i = 0; i < 15; i++)
        {
            context.ExchangeRateHistory.Add(new ExchangeRateHistory
            {
                Date = baseDate.AddDays(i),
                Rate = 35.0m + i,
                UpdatedAt = DateTime.UtcNow.AddDays(i)
            });
        }
        await context.SaveChangesAsync();

        var mockUser = new Mock<ICurrentUserService>();
        var mockSales = new Mock<ISalesService>();
        var mockInventory = new Mock<IInventoryService>();
        var mockHub = new Mock<IHubContext<ExchangeRateHub>>();

        var controller = new ExchangeRateController(context, mockUser.Object, mockSales.Object, mockInventory.Object, mockHub.Object);

        // Act & Assert 1: Custom limit = 5
        var result5 = await controller.GetHistory(limit: 5) as OkObjectResult;
        Assert.NotNull(result5);
        var items5 = Assert.IsAssignableFrom<System.Collections.IEnumerable>(result5.Value).Cast<object>().ToList();
        Assert.Equal(5, items5.Count);

        // Act & Assert 2: Negative limit clamps to 1
        var resultNeg = await controller.GetHistory(limit: -5) as OkObjectResult;
        Assert.NotNull(resultNeg);
        var itemsNeg = Assert.IsAssignableFrom<System.Collections.IEnumerable>(resultNeg.Value).Cast<object>().ToList();
        Assert.Single(itemsNeg);

        // Act & Assert 3: Limit > 365 clamps to 365 (in this case returns all 15 records since 15 < 365)
        var resultOver = await controller.GetHistory(limit: 500) as OkObjectResult;
        Assert.NotNull(resultOver);
        var itemsOver = Assert.IsAssignableFrom<System.Collections.IEnumerable>(resultOver.Value).Cast<object>().ToList();
        Assert.Equal(15, itemsOver.Count);
    }

    [Fact]
    public async Task UpdateStockBatchAsync_DeductsMultipleProductsInSingleExecution()
    {
        // Arrange
        using var context = CreateInMemoryInventoryDbContext();
        var p1 = new Product
        {
            Id = 1,
            Name = "Producto A",
            SKU = "SKU-A",
            StockQuantity = 50m,
            IsActive = true
        };
        var p2 = new Product
        {
            Id = 2,
            Name = "Producto B",
            SKU = "SKU-B",
            StockQuantity = 20m,
            IsActive = true
        };
        context.Products.AddRange(p1, p2);
        await context.SaveChangesAsync();

        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("USER-TEST");
        var service = new InventoryService(context, mockUser.Object);

        var batchRequests = new List<StockDeductionRequest>
        {
            new StockDeductionRequest(1, -5m, "Venta Ítem 1"),
            new StockDeductionRequest(2, -10m, "Venta Ítem 2")
        };

        // Act
        await service.UpdateStockBatchAsync(batchRequests, userId: "USER-TEST", allowNegativeStock: false);

        // Assert
        var updatedP1 = await context.Products.FindAsync(1);
        var updatedP2 = await context.Products.FindAsync(2);

        Assert.NotNull(updatedP1);
        Assert.NotNull(updatedP2);
        Assert.Equal(45m, updatedP1.StockQuantity);
        Assert.Equal(10m, updatedP2.StockQuantity);

        var movements = await context.StockMovements.ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Contains(movements, m => m.ProductId == 1 && m.QuantityChange == -5m && m.NewStockLevel == 45m);
        Assert.Contains(movements, m => m.ProductId == 2 && m.QuantityChange == -10m && m.NewStockLevel == 10m);
    }

    [Fact]
    public async Task GetSuggestionsAsync_LimitsResultsTo10BeforeProjection()
    {
        // Arrange
        using var context = CreateInMemoryInventoryDbContext();
        for (int i = 1; i <= 25; i++)
        {
            context.Products.Add(new Product
            {
                Id = i,
                Name = $"Arroz Marca {i:D2}",
                SKU = $"7590000000{i:D2}",
                PriceRetailUSD = 1.25m,
                IsActive = true
            });
        }
        await context.SaveChangesAsync();

        var mockUser = new Mock<ICurrentUserService>();
        var service = new InventoryService(context, mockUser.Object);

        // Act
        var suggestions = await service.GetSuggestionsAsync("Arroz", false, CancellationToken.None);

        // Assert
        Assert.NotNull(suggestions);
        Assert.Equal(10, suggestions.Count);
    }
}
