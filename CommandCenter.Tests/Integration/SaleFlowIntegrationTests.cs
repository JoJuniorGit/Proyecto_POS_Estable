using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
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
using Xunit;
using SalesService = Sales.Module.Services.SalesService;
using ICashDrawerService = Sales.Module.Interfaces.ICashDrawerService;
using CashDrawerStatus = Sales.Module.Entities.CashDrawerStatus;
using CashTransactionType = Sales.Module.Entities.CashTransactionType;
using CashTransactionSource = Sales.Module.Entities.CashTransactionSource;

namespace CommandCenter.Tests.Integration;

public class SaleFlowIntegrationTests
{
    [Fact]
    public async Task CompleteNormalSaleFlow_CreatesSale_AddsItems_ChecksOut_AndRecordsDrawerTransactions()
    {
        // 1. Setup Data & Services
        var salesContext = TestDatabaseFactory.CreateSalesDbContext();
        var inventoryContext = TestDatabaseFactory.CreateInventoryDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(salesContext);

        var product1 = new ProductBuilder().WithId(101).WithSku("SKU-HARINA").WithName("Harina Pan").WithCostAndMargin(1.00m, 20.00m).WithStock(50m).Build();
        var product2 = new ProductBuilder().WithId(102).WithSku("SKU-ARROZ").WithName("Arroz Blanco").WithCostAndMargin(0.80m, 25.00m).WithStock(40m).Build();
        inventoryContext.Products.AddRange(product1, product2);
        await inventoryContext.SaveChangesAsync();

        var inventoryServiceMock = new Mock<IInventoryService>();
        inventoryServiceMock.Setup(i => i.GetProductByIdAsync(101)).ReturnsAsync(product1);
        inventoryServiceMock.Setup(i => i.GetProductByIdAsync(102)).ReturnsAsync(product2);
        inventoryServiceMock.Setup(i => i.GetProductsByIdsAsync(It.IsAny<IEnumerable<int>>())).ReturnsAsync(new List<Product> { product1, product2 });

        var mediatorMock = new Mock<IMediator>();
        var cashDrawerService = new CashDrawerService(salesContext);
        var settingsMock = new Mock<ISystemSettingsService>();

        var salesService = new SalesService(salesContext, inventoryServiceMock.Object, mediatorMock.Object, cashDrawerService, settingsMock.Object);

        // 2. Open Cash Drawer Session
        var session = await cashDrawerService.OpenSessionAsync(2000m, 50.00m);

        // 3. Start Sale
        var saleDto = await salesService.StartSaleAsync(1);
        Assert.NotNull(saleDto);
        Assert.Equal("Pending", saleDto.Status);

        // 4. Add Items (2 Harina @ $1.20 = $2.40; 3 Arroz @ $1.00 = $3.00 -> Total = $5.40 USD = 270 Bs.S)
        await salesService.AddItemAsync(saleDto.Id, 101, 2, 50.00m);
        var updatedSale = await salesService.AddItemAsync(saleDto.Id, 102, 3, 50.00m);

        Assert.Equal(5.40m, updatedSale.TotalUSD);
        Assert.Equal(270.00m, updatedSale.TotalBsS);

        // 5. Checkout (Pay $5.40 USD exact: $3.00 USD cash + 120.00 Bs.S cash)
        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 3.00m, 150.00m, null), // Efectivo USD
            new PaymentInfo(2, 2.40m, 120.00m, null)  // Efectivo Bs.S
        };

        int invoiceNumber = await salesService.CompleteSaleAsync(saleDto.Id, 50.00m, payments);

        // 6. Assert Sale State
        var completedSale = await salesContext.Sales.Include(s => s.Items).Include(s => s.Payments).FirstAsync(s => s.Id == saleDto.Id);
        Assert.Equal(SaleStatus.Completed, completedSale.Status);
        Assert.Equal(invoiceNumber, completedSale.InvoiceNumber);
        Assert.Equal(5.40m, completedSale.TotalUSD);
        Assert.Equal(2, completedSale.Payments.Count);

        // 7. Assert MediatR Event was published once
        mediatorMock.Verify(m => m.Publish(It.Is<SaleMadeEvent>(e => e.SaleId == saleDto.Id && e.Items.Count() == 2), default), Times.Once);

        // 8. Assert Cash Drawer physical balance updated
        // 2000 initial + 150 USD_Cash (converted) + 120 BsS_Cash = 2270 BsS
        var transactions = await salesContext.CashTransactions.Where(t => t.SaleId == saleDto.Id).ToListAsync();
        Assert.Equal(2, transactions.Count);
        Assert.All(transactions, t => Assert.True(t.IsPhysicalCash));

        decimal currentBalance = await cashDrawerService.GetCurrentBalanceLocalAsync(session.Id);
        Assert.Equal(2270.00m, currentBalance);
    }
}
