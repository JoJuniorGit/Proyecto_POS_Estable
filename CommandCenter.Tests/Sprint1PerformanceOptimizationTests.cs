using Core.DTOs;
using Core.Entities;
using Core.Events;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests;

public class Sprint1PerformanceOptimizationTests
{
    private SalesDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private (SalesService service, SalesDbContext context, Mock<IInventoryService> mockInventory) CreateSalesService(SalesDbContext context)
    {
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockCashDrawer
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSession { Id = 1, Status = CashDrawerStatus.Open });

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
        return (service, context, mockInventory);
    }

    [Fact]
    public async Task FullPosFlow_AddItem_UpdateQuantity_UpdateRate_CompleteSale_Succeeds()
    {
        using var context = GetInMemoryDbContext();
        var (service, _, mockInventory) = CreateSalesService(context);

        // Seed customer & payment method
        var customer = new Customer { Id = 1, Name = "Consumidor Final", CedulaOrRif = "V-00000000", IsDefault = true, IsActive = true };
        var paymentMethod = new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true };
        context.Customers.Add(customer);
        context.PaymentMethods.Add(paymentMethod);
        await context.SaveChangesAsync();

        var prod1 = new Product { Id = 101, Name = "Arroz 1kg", PriceUSD = 1.50m, PriceRetailUSD = 1.50m, IsActive = true };
        var prod2 = new Product { Id = 102, Name = "Harina PAN", PriceUSD = 1.20m, PriceRetailUSD = 1.20m, IsActive = true };

        mockInventory.Setup(i => i.GetProductByIdAsync(101)).ReturnsAsync(prod1);
        mockInventory.Setup(i => i.GetProductByIdAsync(102)).ReturnsAsync(prod2);
        mockInventory.Setup(i => i.GetProductsByIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync((IEnumerable<int> ids) => new List<Product> { prod1, prod2 }.Where(p => ids.Contains(p.Id)).ToList());

        // 1. Start Sale
        var saleDto = await service.StartSaleAsync();
        Assert.NotNull(saleDto);
        int saleId = saleDto.Id;

        // 2. Add items
        var updatedSale = await service.AddItemAsync(saleId, 101, 2m, 50m);
        Assert.Single(updatedSale.Items);
        Assert.Equal(3.00m, updatedSale.TotalUSD);
        Assert.Equal(150.00m, updatedSale.TotalBsS);

        updatedSale = await service.AddItemAsync(saleId, 102, 3m, 50m);
        Assert.Equal(2, updatedSale.Items.Count);
        Assert.Equal(6.60m, updatedSale.TotalUSD); // 2*1.50 + 3*1.20 = 3.00 + 3.60 = 6.60
        Assert.Equal(330.00m, updatedSale.TotalBsS); // 6.60 * 50 = 330.00

        // 3. Update quantity of item 1
        var item1 = updatedSale.Items.First(i => i.ProductId == 101);
        updatedSale = await service.UpdateItemQuantityAsync(saleId, item1.Id, 4m, 50m);
        Assert.Equal(9.60m, updatedSale.TotalUSD); // 4*1.50 + 3*1.20 = 6.00 + 3.60 = 9.60

        // 4. Update exchange rate to 60
        updatedSale = await service.UpdateExchangeRateAsync(saleId, 60m);
        Assert.Equal(9.60m, updatedSale.TotalUSD);
        Assert.Equal(576.00m, updatedSale.TotalBsS); // 9.60 * 60 = 576.00

        // 5. Complete Sale
        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 9.60m, 576.00m, null)
        };

        int invoiceNumber = await service.CompleteSaleAsync(saleId, 60m, payments, 0m);
        Assert.True(invoiceNumber > 0);

        var savedSale = await context.Sales.Include(s => s.Payments).Include(s => s.Items).FirstAsync(s => s.Id == saleId);
        Assert.Equal(SaleStatus.Completed, savedSale.Status);
        Assert.Equal(9.60m, savedSale.TotalUSD);
        Assert.Equal(invoiceNumber, savedSale.InvoiceNumber);
    }

    [Fact]
    public async Task UpdateSaleItemsAsync_UsesBatchProductFetch_AndReplacesItemsCorrectly()
    {
        using var context = GetInMemoryDbContext();
        var (service, _, mockInventory) = CreateSalesService(context);

        var customer = new Customer { Id = 2, Name = "Juan Perez", CedulaOrRif = "V-12345678", IsActive = true };
        var paymentMethod = new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true };
        context.Customers.Add(customer);
        context.PaymentMethods.Add(paymentMethod);
        await context.SaveChangesAsync();

        var prod1 = new Product { Id = 201, Name = "Aceite 1L", PriceUSD = 3.00m, PriceRetailUSD = 3.00m, IsActive = true };
        var prod2 = new Product { Id = 202, Name = "Azúcar 1kg", PriceUSD = 1.00m, PriceRetailUSD = 1.00m, IsActive = true };
        var prod3 = new Product { Id = 203, Name = "Café 500g", PriceUSD = 4.50m, PriceRetailUSD = 4.50m, IsActive = true };

        mockInventory.Setup(i => i.GetProductByIdAsync(201)).ReturnsAsync(prod1);
        mockInventory.Setup(i => i.GetProductByIdAsync(202)).ReturnsAsync(prod2);
        mockInventory.Setup(i => i.GetProductByIdAsync(203)).ReturnsAsync(prod3);
        mockInventory.Setup(i => i.GetProductsByIdsAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync((IEnumerable<int> ids) => new List<Product> { prod1, prod2, prod3 }.Where(p => ids.Contains(p.Id)).ToList());

        // Create an on-hold sale with prod1
        var sale = new Sale
        {
            Status = SaleStatus.OnHold,
            Date = DateTime.UtcNow,
            CustomerId = 2,
            CustomerName = "Juan Perez",
            CustomerCedula = "V-12345678",
            AppliedRate = 50m,
            TotalUSD = 3.00m,
            Subtotal = 3.00m,
            TotalBsS = 150m,
            SubtotalBsS = 150m,
            Items = new List<SaleItem>
            {
                new SaleItem { ProductId = 201, ProductName = "Aceite 1L", Quantity = 1m, UnitPrice = 3.00m, Subtotal = 3.00m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        // Update items to prod2 and prod3 via batch
        var request = new UpdateSaleItemsRequestDto
        {
            Items = new List<UpdateSaleItemDto>
            {
                new UpdateSaleItemDto { ProductId = 202, Quantity = 2m, UnitPrice = 1.00m },
                new UpdateSaleItemDto { ProductId = 203, Quantity = 1m, UnitPrice = 4.50m }
            }
        };

        var result = await service.UpdateSaleItemsAsync(sale.Id, request);

        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, i => i.ProductId == 202 && i.Quantity == 2m);
        Assert.Contains(result.Items, i => i.ProductId == 203 && i.Quantity == 1m);
        Assert.Equal(6.50m, result.TotalUSD); // 2*1.00 + 1*4.50 = 6.50
        Assert.Equal(325.00m, result.TotalBsS); // 6.50 * 50 = 325.00

        // Verify batch method was called
        mockInventory.Verify(i => i.GetProductsByIdsAsync(It.Is<IEnumerable<int>>(ids => ids.Contains(202) && ids.Contains(203))), Times.AtLeastOnce());
    }
}
