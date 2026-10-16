using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Events;
using Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    private const int TestActorId = 42;

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

        var product = new ProductBuilder().WithId(201).WithName("Batería 12V").WithCostAndMargin(80.00m, 25.00m).BuildSaleProductInfo(); // $100.00 USD
        var inventoryServiceMock = new Mock<IInventoryService>();
        inventoryServiceMock.Setup(i => i.GetSaleProductByIdAsync(201)).ReturnsAsync(product);

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

        var holdClaim = await salesContext.Sales.FindAsync(saleDto.Id);
        holdClaim!.ClaimedByUserId = TestActorId;
        holdClaim.ClaimAction = SaleClaimAction.Editing;
        holdClaim.ClaimedByUserName = "Test Actor";
        holdClaim.ClaimedAtUtc = DateTime.UtcNow;
        await salesContext.SaveChangesAsync();

        var secondPaymentRequest = new AddPaymentRequestDto
        {
            PaymentMethodId = 1,
            AmountUSD = 30.00m,
            AmountBsS = 1500.00m,
            ExchangeRate = 50.00m,
            ReferenceNumber = null
        };

        var secondResult = await salesService.AddPaymentToHoldSaleAsync(saleDto.Id, secondPaymentRequest, actingUserId: TestActorId);

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

    // 8.185-T3 (S5): flujo real — venta con política cash efectiva (nearest OFF), abono inicial y
    // cobro del remanente Bs.S EFECTIVO. El remanente USD (0.08) superaba la tolerancia del gate
    // USD-only previo; la cobertura del dominio Bs.S (28.00 efectivo) liquida la venta.
    [Fact]
    public async Task CheckoutFlow_EffectiveCashPolicy_PaysRemainderInBsS_AndCompletesInDualDomain()
    {
        // 1. Setup Data & Services
        var salesContext = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(salesContext);

        var customer = new CustomerBuilder()
            .WithId(70)
            .WithCedula("V-30123456")
            .WithName("Luis Mora")
            .Build();
        salesContext.Customers.Add(customer);
        await salesContext.SaveChangesAsync();

        // Producto de 1.44 USD (1.20 + 20%); a tasa 10 el matemático 14.40 baja al escalón 14.00.
        var product = new ProductBuilder().WithId(220).WithName("Jabón").WithCostAndMargin(1.20m, 20.00m).BuildSaleProductInfo();
        var inventoryServiceMock = new Mock<IInventoryService>();
        inventoryServiceMock.Setup(i => i.GetSaleProductByIdAsync(220)).ReturnsAsync(product);

        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultCashBsRoundingRule))
            .ReturnsAsync("VesCashFriendlyInteger");
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor))
            .ReturnsAsync("false");

        var cashDrawerService = new CashDrawerService(salesContext);
        var salesService = new SalesService(
            salesContext,
            inventoryServiceMock.Object,
            new Mock<IMediator>().Object,
            cashDrawerService,
            settingsMock.Object);

        await cashDrawerService.OpenSessionAsync(1000m, 10.00m);

        // 2. Venta de 2 unidades: TotalUSD 2.88 y TotalBsS efectivo 28.00 (14.00 por línea).
        var saleDto = await salesService.StartSaleAsync();
        var withItem = await salesService.AddItemAsync(saleDto.Id, 220, 2, 10.00m);
        Assert.Equal(2.88m, withItem.TotalUSD);
        Assert.Equal(28.00m, withItem.TotalBsS);

        // 3. Apartado con abono inicial de 0.50 USD (5.00 Bs.S).
        var holdResult = await salesService.HoldSaleAsync(saleDto.Id, new HoldSaleRequestDto
        {
            CustomerId = customer.Id,
            ExchangeRate = 10.00m,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = 1, // Efectivo USD
                AmountUSD = 0.50m,
                AmountBsS = 5.00m,
                ExchangeRate = 10.00m
            }
        });
        Assert.Equal("OnHold", holdResult.Status);

        var holdClaim = await salesContext.Sales.FindAsync(saleDto.Id);
        holdClaim!.ClaimedByUserId = TestActorId;
        holdClaim.ClaimAction = SaleClaimAction.Editing;
        holdClaim.ClaimedByUserName = "Test Actor";
        holdClaim.ClaimedAtUtc = DateTime.UtcNow;
        await salesContext.SaveChangesAsync();

        // Precondición documentada: el pago exacto del efectivo queda 0.08 USD bajo el canónico,
        // por lo que el gate USD-only previo rechazaba el cierre.
        Assert.True(2.88m - Math.Round(28.00m / 10m, 2, MidpointRounding.AwayFromZero) > 0.05m);

        // 4. Cobro del remanente en Bs.S efectivo (23.00 + 5.00 abonado = 28.00).
        await salesService.CompleteSaleAsync(
            saleDto.Id,
            10.00m,
            new List<PaymentInfo> { new PaymentInfo(1, 0m, 23.00m, null) },
            actingUserId: TestActorId);

        // 5. Assertions: snapshots consistentes con el total efectivo y sin vuelto falso.
        var savedSale = await salesContext.Sales
            .AsNoTracking()
            .Include(s => s.Payments)
            .FirstAsync(s => s.Id == saleDto.Id);

        Assert.Equal(SaleStatus.Completed, savedSale.Status);
        Assert.Equal(2.88m, savedSale.TotalUSD);
        Assert.Equal(28.00m, savedSale.TotalBsS);
        Assert.Equal(28.00m, savedSale.FinalPaidAmountBsS);
        Assert.Equal(0m, savedSale.RoundingAdjustment);
        Assert.Equal(2.80m, savedSale.Payments.Sum(p => p.Amount));
        Assert.Equal(28.00m, savedSale.Payments.Sum(p => p.AmountBsS));
        Assert.Empty(await salesContext.CashTransactions
            .Where(t => t.SaleId == saleDto.Id && t.Type == CashTransactionType.Expense)
            .ToListAsync());
    }

    // 8.185-T3 (decisión mantenedor, guard dual): un abono ÚNICO que cubre el total EFECTIVO Bs.S
    // se acepta aunque el dominio USD exceda `total + 0.05` por el escalón de la política cash.
    [Fact]
    public async Task AddPaymentToHoldSale_SingleAbonoCoveringEffectiveTotal_IsAccepted()
    {
        // 1. Setup Data & Services
        var salesContext = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(salesContext);

        var customer = new CustomerBuilder()
            .WithId(71)
            .WithCedula("V-31123456")
            .WithName("Rosa Díaz")
            .Build();
        salesContext.Customers.Add(customer);
        await salesContext.SaveChangesAsync();

        // Producto de 1.04 USD; a tasa 10 con never-down el escalón entero es 11.00 por unidad.
        var product = new ProductBuilder().WithId(221).WithName("Clavo").WithCostAndMargin(0.80m, 30.00m).BuildSaleProductInfo();
        var inventoryServiceMock = new Mock<IInventoryService>();
        inventoryServiceMock.Setup(i => i.GetSaleProductByIdAsync(221)).ReturnsAsync(product);

        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultCashBsRoundingRule))
            .ReturnsAsync("VesCashFriendlyInteger");
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor))
            .ReturnsAsync("true");

        var cashDrawerService = new CashDrawerService(salesContext);
        var salesService = new SalesService(
            salesContext,
            inventoryServiceMock.Object,
            new Mock<IMediator>().Object,
            cashDrawerService,
            settingsMock.Object);

        await cashDrawerService.OpenSessionAsync(1000m, 10.00m);

        var saleDto = await salesService.StartSaleAsync();
        var withItems = await salesService.AddItemAsync(saleDto.Id, 221, 2, 10.00m);
        Assert.Equal(2.08m, withItems.TotalUSD);
        Assert.Equal(22.00m, withItems.TotalBsS);

        await salesService.HoldSaleAsync(saleDto.Id, new HoldSaleRequestDto { CustomerId = customer.Id, ExchangeRate = 10.00m });

        var holdClaim = await salesContext.Sales.FindAsync(saleDto.Id);
        holdClaim!.ClaimedByUserId = TestActorId;
        holdClaim.ClaimAction = SaleClaimAction.Editing;
        holdClaim.ClaimedByUserName = "Test Actor";
        holdClaim.ClaimedAtUtc = DateTime.UtcNow;
        await salesContext.SaveChangesAsync();

        // Precondición documentada: el abono de 22.00 Bs.S acredita 2.20 USD y el guard USD-only
        // previo (2.20 > 2.08 + 0.05) lo rechazaba con "excede el total pendiente".
        Assert.True(2.08m - Math.Round(22.00m / 10m, 2, MidpointRounding.AwayFromZero) < -0.05m);

        var result = await salesService.AddPaymentToHoldSaleAsync(saleDto.Id, new AddPaymentRequestDto
        {
            PaymentMethodId = 2, // Efectivo Bs.S
            AmountUSD = 0m,
            AmountBsS = 22.00m,
            ExchangeRate = 10.00m
        }, actingUserId: TestActorId);

        Assert.Equal(2.20m, result.TotalPaidUSD);
        Assert.Single(result.Payments);
        Assert.Equal(22.00m, result.Payments[0].AmountBsS);

        var saved = await salesContext.Sales.AsNoTracking().Include(s => s.Payments).FirstAsync(s => s.Id == saleDto.Id);
        Assert.Equal(SaleStatus.OnHold, saved.Status);
        Assert.Equal(22.00m, saved.Payments.Sum(p => p.AmountBsS));
    }

    [Fact]
    public async Task AddPayment_RetryWithSameIdempotencyKey_AppliesAbonoOnlyOnce()
    {
        var salesContext = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(salesContext);

        var customer = new CustomerBuilder()
            .WithId(60)
            .WithCedula("V-27123456")
            .WithName("Ana Torres")
            .Build();
        salesContext.Customers.Add(customer);
        await salesContext.SaveChangesAsync();

        var product = new ProductBuilder().WithId(210).WithName("Bombillo LED").WithCostAndMargin(40.00m, 25.00m).BuildSaleProductInfo();
        var inventoryServiceMock = new Mock<IInventoryService>();
        inventoryServiceMock.Setup(i => i.GetSaleProductByIdAsync(210)).ReturnsAsync(product);

        var mediatorMock = new Mock<IMediator>();
        var cashDrawerService = new CashDrawerService(salesContext);
        var settingsMock = new Mock<ISystemSettingsService>();

        var salesService = new SalesService(salesContext, inventoryServiceMock.Object, mediatorMock.Object, cashDrawerService, settingsMock.Object);
        var idService = new IdempotencyService(salesContext);

        var saleDto = await salesService.StartSaleAsync(cashierId: 1);
        await salesService.AddItemAsync(saleDto.Id, 210, 1, 50.00m);

        var holdRequest = new HoldSaleRequestDto
        {
            CustomerId = customer.Id,
            ExchangeRate = 50.00m,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = 3, // Punto de Venta (no cash)
                AmountUSD = 10.00m,
                AmountBsS = 500.00m,
                ExchangeRate = 50.00m
            }
        };
        await salesService.HoldSaleAsync(saleDto.Id, holdRequest);

        var holdClaim = await salesContext.Sales.FindAsync(saleDto.Id);
        holdClaim!.ClaimedByUserId = 1;
        holdClaim.ClaimAction = SaleClaimAction.Editing;
        holdClaim.ClaimedByUserName = "Test Actor";
        holdClaim.ClaimedAtUtc = DateTime.UtcNow;
        await salesContext.SaveChangesAsync();

        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns("1");

        var controller = new SalesController(salesService, mockUser.Object, idService);

        var paymentReq = new AddPaymentRequestDto
        {
            PaymentMethodId = 3,
            AmountUSD = 10.00m,
            AmountBsS = 500.00m,
            ExchangeRate = 50.00m
        };
        const string key = "ABONO-RETRY-INTEGRATION-01";

        // Primer intento: se aplica el abono → MISS
        controller.ControllerContext = CreatePaymentHttpContext(key);
        var first = await controller.AddPaymentAsync(saleDto.Id, paymentReq);
        Assert.IsType<OkObjectResult>(first.Result);
        Assert.Equal("MISS", controller.Response.Headers["X-Cache-Lookup"].ToString());

        // Retry con la misma clave: replica → HIT, sin re-aplicar el abono
        controller.ControllerContext = CreatePaymentHttpContext(key);
        var second = await controller.AddPaymentAsync(saleDto.Id, paymentReq);
        Assert.IsType<ContentResult>(second.Result);
        Assert.Equal("HIT", controller.Response.Headers["X-Cache-Lookup"].ToString());

        var savedSale = await salesContext.Sales
            .AsNoTracking()
            .Include(s => s.Payments)
            .FirstAsync(s => s.Id == saleDto.Id);

        // 1 abono del hold + 1 único abono registrado (el retry NO lo duplicó)
        Assert.Equal(2, savedSale.Payments.Count);
        Assert.Equal(2, savedSale.Payments.Count(p => p.Amount == 10.00m));

        var idempotencyCount = await salesContext.IdempotentRequests.CountAsync(r => r.Key == key && r.RequestPath == $"/api/sales/{saleDto.Id}/payments");
        Assert.Equal(1, idempotencyCount);
    }

    private static ControllerContext CreatePaymentHttpContext(string key)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Idempotency-Key"] = key;
        httpContext.Request.Method = "POST";
        httpContext.Response.Body = new MemoryStream();
        return new ControllerContext { HttpContext = httpContext };
    }
}
