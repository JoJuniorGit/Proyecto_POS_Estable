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
using System;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests;

public class OnHoldSalesTests
{
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
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        // Abono de 2000 Bs.S a tasa 50 (devaluación ocurrió de 40 -> 50) => $40 USD abonados
        var paymentReq = new AddPaymentRequestDto
        {
            PaymentMethodId = 1,
            AmountBsS = 2000m,
            ExchangeRate = 50m
        };

        var result = await service.AddPaymentToHoldSaleAsync(1, paymentReq);

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
            .ReturnsAsync(new CashDrawerSession { Id = 1 });

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Juan Perez", CreditLimitUSD = 100m };
        context.Customers.Add(customer);

        var sale = new Sale { Id = 1, CustomerId = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 50m };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        // Abono parcial de $40 USD vía CompleteSaleAsync debe lanzar excepción
        var payments = new[] { new Sales.Module.Interfaces.PaymentInfo(1, 40m, 2000m, null) };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteSaleAsync(1, 50m, payments));
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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.HoldSaleAsync(1, request));
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
            .ReturnsAsync(new CashDrawerSession { Id = 1 });

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Juan Perez", CreditLimitUSD = 100m };
        context.Customers.Add(customer);

        // Sale has $40 USD already paid, $60 USD remaining out of $100 USD
        var sale = new Sale { Id = 1, CustomerId = 1, TotalUSD = 100m, Status = SaleStatus.OnHold, AppliedRate = 50m };
        sale.Payments.Add(new SalePayment { Amount = 40m, AmountBsS = 2000m, ExchangeRate = 50m });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        // Paying remaining $60 USD
        var payments = new[] { new Sales.Module.Interfaces.PaymentInfo(1, 60m, 3000m, null) };
        var invoiceNum = await service.CompleteSaleAsync(1, 50m, payments);

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
            .ReturnsAsync(new CashDrawerSession { Id = 1 });

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Juan Perez" };
        context.Customers.Add(customer);

        // Item with UnitPrice $10 USD. Initially applied rate was 50 (UnitPriceBsS = 500)
        var item = new SaleItem { Id = 1, ProductId = 1, ProductName = "Test Item", Quantity = 2, UnitPrice = 10m, UnitPriceBsS = 500m, Subtotal = 20m, SubtotalBsS = 1000m };
        var sale = new Sale { Id = 1, CustomerId = 1, TotalUSD = 20m, TotalBsS = 1000m, Status = SaleStatus.Pending, AppliedRate = 50m };
        sale.Items.Add(item);
        context.Sales.Add(sale);
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

        var sale = new Sale { Id = 1, TotalUSD = 100m, Status = SaleStatus.OnHold };
        sale.Payments.Add(new SalePayment { Id = 1, Amount = 50m, AmountBsS = 2000m, ExchangeRate = 40m }); // Abonado = $50 USD
        sale.Items.Add(new SaleItem { Id = 1, ProductId = 2, ProductName = "Original Item", Quantity = 1, UnitPrice = 100m, Subtotal = 100m });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
        var request = new Sales.Module.DTOs.UpdateSaleItemsRequestDto
        {
            Items = new System.Collections.Generic.List<Sales.Module.DTOs.UpdateSaleItemDto>
            {
                new Sales.Module.DTOs.UpdateSaleItemDto { ProductId = 1, Quantity = 1, UnitPrice = 30m } // Nuevo Total = $30 USD < $50 USD Abonados
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateSaleItemsAsync(1, request));
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

        var customer = new Customer { Id = 1, CedulaOrRif = "V-12345678", Name = "Juan Perez", CreditLimitUSD = 50m };
        context.Customers.Add(customer);

        var sale = new Sale { Id = 1, CustomerId = 1, TotalUSD = 60m, Status = SaleStatus.OnHold };
        sale.Payments.Add(new SalePayment { Id = 1, Amount = 20m, AmountBsS = 800m, ExchangeRate = 40m }); // Abonado = $20 USD
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
        var request = new Sales.Module.DTOs.UpdateSaleItemsRequestDto
        {
            Items = new System.Collections.Generic.List<Sales.Module.DTOs.UpdateSaleItemDto>
            {
                new Sales.Module.DTOs.UpdateSaleItemDto { ProductId = 1, Quantity = 1, UnitPrice = 100m } // Nuevo Total = $100 USD. Nuevo saldo = $80 USD > $50 USD Límite
            }
        };

        // Se permite actualizar los ítems aun cuando el nuevo saldo pendiente supere el límite de crédito
        var updatedSale = await service.UpdateSaleItemsAsync(1, request);

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

        mockInventory.Setup(i => i.GetProductByIdAsync(10))
            .ReturnsAsync(new Product { Id = 10, Name = "Laptop Lenovo", PriceUSD = 500m });

        var sale = new Sale { Id = 1, TotalUSD = 300m, AppliedRate = 40m, Status = SaleStatus.OnHold };
        sale.Payments.Add(new SalePayment { Id = 1, Amount = 100m, AmountBsS = 4000m, ExchangeRate = 40m }); // Paid = $100 USD
        sale.Items.Add(new SaleItem { Id = 1, ProductId = 2, ProductName = "Old Item", Quantity = 1, UnitPrice = 300m, Subtotal = 300m });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
        var request = new Sales.Module.DTOs.UpdateSaleItemsRequestDto
        {
            Items = new System.Collections.Generic.List<Sales.Module.DTOs.UpdateSaleItemDto>
            {
                new Sales.Module.DTOs.UpdateSaleItemDto { ProductId = 10, Quantity = 1, UnitPrice = 500m } // New Total = $500 USD
            }
        };

        var updatedSale = await service.UpdateSaleItemsAsync(1, request);

        Assert.Equal("OnHold", updatedSale.Status);
        Assert.Equal(500m, updatedSale.TotalUSD);
        Assert.Equal(100m, updatedSale.TotalPaidUSD);
        Assert.Equal(400m, updatedSale.RemainingBalanceUSD);
        Assert.Single(updatedSale.Items);
        Assert.Equal("Laptop Lenovo", updatedSale.Items[0].ProductName);
    }

    [Fact]
    public async Task LiquidateOnHoldSale_WithPendingPickup_SetsPendingPickupStatusAndDeductsStock()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var realCustomer = new Customer { Id = 5, CedulaOrRif = "V-99999999", Name = "Maria Gomez", IsDefault = false };
        context.Customers.Add(realCustomer);

        var sale = new Sale { Id = 1, TotalUSD = 100m, AppliedRate = 40m, Status = SaleStatus.OnHold, CustomerId = 5 };
        sale.Payments.Add(new SalePayment { Id = 1, Amount = 40m, AmountBsS = 1600m, ExchangeRate = 40m }); // Abono previo = $40 USD
        sale.Items.Add(new SaleItem { Id = 1, ProductId = 10, ProductName = "Harina", Quantity = 2, UnitPrice = 50m, Subtotal = 100m });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        // Pago restante de $60 USD
        var payments = new System.Collections.Generic.List<PaymentInfo>
        {
            new PaymentInfo(1, 60m, 2400m, "REF-100")
        };

        int invoiceNumber = await service.CompleteSaleAsync(1, 40m, payments, 0m, 1, isPendingPickup: true);

        Assert.True(invoiceNumber > 0);
        var completedSale = await context.Sales.FindAsync(1);
        Assert.NotNull(completedSale);
        Assert.Equal(SaleStatus.Completed, completedSale.Status);
        Assert.Equal(SaleDeliveryStatus.PendingPickup, completedSale.DeliveryStatus);

        // Verifica que se publicó el evento para descontar inventario
        mockMediator.Verify(m => m.Publish(It.Is<Core.Events.SaleMadeEvent>(e => e.SaleId == 1), default), Times.Once);
    }

    [Fact]
    public async Task LiquidateOnHoldSale_WithPendingPickup_RejectsDefaultCustomer()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var defaultCustomer = new Customer { Id = 1, CedulaOrRif = "V-00000000", Name = "Consumidor Final", IsDefault = true };
        context.Customers.Add(defaultCustomer);

        var sale = new Sale { Id = 1, TotalUSD = 100m, AppliedRate = 40m, Status = SaleStatus.OnHold, CustomerId = 1 };
        sale.Payments.Add(new SalePayment { Id = 1, Amount = 100m, AmountBsS = 4000m, ExchangeRate = 40m });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteSaleAsync(1, 40m, System.Linq.Enumerable.Empty<PaymentInfo>(), 0m, 1, isPendingPickup: true));
        Assert.Contains("se requiere seleccionar o crear un cliente real", ex.Message);
    }

    [Fact]
    public async Task RecalculateOnHoldSalesAsync_UpdatesAppliedRateAndBsSTotals_ForOnHoldSalesOnly()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var onHoldSale = new Sale
        {
            Id = 1,
            Status = SaleStatus.OnHold,
            AppliedRate = 50m,
            TotalUSD = 100m,
            TotalBsS = 5000m,
            Items = new System.Collections.Generic.List<SaleItem>
            {
                new SaleItem { Id = 10, ProductId = 1, ProductName = "Product A", Quantity = 2m, UnitPrice = 50m, Subtotal = 100m, UnitPriceBsS = 2500m, SubtotalBsS = 5000m }
            }
        };

        var completedSale = new Sale
        {
            Id = 2,
            Status = SaleStatus.Completed,
            AppliedRate = 50m,
            TotalUSD = 100m,
            TotalBsS = 5000m
        };

        context.Sales.AddRange(onHoldSale, completedSale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        // Recalculate with new rate = 60m
        int count = await service.RecalculateOnHoldSalesAsync(60m);

        Assert.Equal(1, count);

        var updatedOnHold = await context.Sales.Include(s => s.Items).FirstOrDefaultAsync(s => s.Id == 1);
        Assert.NotNull(updatedOnHold);
        Assert.Equal(60m, updatedOnHold.AppliedRate);
        Assert.Equal(6000m, updatedOnHold.TotalBsS);
        Assert.Equal(3000m, updatedOnHold.Items[0].UnitPriceBsS);
        Assert.Equal(6000m, updatedOnHold.Items[0].SubtotalBsS);

        var updatedCompleted = await context.Sales.FindAsync(2);
        Assert.NotNull(updatedCompleted);
        Assert.Equal(50m, updatedCompleted.AppliedRate);
        Assert.Equal(5000m, updatedCompleted.TotalBsS);
    }

    [Fact]
    public async Task RecalculateOnHoldSalesAsync_PreservesExistingPayments()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var onHoldSale = new Sale
        {
            Id = 1,
            Status = SaleStatus.OnHold,
            AppliedRate = 50m,
            TotalUSD = 100m,
            TotalBsS = 5000m,
            Payments = new System.Collections.Generic.List<SalePayment>
            {
                new SalePayment { Id = 1, Amount = 20m, AmountBsS = 1000m, ExchangeRate = 50m }
            }
        };

        context.Sales.Add(onHoldSale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        await service.RecalculateOnHoldSalesAsync(60m);

        var updatedSale = await context.Sales.Include(s => s.Payments).FirstOrDefaultAsync(s => s.Id == 1);
        Assert.NotNull(updatedSale);
        Assert.Equal(60m, updatedSale.AppliedRate);
        Assert.Equal(6000m, updatedSale.TotalBsS);

        // Previous payment retains its original rate and amounts
        var payment = updatedSale.Payments[0];
        Assert.Equal(20m, payment.Amount);
        Assert.Equal(1000m, payment.AmountBsS);
        Assert.Equal(50m, payment.ExchangeRate);
    }

    [Fact]
    public async Task GetPendingSalesAsync_AutoRecalculatesOutdatedOnHoldSalesWithTodayExchangeRate()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockInventory.Setup(i => i.GetTodayExchangeRateAsync()).ReturnsAsync(65m);

        var oldOnHoldSale = new Sale
        {
            Id = 1,
            Status = SaleStatus.OnHold,
            AppliedRate = 50m,
            TotalUSD = 100m,
            TotalBsS = 5000m
        };

        context.Sales.Add(oldOnHoldSale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var pendingSales = (await service.GetPendingSalesAsync()).ToList();

        Assert.Single(pendingSales);
        Assert.Equal(65m, pendingSales[0].AppliedRate);
        Assert.Equal(6500m, pendingSales[0].TotalBsS);
    }

    [Fact]
    public async Task AddItemAsync_AllowsDecimalQuantityForUnitProducts()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockInventory.Setup(i => i.GetProductByIdAsync(10)).ReturnsAsync(new Product
        {
            Id = 10,
            Name = "Acondicionador Drene Brillo 200ml",
            PriceUSD = 15.69m / 60m,
            IsFractional = false
        });

        var sale = new Sale { Id = 1, Status = SaleStatus.Pending };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var updatedSale = await service.AddItemAsync(1, 10, 1.5m, 60m);

        Assert.NotNull(updatedSale);
        Assert.Single(updatedSale.Items);
        Assert.Equal(1.5m, updatedSale.Items[0].Quantity);
    }

    [Fact]
    public async Task HoldSaleAsync_SmallAmountFractionalProduct_StaysOnHoldWhenUnpaid()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var customer = new Customer { Id = 5, Name = "Carlos Sanchez", CedulaOrRif = "V-20111222" };
        context.Customers.Add(customer);

        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.Pending,
            TotalUSD = 0.03m,
            AppliedRate = 784.67m,
            TotalBsS = 20.87m
        };
        sale.Items.Add(new SaleItem
        {
            Id = 1,
            ProductId = 10,
            ProductName = "Acondicionador Drene Brillo 200ml",
            Quantity = 1.33m,
            UnitPrice = 0.026m,
            Subtotal = 0.03m,
            UnitPriceBsS = 15.69m,
            SubtotalBsS = 20.87m
        });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var heldSale = await service.HoldSaleAsync(1, new HoldSaleRequestDto
        {
            CustomerId = 5,
            ExchangeRate = 784.67m
        });

        Assert.Equal("OnHold", heldSale.Status);
        Assert.Null(heldSale.InvoiceNumber);

        var pendingSales = (await service.GetPendingSalesAsync()).ToList();
        Assert.Single(pendingSales);
        Assert.Equal(1, pendingSales[0].Id);
        Assert.Single(pendingSales[0].Items);
        Assert.Equal(1.33m, pendingSales[0].Items[0].Quantity);
    }

    [Fact]
    public async Task CancelSaleAsync_WithoutPayments_ChangesStatusToCancelled()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale { Id = 1, TotalUSD = 50m, Status = SaleStatus.OnHold, DeliveryStatus = SaleDeliveryStatus.PendingPickup };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        await service.CancelSaleAsync(1);

        var updatedSale = await context.Sales.FindAsync(1);
        Assert.NotNull(updatedSale);
        Assert.Equal(SaleStatus.Cancelled, updatedSale.Status);
    }

    [Fact]
    public async Task CancelSaleAsync_WithPayments_ThrowsInvalidOperationException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var paymentMethod = new PaymentMethod { Id = 1, Name = "Efectivo USD", IsActive = true };
        context.PaymentMethods.Add(paymentMethod);

        var sale = new Sale { Id = 1, TotalUSD = 50m, Status = SaleStatus.OnHold, DeliveryStatus = SaleDeliveryStatus.PendingPickup };
        sale.Payments.Add(new SalePayment { Id = 1, SaleId = 1, PaymentMethodId = 1, Amount = 10m, AmountBsS = 7800m });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelSaleAsync(1));
        Assert.Contains("abonos acumulados", ex.Message);
    }

    [Fact]
    public async Task CancelSaleAsync_DeliveredOrder_ThrowsInvalidOperationException()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var sale = new Sale { Id = 1, TotalUSD = 50m, Status = SaleStatus.OnHold, DeliveryStatus = SaleDeliveryStatus.Delivered };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelSaleAsync(1));
        Assert.Contains("entregado al cliente", ex.Message);
    }

    [Fact]
    public async Task GetPendingSalesAsync_ExcludesCancelledSales()
    {
        using var context = GetInMemoryDbContext();
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        var saleOnHold = new Sale { Id = 1, TotalUSD = 50m, Status = SaleStatus.OnHold };
        var saleCancelled = new Sale { Id = 2, TotalUSD = 30m, Status = SaleStatus.Cancelled };
        context.Sales.AddRange(saleOnHold, saleCancelled);
        await context.SaveChangesAsync();

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);

        var pendingSales = (await service.GetPendingSalesAsync()).ToList();

        Assert.Single(pendingSales);
        Assert.Equal(1, pendingSales[0].Id);
    }
}


