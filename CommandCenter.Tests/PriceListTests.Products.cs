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
    public async Task AddSaleItem_WithNonFractionalProduct_TruncatesDecimalQuantity()
    {
        using var context = GetInMemoryDbContext();
        var product = new Product { Id = 102, Name = "Refresco", PriceUSD = 2m, IsFractional = false, UnitOfMeasure = UnitOfMeasureType.Und };
        var mockInv = CreateMockInventory(product);

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending, AppliedRate = 40m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInv.Object, Mock.Of<IMediator>(), Mock.Of<ICashDrawerService>(), Mock.Of<ISystemSettingsService>());

        var result = await service.AddItemAsync(1, 102, 1.5m, 40m);
        Assert.Single(result.Items);
        Assert.Equal(1m, result.Items[0].Quantity);
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
    public async Task UpdateSaleItems_WithNonFractionalProduct_TruncatesDecimalQuantity()
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

        var result = await service.UpdateSaleItemsAsync(1, req);
        Assert.Single(result.Items);
        Assert.Equal(2m, result.Items[0].Quantity);
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
