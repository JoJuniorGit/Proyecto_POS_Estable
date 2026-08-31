using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
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

namespace CommandCenter.Tests.Integration;

public class EditHoldOrderFlowIntegrationTests
{
    [Fact]
    public async Task EditHoldOrderFlow_UpdatesItems_RecalculatesTotals_AndPreservesExistingAbonos()
    {
        // 1. Setup Data & Services
        var salesContext = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(salesContext);

        var customer = new CustomerBuilder()
            .WithId(70)
            .WithCedula("V-11223344")
            .WithName("Carlos Santana")
            .Build();
        salesContext.Customers.Add(customer);
        await salesContext.SaveChangesAsync();

        var productA = new ProductBuilder().WithId(401).WithName("Bujía Bosch").WithCostAndMargin(5.00m, 100.00m).Build(); // $10.00 USD
        var productB = new ProductBuilder().WithId(402).WithName("Filtro Aceite").WithCostAndMargin(8.00m, 50.00m).Build();  // $12.00 USD

        var inventoryServiceMock = new Mock<IInventoryService>();
        inventoryServiceMock.Setup(i => i.GetProductByIdAsync(401)).ReturnsAsync(productA);
        inventoryServiceMock.Setup(i => i.GetProductByIdAsync(402)).ReturnsAsync(productB);
        inventoryServiceMock.Setup(i => i.GetProductsByIdsAsync(It.IsAny<IEnumerable<int>>())).ReturnsAsync(new List<Product> { productA, productB });

        var mediatorMock = new Mock<IMediator>();
        var cashDrawerService = new CashDrawerService(salesContext);
        var settingsMock = new Mock<ISystemSettingsService>();

        var salesService = new SalesService(salesContext, inventoryServiceMock.Object, mediatorMock.Object, cashDrawerService, settingsMock.Object);

        // 2. Create Initial OnHold Sale: 2 Bujías ($20.00 USD) + $10.00 USD initial payment
        var saleDto = await salesService.StartSaleAsync();
        await salesService.AddItemAsync(saleDto.Id, 401, 2, 50.00m);

        var holdRequest = new HoldSaleRequestDto
        {
            CustomerId = customer.Id,
            ExchangeRate = 50.00m,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = 1,
                AmountUSD = 10.00m,
                AmountBsS = 500.00m,
                ExchangeRate = 50.00m
            }
        };
        await salesService.HoldSaleAsync(saleDto.Id, holdRequest);

        var initialHold = await salesContext.Sales.Include(s => s.Payments).FirstAsync(s => s.Id == saleDto.Id);
        Assert.Equal(20.00m, initialHold.TotalUSD);
        Assert.Single(initialHold.Payments);
        Assert.Equal(10.00m, initialHold.Payments.First().Amount);

        // 3. Edit Order Items: Change to 4 Bujías ($40.00) + 2 Filtros ($24.00) = $64.00 USD Total
        var updateItemsRequest = new UpdateSaleItemsRequestDto
        {
            Items = new List<UpdateSaleItemDto>
            {
                new UpdateSaleItemDto { ProductId = 401, Quantity = 4, UnitPrice = 10.00m },
                new UpdateSaleItemDto { ProductId = 402, Quantity = 2, UnitPrice = 12.00m }
            }
        };

        var updatedSaleDto = await salesService.UpdateSaleItemsAsync(saleDto.Id, updateItemsRequest);

        // 4. Assertions
        var editedSale = await salesContext.Sales.Include(s => s.Items).Include(s => s.Payments).FirstAsync(s => s.Id == saleDto.Id);
        Assert.Equal(SaleStatus.OnHold, editedSale.Status);
        Assert.Equal(2, editedSale.Items.Count);
        Assert.Equal(64.00m, editedSale.TotalUSD);
        Assert.Equal(3200.00m, editedSale.TotalBsS);

        // Crucial Check: Existing payment of $10.00 USD must be completely preserved
        Assert.Single(editedSale.Payments);
        Assert.Equal(10.00m, editedSale.Payments.First().Amount);

        // Remaining balance check: $64.00 - $10.00 = $54.00 USD
        decimal remainingBalance = editedSale.TotalUSD - editedSale.Payments.Sum(p => p.Amount);
        Assert.Equal(54.00m, remainingBalance);
    }
}
