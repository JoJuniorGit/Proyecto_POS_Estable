using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Events;
using Inventory.Module.Data;
using Inventory.Module.EventHandlers;
using Inventory.Module.Services;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests;

public class DataIntegritySprint1Tests
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
    public async Task FractionalStock_DeductsExactDecimalQuantity_WithoutTruncation()
    {
        using var db = GetInMemoryInventoryDbContext();
        var inventoryService = new InventoryService(db);

        var product = new Product
        {
            Id = 10,
            Name = "Queso Llanero",
            SKU = "100010",
            IsFractional = true,
            UnitOfMeasure = UnitOfMeasureType.Kg,
            CostPriceUSD = 4.00m,
            PriceRetailUSD = 6.00m,
            StockQuantity = 10.000m
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var handler = new InventorySaleMadeEventHandler(inventoryService, db);
        var saleEvent = new SaleMadeEvent(
            SaleId: 101,
            SaleDate: DateTime.UtcNow,
            Items: new List<SaleItemSnapshot>
            {
                new SaleItemSnapshot(ProductId: 10, Quantity: 0.500m)
            }
        );

        await handler.Handle(saleEvent, default);

        var updated = await db.Products.FindAsync(10);
        Assert.NotNull(updated);
        // 10.000 - 0.500 = 9.500 (Previously with int cast, 0 was deducted leaving 10.000)
        Assert.Equal(9.500m, updated!.StockQuantity);
    }

    [Fact]
    public async Task UpdateStockAsync_ThrowsWhenStockWouldDropBelowZero()
    {
        using var db = GetInMemoryInventoryDbContext();
        var inventoryService = new InventoryService(db);

        var product = new Product
        {
            Id = 11,
            Name = "Harina PAN",
            SKU = "100011",
            CostPriceUSD = 0.90m,
            PriceRetailUSD = 1.20m,
            StockQuantity = 3.000m
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        // Attempt to deduct 5 when stock is only 3
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            inventoryService.UpdateStockAsync(11, -5.000m, "Deduction test"));

        Assert.Contains("Stock insuficiente", ex.Message);
    }

    [Fact]
    public async Task AddItemAsync_ThrowsWhenQuantityIsZeroOrNegative()
    {
        using var salesDb = GetInMemorySalesDbContext();
        using var invDb = GetInMemoryInventoryDbContext();
        var invService = new InventoryService(invDb);
        var cashDrawerService = new CashDrawerService(salesDb);

        salesDb.Customers.Add(new Customer { Id = 1, Name = "Consumidor Final", CedulaOrRif = "V-00000000", IsDefault = true });
        await salesDb.SaveChangesAsync();

        var salesService = new SalesService(salesDb, invService, null!, cashDrawerService, null!, null);

        var sale = await salesService.StartSaleAsync(1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            salesService.AddItemAsync(sale.Id, 1, 0m, 50m));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            salesService.AddItemAsync(sale.Id, 1, -2m, 50m));
    }

    [Fact]
    public async Task AddPaymentToHoldSale_ValidatesPositiveAmountAndExcessOverTotal()
    {
        using var salesDb = GetInMemorySalesDbContext();
        using var invDb = GetInMemoryInventoryDbContext();
        var invService = new InventoryService(invDb);
        var cashDrawerService = new CashDrawerService(salesDb);

        var product = new Product
        {
            Id = 20,
            Name = "Aceite",
            SKU = "100020",
            PriceRetailUSD = 10.00m,
            StockQuantity = 50m
        };
        invDb.Products.Add(product);
        await invDb.SaveChangesAsync();

        var defaultCust = new Customer
        {
            Id = 1,
            Name = "Consumidor Final",
            CedulaOrRif = "V-00000000",
            IsDefault = true
        };
        var customer = new Customer
        {
            Id = 5,
            Name = "Juan Perez",
            CedulaOrRif = "V-11223344",
            IsDefault = false
        };
        salesDb.Customers.AddRange(defaultCust, customer);
        await salesDb.SaveChangesAsync();

        var salesService = new SalesService(salesDb, invService, null!, cashDrawerService, null!, null);

        var sale = await salesService.StartSaleAsync(1);
        await salesService.AddItemAsync(sale.Id, 20, 1m, 50m);

        // Put on hold with customer
        await salesService.HoldSaleAsync(sale.Id, new HoldSaleRequestDto
        {
            CustomerId = 5,
            ExchangeRate = 50m
        });

        // 1. Payment with $0 must throw ArgumentException
        await Assert.ThrowsAsync<ArgumentException>(() =>
            salesService.AddPaymentToHoldSaleAsync(sale.Id, new AddPaymentRequestDto
            {
                AmountUSD = 0m,
                AmountBsS = 0m,
                ExchangeRate = 50m,
                PaymentMethodId = 1
            }));

        // 2. Payment exceeding $10 (e.g. $15) must throw InvalidOperationException
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            salesService.AddPaymentToHoldSaleAsync(sale.Id, new AddPaymentRequestDto
            {
                AmountUSD = 15m,
                AmountBsS = 750m,
                ExchangeRate = 50m,
                PaymentMethodId = 1
            }));
    }

    [Fact]
    public async Task PaymentMethodService_UpdateAsync_PersistsIsCashProperty()
    {
        using var salesDb = GetInMemorySalesDbContext();
        var service = new PaymentMethodService(salesDb);

        var created = await service.CreateAsync(new PaymentMethod
        {
            Name = "Efectivo Divisas",
            IsActive = true,
            IsCash = false // Initially false
        });

        created.IsCash = true; // Update to true
        var updated = await service.UpdateAsync(created);

        Assert.True(updated.IsCash);

        var fromDb = await service.GetByIdAsync(created.Id);
        Assert.True(fromDb.IsCash);
    }
}
