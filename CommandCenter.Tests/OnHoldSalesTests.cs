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
using System;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests;

public partial class OnHoldSalesTests
{
    private const int TestActorId = 42;

    private SalesDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    [Fact]
    public async Task HoldSale_Allows_ExceedingCreditLimit()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Juan Perez", CreditLimitUSD = 50m };
        context.Customers.Add(customer);

        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.Pending };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var request = new HoldSaleRequestDto
        {
            CustomerId = 1,
            ExchangeRate = 40m,
            InitialPayment = null
        };

        // La cuenta abierta se crea exitosamente aun cuando la deuda ($100 USD) supere el límite ($50 USD)
        var result = await service.HoldSaleAsync(1, request);

        Assert.Equal("OnHold", result.Status);
        Assert.Equal(0m, result.TotalPaidUSD);
        Assert.Equal(100m, result.RemainingBalanceUSD);
    }

    [Fact]
    public async Task HoldSale_WithInitialPayment_SucceedsIfUnderLimit()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Juan Perez", CreditLimitUSD = 50m };
        context.Customers.Add(customer);

        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.Pending };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        // Initial payment: 2400 Bs.S at rate 40 = $60 USD. Remaining = $40 USD <= $50 USD limit
        var request = new HoldSaleRequestDto
        {
            CustomerId = 1,
            ExchangeRate = 40m,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = 1,
                AmountBsS = 2400m,
                ExchangeRate = 40m
            }
        };

        var result = await service.HoldSaleAsync(1, request);

        Assert.Equal("OnHold", result.Status);
        Assert.Equal(60m, result.TotalPaidUSD);
        Assert.Equal(40m, result.RemainingBalanceUSD);
    }

    [Fact]
    public async Task HoldSale_InitialPayment_AnchorsClientRateToBcv()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        // Tasa BCV del día = 65. Cliente envía 90 (desvío >10% pero <100%): debe anclarse a 65.
        mockInventory.Setup(i => i.GetTodayExchangeRateAsync()).ReturnsAsync(65m);

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Juan Perez", CreditLimitUSD = 50m };
        context.Customers.Add(customer);

        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.Pending };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var request = new HoldSaleRequestDto
        {
            CustomerId = 1,
            ExchangeRate = 90m,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = 1,
                AmountBsS = 3250m,
                ExchangeRate = 90m
            }
        };

        var result = await service.HoldSaleAsync(1, request);

        Assert.Equal("OnHold", result.Status);
        Assert.Equal(50m, result.TotalPaidUSD);
        Assert.Equal(50m, result.RemainingBalanceUSD);

        var storedPayment = await context.SalePayments.FirstAsync(p => p.SaleId == 1);
        Assert.Equal(65m, storedPayment.ExchangeRate);
        Assert.Equal(50m, storedPayment.Amount);
    }

    [Fact]
    public async Task AddPaymentToHoldSale_ConvertsBsSToUSD_AntiDevaluation()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Juan Perez", CreditLimitUSD = 100m };
        context.Customers.Add(customer);

        var sale = new Sale { Id = 1, CustomerId = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 40m };
        sale.ClaimedByUserId = TestActorId;
        sale.ClaimAction = SaleClaimAction.Editing;
        sale.ClaimedByUserName = "Test Actor";
        sale.ClaimedAtUtc = DateTime.UtcNow;
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var paymentReq = new AddPaymentRequestDto
        {
            PaymentMethodId = 1,
            AmountBsS = 2000m,
            ExchangeRate = 50m
        };

        var result = await service.AddPaymentToHoldSaleAsync(1, paymentReq, actingUserId: TestActorId);

        Assert.Equal(40m, result.TotalPaidUSD);
        Assert.Equal(60m, result.RemainingBalanceUSD);
    }

    [Fact]
    public async Task CompleteSale_PartialPayment_ThrowsInvalidOperationException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockCashDrawer.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1 });

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Juan Perez", CreditLimitUSD = 100m };
        context.Customers.Add(customer);

        var sale = new Sale { Id = 1, CustomerId = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 50m };
        sale.ClaimedByUserId = TestActorId;
        sale.ClaimAction = SaleClaimAction.Editing;
        sale.ClaimedByUserName = "Test Actor";
        sale.ClaimedAtUtc = DateTime.UtcNow;
        context.Sales.Add(sale);
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true });
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var payments = new[] { new Sales.Module.Interfaces.PaymentInfo(1, 40m, 2000m, null) };
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.CompleteSaleAsync(1, 50m, payments, actingUserId: TestActorId));
        Assert.Contains("El flujo de cobro requiere liquidación al 100%", ex.Message);
    }

    [Fact]
    public async Task HoldSale_Rejects_DefaultCustomer()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var defaultCustomer = new Customer { Id = 1, CedulaOrRif = "V-00000000", Name = "CLIENTE GENERAL", IsDefault = true };
        context.Customers.Add(defaultCustomer);

        var sale = new Sale { Id = 1, TotalUSD = 50m, Status = SaleStatus.Pending };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var request = new HoldSaleRequestDto
        {
            CustomerId = 1,
            ExchangeRate = 40m
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.HoldSaleAsync(1, request));
        Assert.Contains("Asigne un cliente distinto al Consumidor Final", ex.Message);
    }

    [Fact]
    public async Task CompleteSale_FullLiquidation_CompletesSaleAndGeneratesInvoice()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockCashDrawer.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1 });

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Juan Perez", CreditLimitUSD = 100m };
        context.Customers.Add(customer);

        var sale = new Sale { Id = 1, CustomerId = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 50m };
        sale.ClaimedByUserId = TestActorId;
        sale.ClaimAction = SaleClaimAction.Editing;
        sale.ClaimedByUserName = "Test Actor";
        sale.ClaimedAtUtc = DateTime.UtcNow;
        sale.Payments.Add(new SalePayment { Amount = 40m, AmountBsS = 2000m, ExchangeRate = 50m });
        context.Sales.Add(sale);
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true });
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var payments = new[] { new Sales.Module.Interfaces.PaymentInfo(1, 60m, 3000m, null) };
        var invoiceNum = await service.CompleteSaleAsync(1, 50m, payments, actingUserId: TestActorId);

        Assert.True(invoiceNum > 0);
        var updatedSale = await service.GetSaleAsync(1);
        Assert.Equal("Completed", updatedSale.Status);
        Assert.Equal(100m, updatedSale.TotalPaidUSD);
        Assert.Equal(0m, updatedSale.RemainingBalanceUSD);
    }

    [Fact]
    public async Task CompleteSale_RecalculatesItemPricesBsSWithNewExchangeRate()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockCashDrawer.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1 });

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Juan Perez" };
        context.Customers.Add(customer);

        // Item with UnitPrice $10 USD. Initially applied rate was 50 (UnitPriceBsS = 500)
        var item = new SaleItem { Id = 1, ProductId = 1, ProductName = "Test Item", Quantity = 2, UnitPrice = 10m, UnitPriceBsS = 500m, Subtotal = 20m, SubtotalBsS = 1000m };
        var sale = new Sale { Id = 1, CustomerId = 1, TotalUSD = 20m, TotalBsS = 1000m, Status = SaleStatus.Pending, AppliedRate = 50m };
        sale.Items.Add(item);
        context.Sales.Add(sale);
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true });
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        // Completing sale with updated rate of 60.00 Bs.S / USD
        var newRate = 60.00m;
        var payments = new[] { new Sales.Module.Interfaces.PaymentInfo(1, 20m, 1200m, null) };
        var invoiceNum = await service.CompleteSaleAsync(1, newRate, payments);

        Assert.True(invoiceNum > 0);
        var detail = await service.GetSaleHistoryDetailAsync(1);
        Assert.Equal(60.00m, detail.AppliedRate);
        Assert.Equal(1200m, detail.TotalBsS); // 2 * $10 * 60 = 1200
        Assert.Single(detail.Items);

        var itemDetail = detail.Items.First();
        Assert.Equal(600m, itemDetail.UnitPriceBsS); // $10 * 60 = 600
        Assert.Equal(1200m, itemDetail.SubtotalBsS); // 2 * 600 = 1200
    }

    [Fact]
    public async Task UpdateSaleItemsAsync_WhenSaleNotOnHold_Throws()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale { Id = 1, TotalUSD = 20m, Status = SaleStatus.Pending };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
        var request = new Sales.Module.DTOs.UpdateSaleItemsRequestDto
        {
            Items = new System.Collections.Generic.List<Sales.Module.DTOs.UpdateSaleItemDto>
            {
                new Sales.Module.DTOs.UpdateSaleItemDto { ProductId = 1, Quantity = 1, UnitPrice = 10m }
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateSaleItemsAsync(1, request));
        Assert.Contains("OnHold", ex.Message);
    }

    [Fact]
    public async Task UpdateSaleItemsAsync_WhenNewTotalLessThanPaid_Throws()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();
        mockInventory.Setup(i => i.GetProductByIdAsync(1)).ReturnsAsync(new Product { Id = 1, Name = "Prod1", PriceUSD = 30m, IsActive = true });

        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.OnHold };
        sale.ClaimedByUserId = TestActorId;
        sale.ClaimAction = SaleClaimAction.Editing;
        sale.ClaimedByUserName = "Test Actor";
        sale.ClaimedAtUtc = DateTime.UtcNow;
        sale.Payments.Add(new SalePayment { Id = 1, Amount = 50m, AmountBsS = 2000m, ExchangeRate = 40m });
        sale.Items.Add(new SaleItem { Id = 1, ProductId = 2, ProductName = "Original Item", Quantity = 1, UnitPrice = 100m, Subtotal = 100m });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
        var request = new Sales.Module.DTOs.UpdateSaleItemsRequestDto
        {
            Items = new System.Collections.Generic.List<Sales.Module.DTOs.UpdateSaleItemDto>
            {
                new Sales.Module.DTOs.UpdateSaleItemDto { ProductId = 1, Quantity = 1, UnitPrice = 30m }
            }
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateSaleItemsAsync(1, request, actingUserId: TestActorId));
        Assert.Contains("no puede ser menor al monto total ya abonado", ex.Message);

        // Salvaguarda financiera: la venta NO fue modificada (total, abonos e ítems intactos)
        Assert.Equal(100m, sale.TotalUSD);
        Assert.Equal(50m, sale.Payments.Sum(p => p.Amount));
        Assert.Single(sale.Items);
        Assert.Equal("Original Item", sale.Items[0].ProductName);
    }

    [Fact]
    public async Task UpdateSaleItemsAsync_Allows_ExceedingCreditLimit()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockInventory.Setup(i => i.GetProductByIdAsync(1)).ReturnsAsync(new Product { Id = 1, Name = "Prod1", PriceUSD = 100m, IsActive = true });

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Juan Perez", CreditLimitUSD = 50m };
        context.Customers.Add(customer);

        var sale = new Sale { Id = 1, CustomerId = 1, TotalUSD = 60m, Status = SaleStatus.OnHold };
        sale.ClaimedByUserId = TestActorId;
        sale.ClaimAction = SaleClaimAction.Editing;
        sale.ClaimedByUserName = "Test Actor";
        sale.ClaimedAtUtc = DateTime.UtcNow;
        sale.Payments.Add(new SalePayment { Id = 1, Amount = 20m, AmountBsS = 800m, ExchangeRate = 40m });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
        var request = new Sales.Module.DTOs.UpdateSaleItemsRequestDto
        {
            Items = new System.Collections.Generic.List<Sales.Module.DTOs.UpdateSaleItemDto>
            {
                new Sales.Module.DTOs.UpdateSaleItemDto { ProductId = 1, Quantity = 1, UnitPrice = 100m }
            }
        };

        var updatedSale = await service.UpdateSaleItemsAsync(1, request, actingUserId: TestActorId);

        Assert.Equal("OnHold", updatedSale.Status);
        Assert.Equal(100m, updatedSale.TotalUSD);
        Assert.Equal(20m, updatedSale.TotalPaidUSD);
        Assert.Equal(80m, updatedSale.RemainingBalanceUSD);
    }

    [Fact]
    public async Task UpdateSaleItemsAsync_ValidEdit_UpdatesItemsAndRecalculates()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockInventory.Setup(i => i.GetProductByIdAsync(100)).ReturnsAsync(new Product { Id = 100, Name = "Prod100", PriceUSD = 2m, IsActive = true });
        mockInventory.Setup(i => i.GetProductByIdAsync(101)).ReturnsAsync(new Product { Id = 101, Name = "Prod101", PriceUSD = 10m, IsActive = true });

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Juan Perez", CreditLimitUSD = 100m };
        context.Customers.Add(customer);

        var sale = new Sale { Id = 1, CustomerId = 1, TotalUSD = 10m, TotalBsS = 400m, Subtotal = 10m, SubtotalBsS = 400m, Status = SaleStatus.OnHold, AppliedRate = 40m, CashierId = TestActorId };
        sale.Items.Add(new SaleItem { ProductId = 100, ProductName = "Prod100", Quantity = 5m, UnitPrice = 2m, Subtotal = 10m, UnitPriceBsS = 80m, SubtotalBsS = 400m });
        
        sale.ClaimedByUserId = TestActorId;
        sale.ClaimAction = SaleClaimAction.Editing;
        sale.ClaimedByUserName = "Test Actor";
        sale.ClaimedAtUtc = DateTime.UtcNow;
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var request = new Sales.Module.DTOs.UpdateSaleItemsRequestDto
        {
            Items = new System.Collections.Generic.List<Sales.Module.DTOs.UpdateSaleItemDto>
            {
                new Sales.Module.DTOs.UpdateSaleItemDto { ProductId = 101, Quantity = 2m }
            }
        };

        var updatedSale = await service.UpdateSaleItemsAsync(1, request, actingUserId: TestActorId);

        Assert.Equal("OnHold", updatedSale.Status);
        Assert.Single(updatedSale.Items);
        Assert.Equal(101, updatedSale.Items[0].ProductId);
        Assert.Equal(20m, updatedSale.TotalUSD);
        Assert.Equal(800m, updatedSale.TotalBsS);
    }

    [Fact]
    public async Task UpdateSaleItemsAsync_WhenProductNotFound_Throws()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockInventory.Setup(i => i.GetProductByIdAsync(100)).ReturnsAsync((Product?)null);

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Juan Perez", CreditLimitUSD = 100m };
        context.Customers.Add(customer);

        var sale = new Sale { Id = 1, CustomerId = 1, TotalUSD = 10m, TotalBsS = 400m, Subtotal = 10m, SubtotalBsS = 400m, Status = SaleStatus.OnHold, AppliedRate = 40m, CashierId = TestActorId };
        sale.Items.Add(new SaleItem { ProductId = 99, ProductName = "Prod99", Quantity = 5m, UnitPrice = 2m, Subtotal = 10m, UnitPriceBsS = 80m, SubtotalBsS = 400m });
        
        sale.ClaimedByUserId = TestActorId;
        sale.ClaimAction = SaleClaimAction.Editing;
        sale.ClaimedByUserName = "Test Actor";
        sale.ClaimedAtUtc = DateTime.UtcNow;
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var request = new Sales.Module.DTOs.UpdateSaleItemsRequestDto
        {
            Items = new System.Collections.Generic.List<Sales.Module.DTOs.UpdateSaleItemDto>
            {
                new Sales.Module.DTOs.UpdateSaleItemDto { ProductId = 100, Quantity = 2m, UnitPrice = 999m }
            }
        };

        var ex = await Assert.ThrowsAsync<System.Collections.Generic.KeyNotFoundException>(() => service.UpdateSaleItemsAsync(1, request, actingUserId: TestActorId));
        Assert.Contains("no existe", ex.Message);
    }

    [Fact]
    public async Task LiquidateOnHoldSale_WithPendingPickup_SetsPendingPickupStatusAndDeductsStock()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockCashDrawer.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1 });

        var realCustomer = new Customer { Id = 5, CedulaOrRif = "V-99999999", Name = "Maria Gomez", IsDefault = false };
        context.Customers.Add(realCustomer);

        var sale = new Sale { Id = 1, TotalUSD = 100m, AppliedRate = 40m, Status = SaleStatus.OnHold, CustomerId = 5 };
        sale.ClaimedByUserId = TestActorId;
        sale.ClaimAction = SaleClaimAction.Editing;
        sale.ClaimedByUserName = "Test Actor";
        sale.ClaimedAtUtc = DateTime.UtcNow;
        sale.Payments.Add(new SalePayment { Id = 1, Amount = 40m, AmountBsS = 1600m, ExchangeRate = 40m });
        sale.Items.Add(new SaleItem { Id = 1, ProductId = 10, ProductName = "Harina", Quantity = 2, UnitPrice = 50m, Subtotal = 100m });
        context.Sales.Add(sale);
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true });
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var payments = new System.Collections.Generic.List<PaymentInfo>
        {
            new PaymentInfo(1, 60m, 2400m, "REF-100")
        };

        int invoiceNumber = await service.CompleteSaleAsync(1, 40m, payments, 0m, 1, isPendingPickup: true, actingUserId: TestActorId);

        Assert.True(invoiceNumber > 0);
        var completedSale = await context.Sales.FindAsync(1);
        Assert.NotNull(completedSale);
        Assert.Equal(SaleStatus.Completed, completedSale.Status);
        Assert.Equal(SaleDeliveryStatus.PendingPickup, completedSale.DeliveryStatus);

        // Verifica que se publicó el evento para descontar inventario
        mockMediator.Verify(m => m.Publish(It.Is<Core.Events.SaleMadeEvent>(e => e.SaleId == 1), default), Times.Once);
    }

}
