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

public class LiquidationFlowIntegrationTests
{
    [Fact]
    public async Task LiquidationFlow_FinalizesOnHoldSale_AssignsInvoice_AndDeductsInventoryExactlyOnce()
    {
        // 1. Setup Data & Services
        var salesContext = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(salesContext);

        var customer = new CustomerBuilder()
            .WithId(60)
            .WithCedula("V-18987654")
            .WithName("Alejandro Sanz")
            .Build();
        salesContext.Customers.Add(customer);
        await salesContext.SaveChangesAsync();

        var product = new ProductBuilder().WithId(301).WithName("Aceite 20W50").WithCostAndMargin(10.00m, 50.00m).Build(); // $15.00 USD
        var inventoryServiceMock = new Mock<IInventoryService>();
        inventoryServiceMock.Setup(i => i.GetProductByIdAsync(301)).ReturnsAsync(product);

        var mediatorMock = new Mock<IMediator>();
        var cashDrawerService = new CashDrawerService(salesContext);
        var settingsMock = new Mock<ISystemSettingsService>();

        var salesService = new SalesService(salesContext, inventoryServiceMock.Object, mediatorMock.Object, cashDrawerService, settingsMock.Object);

        // 2. Open Drawer Session
        await cashDrawerService.OpenSessionAsync(1000m, 50.00m);

        // 3. Create OnHold Sale of 4 bottles ($60.00 USD total) with $20.00 partial payment
        var saleDto = await salesService.StartSaleAsync();
        await salesService.AddItemAsync(saleDto.Id, 301, 4, 50.00m);

        var holdRequest = new HoldSaleRequestDto
        {
            CustomerId = customer.Id,
            ExchangeRate = 50.00m,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = 1,
                AmountUSD = 20.00m,
                AmountBsS = 1000.00m,
                ExchangeRate = 50.00m
            }
        };
        await salesService.HoldSaleAsync(saleDto.Id, holdRequest);

        // Verify initial hold state
        var holdSale = await salesContext.Sales.FindAsync(saleDto.Id);
        Assert.Equal(SaleStatus.OnHold, holdSale!.Status);
        mediatorMock.Verify(m => m.Publish(It.IsAny<SaleMadeEvent>(), default), Times.Never);

        // 4. Liquidate remaining $40.00 USD (Punto de Venta $40.00 = 2000 Bs.S)
        var finalPayments = new List<PaymentInfo>
        {
            new PaymentInfo(3, 40.00m, 2000.00m, "REF-POS-LIQUIDATE")
        };

        int invoiceNumber = await salesService.CompleteSaleAsync(saleDto.Id, 50.00m, finalPayments);

        // 5. Assertions
        var finalizedSale = await salesContext.Sales.Include(s => s.Payments).FirstAsync(s => s.Id == saleDto.Id);
        Assert.Equal(SaleStatus.Completed, finalizedSale.Status);
        Assert.Equal(invoiceNumber, finalizedSale.InvoiceNumber);
        Assert.True(finalizedSale.InvoiceNumber > 0);
        Assert.Equal(2, finalizedSale.Payments.Count);

        decimal totalPaid = finalizedSale.Payments.Sum(p => p.Amount);
        Assert.Equal(60.00m, totalPaid);
        Assert.Equal(60.00m, finalizedSale.TotalUSD);

        // 6. Assert Stock Deduction occurred exactly once upon 100% liquidation
        mediatorMock.Verify(m => m.Publish(It.Is<SaleMadeEvent>(e => e.SaleId == saleDto.Id), default), Times.Once);
    }
}
