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

}
