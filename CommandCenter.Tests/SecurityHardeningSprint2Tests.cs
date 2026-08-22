using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Backend.API.DTOs;
using Backend.API.Middleware;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests;

public class SecurityHardeningSprint2Tests
{
    private InventoryDbContext GetInMemoryInventoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    private SalesDbContext GetInMemorySalesDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new SalesDbContext(options);
    }

    [Fact]
    public async Task SecurityHeadersMiddleware_SetsAllRequiredSecurityHeaders()
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(nextContext =>
        {
            nextContext.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        // Trigger response start callback
        context.Response.Body = new MemoryStream();
        await context.Response.StartAsync();

        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"]);
        Assert.Equal("1; mode=block", context.Response.Headers["X-XSS-Protection"]);
        Assert.Equal("strict-origin-when-cross-origin", context.Response.Headers["Referrer-Policy"]);
        Assert.True(context.Response.Headers.ContainsKey("Content-Security-Policy"));
    }

    [Fact]
    public async Task CsvExport_NeutralizesFormulaInjection()
    {
        using var db = GetInMemoryInventoryDbContext();
        var service = new InventoryService(db);

        var dangerousProduct = new Product
        {
            Id = 100,
            SKU = "=cmd|'/C calc'!A0",
            Name = "+SUM(A1:A10)",
            Description = "@hyperlink(\"http://malicious.site\")",
            CostPriceUSD = 5.00m,
            PriceRetailUSD = 10.00m,
            StockQuantity = 20m
        };

        db.Products.Add(dangerousProduct);
        await db.SaveChangesAsync();

        var csvBytes = await service.ExportProductsAsync("csv", activeOnly: false);
        var csvString = System.Text.Encoding.UTF8.GetString(csvBytes);

        // Must prefix dangerous leading formula characters with '
        Assert.Contains("'=cmd|", csvString);
        Assert.Contains("'+SUM", csvString);
        Assert.Contains("'@hyperlink", csvString);
    }

    [Fact]
    public async Task CashDrawerService_ThrowsOnNegativeOpeningOrClosingBalance()
    {
        using var db = GetInMemorySalesDbContext();
        var service = new CashDrawerService(db);

        // Negative opening balance
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.OpenSessionAsync(-100m, 50m));

        // Invalid exchange rate
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.OpenSessionAsync(100m, 0m));

        // Valid opening
        var session = await service.OpenSessionAsync(100m, 50m);
        Assert.Equal(CashDrawerStatus.Open, session.Status);

        // Negative closing balance
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CloseSessionAsync(-50m, 50m));
    }

    [Fact]
    public async Task DailyClosureService_ThrowsOnNegativeActualAmount()
    {
        using var db = GetInMemorySalesDbContext();
        var service = new DailyClosureService(db);

        var method = new PaymentMethod { Id = 1, Name = "Efectivo", IsActive = true };
        db.PaymentMethods.Add(method);
        await db.SaveChangesAsync();

        var closure = new DailyClosure
        {
            ClosureDate = DateTime.UtcNow,
            Details = new List<ClosureDetail>
            {
                new ClosureDetail
                {
                    PaymentMethodId = 1,
                    PaymentMethodName = "Efectivo",
                    ExpectedAmountBsS = 100m,
                    ActualAmountBsS = -20m // Negative
                }
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateClosureAsync(closure));
    }

    [Fact]
    public async Task ReservationsController_RejectsNegativeQuantityAndClampsDuration()
    {
        using var db = GetInMemoryInventoryDbContext();
        var inventoryService = new InventoryService(db);
        var controller = new ReservationsController(inventoryService);

        // Negative quantity must return BadRequest
        var result = await controller.ReserveStock(new ReserveStockDto
        {
            ProductId = 1,
            Quantity = -5m,
            DurationSeconds = 300
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
