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

public class PartialPaymentFlowIntegrationTests
{
    [Fact]
    public async Task PartialPaymentFlow_HoldsOrder_RecordsAbonos_UpdatesBalance_AndRegistersDrawerCash()
    {
        // 1. Setup Data & Services
        var salesContext = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(salesContext);

        var customer = new CustomerBuilder()
            .WithId(50)
            .WithCedula("V-25123456")
            .WithName("Maria Perez")
            .Build();
        salesContext.Customers.Add(customer);
        await salesContext.SaveChangesAsync();

        var product = new ProductBuilder().WithId(201).WithName("Batería 12V").WithCostAndMargin(80.00m, 25.00m).Build(); // $100.00 USD
        var inventoryServiceMock = new Mock<IInventoryService>();
        inventoryServiceMock.Setup(i => i.GetProductByIdAsync(201)).ReturnsAsync(product);

        var mediatorMock = new Mock<IMediator>();
        var cashDrawerService = new CashDrawerService(salesContext);
        var settingsMock = new Mock<ISystemSettingsService>();

        var salesService = new SalesService(salesContext, inventoryServiceMock.Object, mediatorMock.Object, cashDrawerService, settingsMock.Object);

        // 2. Open Drawer Session
        var session = await cashDrawerService.OpenSessionAsync(1000m, 50.00m);

        // 3. Start Sale and Add $100 Item
        var saleDto = await salesService.StartSaleAsync();
        await salesService.AddItemAsync(saleDto.Id, 201, 1, 50.00m);

        // 4. Place on Hold with initial payment of $20.00 USD (Efectivo USD)
        var holdRequest = new HoldSaleRequestDto
        {
            CustomerId = customer.Id,
            ExchangeRate = 50.00m,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = 1, // Efectivo USD
                AmountUSD = 20.00m,
                AmountBsS = 1000.00m,
                ExchangeRate = 50.00m
            }
        };

        var holdResult = await salesService.HoldSaleAsync(saleDto.Id, holdRequest);

        Assert.Equal("OnHold", holdResult.Status);
        Assert.Equal(100.00m, holdResult.TotalUSD);

        // 5. Add second partial payment: $30.00 USD via Cash (1500 Bs.S)
        var secondPaymentRequest = new AddPaymentRequestDto
        {
            PaymentMethodId = 1,
            AmountUSD = 30.00m,
            AmountBsS = 1500.00m,
            ExchangeRate = 50.00m,
            ReferenceNumber = null
        };

        var secondResult = await salesService.AddPaymentToHoldSaleAsync(saleDto.Id, secondPaymentRequest);

        // 6. Assertions
        var savedSale = await salesContext.Sales.Include(s => s.Payments).FirstAsync(s => s.Id == saleDto.Id);
        Assert.Equal(SaleStatus.OnHold, savedSale.Status);
        Assert.Equal(2, savedSale.Payments.Count);

        decimal totalPaid = savedSale.Payments.Sum(p => p.Amount);
        decimal remainingBalance = savedSale.TotalUSD - totalPaid;
        Assert.Equal(50.00m, totalPaid);
        Assert.Equal(50.00m, remainingBalance);

        // 7. Inventory deduction must NOT have occurred yet
        mediatorMock.Verify(m => m.Publish(It.IsAny<SaleMadeEvent>(), default), Times.Never);

        // 8. Drawer must reflect the physical cash installment
        var cashTx = await salesContext.CashTransactions.FirstOrDefaultAsync(ct => ct.SaleId == saleDto.Id && ct.AmountUsd == 30.00m);
        Assert.NotNull(cashTx);
        Assert.True(cashTx.IsPhysicalCash);
        Assert.Equal(1500.00m, cashTx.AmountLocal);
    }
}
