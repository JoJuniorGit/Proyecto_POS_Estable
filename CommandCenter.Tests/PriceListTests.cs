using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Inventory.Module.Services;
using Inventory.Module.Data;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests;

public partial class PriceListTests
{
    private SalesDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private Mock<IInventoryService> CreateMockInventory(params Product[] products)
    {
        var mock = new Mock<IInventoryService>();
        foreach (var p in products)
        {
            mock.Setup(x => x.GetProductByIdAsync(p.Id)).ReturnsAsync(p);
        }
        return mock;
    }

    [Fact]
    public async Task UpdatePriceList_Throws_WhenOnHoldAndNewTotalBelowPaid()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 1, Name = "Cafe", PriceUSD = 10.0m, PriceRetailUSD = 10.0m, PriceWholesaleUSD = 5.0m, HasWholesale = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.OnHold,
            AppliedRate = 40m,
            PriceListType = "Retail",
            TotalUSD = 100.0m,
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 1, ProductName = "Cafe", Quantity = 10, UnitPrice = 10.0m }
            },
            Payments = new List<SalePayment>
            {
                new SalePayment { Id = 1, Amount = 80.0m, AmountBsS = 3200m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.UpdatePriceListAsync(1, "Wholesale"));
        Assert.Contains("menor al monto ya abonado", ex.Message);
    }

    [Fact]
    public async Task UpdatePriceList_Throws_WhenSaleIsCompleted()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 1, Name = "Jabon", PriceUSD = 1.0m, PriceRetailUSD = 1.0m, PriceWholesaleUSD = 0.8m, HasWholesale = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.Completed,
            AppliedRate = 40m,
            PriceListType = "Retail",
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 1, ProductName = "Jabon", Quantity = 10, UnitPrice = 1.0m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdatePriceListAsync(1, "Wholesale"));
        Assert.Contains("venta ya finalizada", ex.Message);
    }

    [Fact]
    public async Task UpdatePriceList_Succeeds_WhenOnHoldAndNewTotalEqualsPaid()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 1, Name = "Leche", PriceUSD = 10.0m, PriceRetailUSD = 10.0m, PriceWholesaleUSD = 5.0m, HasWholesale = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.OnHold,
            AppliedRate = 40m,
            PriceListType = "Retail",
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 1, ProductName = "Leche", Quantity = 10, UnitPrice = 10.0m }
            },
            Payments = new List<SalePayment>
            {
                new SalePayment { Id = 1, Amount = 50.0m, AmountBsS = 2000m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.UpdatePriceListAsync(1, "Wholesale");

        Assert.Equal(50.0m, result.TotalUSD); // 10 * $5 = $50 == paid $50
        Assert.Equal(50.0m, result.TotalPaidUSD);
        Assert.Equal(0m, result.RemainingBalanceUSD);
    }

    [Fact]
    public async Task UpdatePriceList_Succeeds_WhenOnHoldWithNoPayments()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 1, Name = "Mantequilla", PriceUSD = 4.0m, PriceRetailUSD = 4.0m, PriceWholesaleUSD = 3.0m, HasWholesale = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.OnHold,
            AppliedRate = 40m,
            PriceListType = "Retail",
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 1, ProductName = "Mantequilla", Quantity = 8, UnitPrice = 4.0m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.UpdatePriceListAsync(1, "Wholesale");

        Assert.Equal(24.0m, result.TotalUSD); // 8 * 3 = 24
    }

    [Fact]
    public async Task UpdatePriceList_Succeeds_WhenNotOnHold()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 1, Name = "Galletas", PriceUSD = 2.0m, PriceRetailUSD = 2.0m, PriceWholesaleUSD = 1.5m, HasWholesale = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.Pending,
            AppliedRate = 40m,
            PriceListType = "Retail",
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 1, ProductName = "Galletas", Quantity = 10, UnitPrice = 2.0m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.UpdatePriceListAsync(1, "Wholesale");

        Assert.Equal("Wholesale", result.PriceListType);
        Assert.Equal(15.0m, result.TotalUSD);
    }

    [Fact]
    public async Task RecalculateTotal_UpdatesSubtotalBsS_Correctly()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 1, Name = "Jugo", PriceUSD = 1.25m, PriceRetailUSD = 1.25m, PriceWholesaleUSD = 1.0m, HasWholesale = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.Pending,
            AppliedRate = 748.79m,
            PriceListType = "Retail",
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 1, ProductName = "Jugo", Quantity = 6, UnitPrice = 1.25m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.UpdatePriceListAsync(1, "Wholesale");

        Assert.Equal(6.0m, result.TotalUSD); // 6 * $1.0 = $6.00
        Assert.Equal(4492.74m, result.TotalBsS); // 6 * (1.0 * 748.79 = 748.79) = 4492.74
        Assert.Equal(result.SubtotalBsS, result.TotalBsS);
    }

    [Fact]
    public async Task ChangeToWholesale_UsesCustomMinWholesaleQuantity_WhenConfiguredOnProduct()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 1, Name = "Refresco 3L", PriceUSD = 3.0m, PriceRetailUSD = 3.0m, PriceWholesaleUSD = 2.0m, MinWholesaleQuantity = 3, HasWholesale = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.Pending,
            AppliedRate = 40m,
            PriceListType = "Retail",
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 1, ProductName = "Refresco 3L", Quantity = 3, UnitPrice = 3.0m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.UpdatePriceListAsync(1, "Wholesale");

        Assert.Equal("Wholesale", result.PriceListType);
        Assert.Equal(6.0m, result.TotalUSD); // 3 * $2.00 = $6.00 (wholesale price applied at Qty=3 because MinWholesaleQuantity=3)
    }
    [Fact]
    public async Task InventoryService_CreateProduct_Throws_WhenWholesalePriceExceedsRetailPrice()
    {
        var options = new DbContextOptionsBuilder<Inventory.Module.Data.InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        using var invContext = new Inventory.Module.Data.InventoryDbContext(options);
        var invService = new Inventory.Module.Services.InventoryService(invContext);

        var invalidProduct = new Product
        {
            Name = "Producto Invalido",
            SKU = "100534",
            CostPriceUSD = 10m,
            ProfitMarginRetail = 10m, // PriceRetailUSD = 11m
            PriceRetailUSD = 11m,
            ProfitMarginWholesale = 30m, // PriceWholesaleUSD = 13m
            PriceWholesaleUSD = 13m,
            MinWholesaleQuantity = 6,
            HasWholesale = true
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => invService.CreateProductAsync(invalidProduct));
        Assert.Contains("precio al mayor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InventoryService_CreateProduct_Throws_WhenWholesaleMarginExceedsRetailMargin()
    {
        var options = new DbContextOptionsBuilder<Inventory.Module.Data.InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        using var invContext = new Inventory.Module.Data.InventoryDbContext(options);
        var invService = new Inventory.Module.Services.InventoryService(invContext);

        var invalidProduct = new Product
        {
            Name = "Producto Invalido Margen",
            SKU = "100560",
            CostPriceUSD = 10m,
            ProfitMarginRetail = 20m,
            PriceRetailUSD = 12m,
            ProfitMarginWholesale = 25m,
            PriceWholesaleUSD = 12m,
            MinWholesaleQuantity = 6,
            HasWholesale = true
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => invService.CreateProductAsync(invalidProduct));
        Assert.Contains("margen al mayor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- FRACTIONAL & DECIMAL QUANTITY UNIT TESTS ---
}
