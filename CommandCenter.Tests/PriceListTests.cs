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

public class PriceListTests
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
    public async Task ChangeToWholesale_AppliesWholesalePrice_WhenQuantityMeetsThreshold()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 1, Name = "Harina", PriceUSD = 2.0m, PriceRetailUSD = 2.0m, PriceWholesaleUSD = 1.5m, HasWholesale = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.Pending,
            AppliedRate = 40m,
            PriceListType = "Retail",
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 1, ProductName = "Harina", Quantity = 6, UnitPrice = 2.0m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.UpdatePriceListAsync(1, "Wholesale");

        Assert.Equal("Wholesale", result.PriceListType);
        Assert.Single(result.Items);
        Assert.Equal(1.5m, result.Items[0].UnitPrice);
        Assert.True(result.Items[0].IsWholesaleApplied);
        Assert.Equal(9.0m, result.TotalUSD);
        Assert.Equal(360.0m, result.TotalBsS);
    }

    [Fact]
    public async Task ChangeToWholesale_KeepsRetailPrice_WhenHasWholesaleIsFalse_EvenIfQuantityMeetsThreshold()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 1, Name = "Harina", PriceUSD = 2.0m, PriceRetailUSD = 2.0m, PriceWholesaleUSD = 1.5m, HasWholesale = false };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.Pending,
            AppliedRate = 40m,
            PriceListType = "Retail",
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 1, ProductName = "Harina", Quantity = 6, UnitPrice = 2.0m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.UpdatePriceListAsync(1, "Wholesale");

        Assert.Equal("Wholesale", result.PriceListType);
        Assert.Single(result.Items);
        Assert.Equal(2.0m, result.Items[0].UnitPrice);
        Assert.False(result.Items[0].IsWholesaleApplied);
        Assert.Equal(12.0m, result.TotalUSD);
    }

    [Fact]
    public async Task ChangeToWholesale_KeepsRetailPrice_WhenQuantityBelowThreshold()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 1, Name = "Harina", PriceUSD = 2.0m, PriceRetailUSD = 2.0m, PriceWholesaleUSD = 1.5m, HasWholesale = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.Pending,
            AppliedRate = 40m,
            PriceListType = "Retail",
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 1, ProductName = "Harina", Quantity = 5, UnitPrice = 2.0m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.UpdatePriceListAsync(1, "Wholesale");

        Assert.Equal("Wholesale", result.PriceListType);
        Assert.Equal(2.0m, result.Items[0].UnitPrice);
        Assert.False(result.Items[0].IsWholesaleApplied);
        Assert.Equal(10.0m, result.TotalUSD);
    }

    [Fact]
    public async Task ChangeToRetail_ResetsAllItemsToRetailPrice()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 1, Name = "Arroz", PriceUSD = 3.0m, PriceRetailUSD = 3.0m, PriceWholesaleUSD = 2.5m, HasWholesale = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.Pending,
            AppliedRate = 40m,
            PriceListType = "Wholesale",
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 1, ProductName = "Arroz", Quantity = 10, UnitPrice = 2.5m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.UpdatePriceListAsync(1, "Retail");

        Assert.Equal("Retail", result.PriceListType);
        Assert.Equal(3.0m, result.Items[0].UnitPrice);
        Assert.False(result.Items[0].IsWholesaleApplied);
        Assert.Equal(30.0m, result.TotalUSD);
    }

    [Fact]
    public async Task MixedQuantities_ApplyWholesaleOnlyToEligibleItems()
    {
        using var context = GetInMemoryDbContext();
        var p1 = new Product { Id = 1, Name = "Aceite", PriceUSD = 5.0m, PriceRetailUSD = 5.0m, PriceWholesaleUSD = 4.0m, HasWholesale = true };
        var p2 = new Product { Id = 2, Name = "Azucar", PriceUSD = 2.0m, PriceRetailUSD = 2.0m, PriceWholesaleUSD = 1.5m, HasWholesale = true };
        var mockInv = CreateMockInventory(p1, p2);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.Pending,
            AppliedRate = 40m,
            PriceListType = "Retail",
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 1, ProductName = "Aceite", Quantity = 10, UnitPrice = 5.0m },
                new SaleItem { Id = 11, ProductId = 2, ProductName = "Azucar", Quantity = 2, UnitPrice = 2.0m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.UpdatePriceListAsync(1, "Wholesale");

        Assert.Equal(4.0m, result.Items[0].UnitPrice); // Qty 10 -> Wholesale
        Assert.True(result.Items[0].IsWholesaleApplied);
        Assert.Equal(2.0m, result.Items[1].UnitPrice); // Qty 2 -> Retail fallback
        Assert.False(result.Items[1].IsWholesaleApplied);
        Assert.Equal(44.0m, result.TotalUSD); // 10*4 + 2*2 = 44
    }

    [Fact]
    public async Task RecalculateTotal_FetchesProductFromDb_WhenNavigationPropertyIsNull()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 5, Name = "Pasta", PriceUSD = 1.8m, PriceRetailUSD = 1.8m, PriceWholesaleUSD = 1.2m, HasWholesale = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.Pending,
            AppliedRate = 40m,
            PriceListType = "Wholesale",
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 5, ProductName = "Pasta", Quantity = 8, UnitPrice = 0m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.UpdatePriceListAsync(1, "Wholesale");

        mockInv.Verify(x => x.GetProductByIdAsync(5), Times.Once);
        Assert.Equal(1.2m, result.Items[0].UnitPrice);
    }

    [Fact]
    public async Task RecalculateTotal_Throws_WhenProductNotFoundInDb()
    {
        using var context = GetInMemoryDbContext();
        var mockInv = new Mock<IInventoryService>();
        mockInv.Setup(x => x.GetProductByIdAsync(999)).ReturnsAsync((Product?)null);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.Pending,
            AppliedRate = 40m,
            PriceListType = "Retail",
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 999, ProductName = "Fantasma", Quantity = 1, UnitPrice = 0m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.UpdatePriceListAsync(1, "Wholesale"));
    }

    [Fact]
    public async Task WholesalePrice_UsesRetailPrice_WhenWholesalePriceIsZero()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 1, Name = "Atun", PriceUSD = 2.5m, PriceRetailUSD = 2.5m, PriceWholesaleUSD = 0m, HasWholesale = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.Pending,
            AppliedRate = 40m,
            PriceListType = "Retail",
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 1, ProductName = "Atun", Quantity = 10, UnitPrice = 2.5m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.UpdatePriceListAsync(1, "Wholesale");

        Assert.Equal(2.5m, result.Items[0].UnitPrice);
    }

    [Fact]
    public async Task WholesalePrice_UsesRetailPrice_WhenWholesalePriceIsNegative()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 1, Name = "Salsa", PriceUSD = 4.0m, PriceRetailUSD = 4.0m, PriceWholesaleUSD = -2.0m, HasWholesale = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.Pending,
            AppliedRate = 40m,
            PriceListType = "Retail",
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 1, ProductName = "Salsa", Quantity = 10, UnitPrice = 4.0m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.UpdatePriceListAsync(1, "Wholesale");

        Assert.Equal(4.0m, result.Items[0].UnitPrice);
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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdatePriceListAsync(1, "Wholesale"));
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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => invService.CreateProductAsync(invalidProduct));
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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => invService.CreateProductAsync(invalidProduct));
        Assert.Contains("margen al mayor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- FRACTIONAL & DECIMAL QUANTITY UNIT TESTS ---

    [Fact]
    public async Task AddSaleItem_WithFractionalProduct_AllowsDecimalQuantity()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 101, Name = "Queso Gouda", PriceUSD = 10m, IsFractional = true, UnitOfMeasure = UnitOfMeasureType.Kg };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending, AppliedRate = 40m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.AddItemAsync(1, 101, 0.250m, 40m);

        Assert.Single(result.Items);
        Assert.Equal(0.250m, result.Items[0].Quantity);
    }

    [Fact]
    public async Task AddSaleItem_WithNonFractionalProduct_RejectsDecimalQuantity()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 102, Name = "Refresco", PriceUSD = 2m, IsFractional = false, UnitOfMeasure = UnitOfMeasureType.Und };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending, AppliedRate = 40m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddItemAsync(1, 102, 1.5m, 40m));
        Assert.Contains("no admite cantidades fraccionadas o decimales", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddSaleItem_WithNonFractionalProduct_AcceptsIntegerQuantity()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 103, Name = "Galletas", PriceUSD = 1m, IsFractional = false };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending, AppliedRate = 40m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.AddItemAsync(1, 103, 3m, 40m);

        Assert.Single(result.Items);
        Assert.Equal(3m, result.Items[0].Quantity);
    }

    [Fact]
    public async Task AddSaleItem_WithFractionalProduct_RoundsTo3Decimals()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 104, Name = "Carne Molida", PriceUSD = 8m, IsFractional = true, UnitOfMeasure = UnitOfMeasureType.Kg };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending, AppliedRate = 40m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.AddItemAsync(1, 104, 1.123456m, 40m);

        Assert.Single(result.Items);
        Assert.Equal(1.123m, result.Items[0].Quantity);
    }

    [Fact]
    public async Task UpdateSaleItems_WithNonFractionalProduct_RejectsDecimalQuantity()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 105, Name = "Aceite", PriceUSD = 3m, IsFractional = false };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale { Id = 1, Status = SaleStatus.OnHold, AppliedRate = 40m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var req = new Sales.Module.DTOs.UpdateSaleItemsRequestDto
        {
            Items = new List<Sales.Module.DTOs.UpdateSaleItemDto>
            {
                new Sales.Module.DTOs.UpdateSaleItemDto { ProductId = 105, Quantity = 2.75m, UnitPrice = 3m }
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateSaleItemsAsync(1, req));
        Assert.Contains("no admite cantidades fraccionadas o decimales", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WholesalePrice_Applied_WhenQuantityExceedsDecimalThreshold()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 106, Name = "Jamon", PriceRetailUSD = 10m, PriceWholesaleUSD = 8m, MinWholesaleQuantity = 5.500m, IsFractional = true, HasWholesale = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending, PriceListType = "Wholesale", AppliedRate = 40m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        await service.AddItemAsync(1, 106, 6.000m, 40m);
        var result = await service.GetSaleAsync(1);

        Assert.Equal(8m, result.Items[0].UnitPrice);
    }

    [Fact]
    public async Task WholesalePrice_NotApplied_WhenQuantityBelowDecimalThreshold()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 107, Name = "Jamon Premium", PriceRetailUSD = 10m, PriceWholesaleUSD = 8m, MinWholesaleQuantity = 5.500m, IsFractional = true, HasWholesale = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending, PriceListType = "Wholesale", AppliedRate = 40m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        await service.AddItemAsync(1, 107, 5.250m, 40m);
        var result = await service.GetSaleAsync(1);

        Assert.Equal(10m, result.Items[0].UnitPrice);
    }

    [Fact]
    public async Task WholesalePrice_Applied_WhenQuantityExactlyEqualsThreshold()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 108, Name = "Queso Blanco", PriceRetailUSD = 6m, PriceWholesaleUSD = 5m, MinWholesaleQuantity = 5.500m, IsFractional = true, HasWholesale = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending, PriceListType = "Wholesale", AppliedRate = 40m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        await service.AddItemAsync(1, 108, 5.500m, 40m);
        var result = await service.GetSaleAsync(1);

        Assert.Equal(5m, result.Items[0].UnitPrice);
    }

    [Fact]
    public async Task MinWholesaleQuantity_Default_IsSix()
    {
        var product = new Product();
        Assert.Equal(6.000m, product.MinWholesaleQuantity);
    }

    [Fact]
    public async Task MinWholesaleQuantity_AllowsDecimalValues()
    {
        var product = new Product { MinWholesaleQuantity = 5.750m };
        Assert.Equal(5.750m, product.MinWholesaleQuantity);
    }

    [Fact]
    public async Task SaleCalculation_WithDecimalMinWholesale_WorksWithMixedItems()
    {
        using var context = GetInMemoryDbContext();
        var p1 = new Product { Id = 201, Name = "P1", PriceRetailUSD = 10m, PriceWholesaleUSD = 8m, MinWholesaleQuantity = 3.000m, IsFractional = true, HasWholesale = true };
        var p2 = new Product { Id = 202, Name = "P2", PriceRetailUSD = 20m, PriceWholesaleUSD = 15m, MinWholesaleQuantity = 10.000m, IsFractional = true, HasWholesale = true };
        var mockInv = CreateMockInventory(p1, p2);

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending, PriceListType = "Wholesale", AppliedRate = 40m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        await service.AddItemAsync(1, 201, 3.500m, 40m); // meets threshold (3.5 >= 3.0) -> wholesale = 8
        await service.AddItemAsync(1, 202, 4.000m, 40m); // below threshold (4.0 < 10.0) -> retail = 20

        var result = await service.GetSaleAsync(1);

        var item1 = result.Items.Find(i => i.ProductId == 201);
        var item2 = result.Items.Find(i => i.ProductId == 202);

        Assert.Equal(8m, item1!.UnitPrice);
        Assert.Equal(20m, item2!.UnitPrice);
    }

    [Fact]
    public async Task CreateProduct_WithValidMargins_Succeeds()
    {
        var options = new DbContextOptionsBuilder<Inventory.Module.Data.InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        using var invContext = new Inventory.Module.Data.InventoryDbContext(options);
        var invService = new Inventory.Module.Services.InventoryService(invContext);

        var validProduct = new Product
        {
            Name = "Producto Valido",
            SKU = "100784",
            CostPriceUSD = 10m,
            ProfitMarginRetail = 30m,
            ProfitMarginWholesale = 20m,
            MinWholesaleQuantity = 5.5m,
            HasWholesale = true
        };

        var created = await invService.CreateProductAsync(validProduct);
        Assert.Equal(13m, created.PriceRetailUSD);
        Assert.Equal(12m, created.PriceWholesaleUSD);
    }

    [Fact]
    public async Task UpdateProduct_WithWholesaleMarginHigherThanRetail_Throws()
    {
        var options = new DbContextOptionsBuilder<Inventory.Module.Data.InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        using var invContext = new Inventory.Module.Data.InventoryDbContext(options);
        var invService = new Inventory.Module.Services.InventoryService(invContext);

        var p = new Product { Name = "Prod", SKU = "100806", CostPriceUSD = 10m, ProfitMarginRetail = 20m, ProfitMarginWholesale = 10m, HasWholesale = true };
        var created = await invService.CreateProductAsync(p);

        created.ProfitMarginWholesale = 40m; // Higher than retail (20m)
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => invService.UpdateProductAsync(created));
        Assert.Contains("margen al mayor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateProduct_WithFractionalDefaultFalse_HasUndUnit()
    {
        var p = new Product();
        Assert.False(p.IsFractional);
        Assert.Equal(UnitOfMeasureType.Und, p.UnitOfMeasure);
    }

    [Fact]
    public async Task Subtotal_WithFractionalQuantity_KeepsFullPrecision()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 301, Name = "Carne", PriceUSD = 2.50m, IsFractional = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending, AppliedRate = 40m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.AddItemAsync(1, 301, 1.125m, 40m); // 1.125 * 2.50 = 2.8125

        Assert.Equal(2.8125m, result.Items[0].Subtotal);
    }

    [Fact]
    public async Task Subtotal_DoesNotRoundQuantity_KeepsFullPrecisionSubtotal()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 302, Name = "Pollo", PriceUSD = 1.00m, IsFractional = true };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending, AppliedRate = 40m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.AddItemAsync(1, 302, 1.125m, 40m);

        Assert.Equal(1.125m, result.Items[0].Quantity);
        Assert.Equal(1.125m, result.Items[0].Subtotal);
    }

    private InventoryDbContext GetInMemoryInventoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    [Fact]
    public async Task InventoryService_CeilingRounding_PushesFractionsUpToNextCent()
    {
        using var context = GetInMemoryInventoryDbContext();
        var service = new InventoryService(context);

        // 1.00 cost with 20.004% margin -> 1.20004 -> Math.Ceiling(120.004) / 100 = 1.21
        var product = new Product
        {
            SKU = "100876",
            Name = "Producto Techo",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 20.004m,
            PriceRetailUSD = 0m
        };

        var result = await service.CreateProductAsync(product);

        Assert.Equal(1.21m, result.PriceRetailUSD);
    }

    [Fact]
    public async Task InventoryService_CeilingRounding_ExactCentRemainsExact()
    {
        using var context = GetInMemoryInventoryDbContext();
        var service = new InventoryService(context);

        // 1.00 cost with 20.00% margin -> 1.20000 -> Math.Ceiling(120.000) / 100 = 1.20
        var product = new Product
        {
            SKU = "100897",
            Name = "Producto Exacto",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 20.00m,
            PriceRetailUSD = 0m
        };

        var result = await service.CreateProductAsync(product);

        Assert.Equal(1.20m, result.PriceRetailUSD);
    }

    [Fact]
    public async Task InventoryService_ManualPricePrecedence_DoesNotOverwriteManualPrice()
    {
        using var context = GetInMemoryInventoryDbContext();
        var service = new InventoryService(context);

        // Manual price 1.50 with cost 1.00 and margin 20% -> Manual price 1.50 MUST BE PRESERVED
        var product = new Product
        {
            SKU = "100918",
            Name = "Producto Precio Manual",
            CostPriceUSD = 1.00m,
            ProfitMarginRetail = 20.00m,
            PriceRetailUSD = 1.50m
        };

        var result = await service.CreateProductAsync(product);

        Assert.Equal(1.50m, result.PriceRetailUSD);
    }
}
