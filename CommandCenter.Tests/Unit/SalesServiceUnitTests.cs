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

namespace CommandCenter.Tests.Unit;

public class SalesServiceUnitTests
{
    private (SalesService service, SalesDbContext context, Mock<IInventoryService> inventoryMock, Mock<IMediator> mediatorMock, Mock<ICashDrawerService> cashDrawerMock) CreateService()
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        var inventoryMock = new Mock<IInventoryService>();
        var mediatorMock = new Mock<IMediator>();
        var cashDrawerMock = new Mock<ICashDrawerService>();
        var settingsMock = new Mock<ISystemSettingsService>();

        cashDrawerMock
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSession { Id = 1, Status = CashDrawerStatus.Open });

        var service = new SalesService(context, inventoryMock.Object, mediatorMock.Object, cashDrawerMock.Object, settingsMock.Object);
        return (service, context, inventoryMock, mediatorMock, cashDrawerMock);
    }

    [Fact]
    public async Task StartSaleAsync_AssignsDefaultCustomer_AndPendingStatus()
    {
        var (service, context, _, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var sale = await service.StartSaleAsync(5);

        Assert.NotNull(sale);
        Assert.True(sale.Id > 0);
        Assert.Equal("Pending", sale.Status);
        Assert.Equal("Consumidor Final", sale.CustomerName);
    }

    [Fact]
    public async Task AddItemAsync_CalculatesSubtotalAndTotalsCorrectly()
    {
        var (service, context, inventoryMock, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var product = new ProductBuilder().WithId(10).WithSku("SKU-10").WithName("Harina").WithCostAndMargin(2.00m, 25.00m).Build();
        inventoryMock.Setup(i => i.GetProductByIdAsync(10)).ReturnsAsync(product);

        var sale = await service.StartSaleAsync();
        var updated = await service.AddItemAsync(sale.Id, 10, 3, 50.00m);

        Assert.Single(updated.Items);
        var item = updated.Items.First();
        Assert.Equal(3, item.Quantity);
        Assert.Equal(product.PriceRetailUSD, item.UnitPrice);
        Assert.Equal(Math.Round(3 * product.PriceRetailUSD, 2, MidpointRounding.AwayFromZero), updated.TotalUSD);
        Assert.Equal(Math.Round(updated.TotalUSD * 50.00m, 2, MidpointRounding.AwayFromZero), updated.TotalBsS);
    }

    [Fact]
    public async Task RemoveItemAsync_RecalculatesTotals_ToZeroWhenEmpty()
    {
        var (service, context, inventoryMock, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var product = new ProductBuilder().WithId(11).WithCostAndMargin(10m, 20m).Build();
        inventoryMock.Setup(i => i.GetProductByIdAsync(11)).ReturnsAsync(product);

        var sale = await service.StartSaleAsync();
        var addedSale = await service.AddItemAsync(sale.Id, 11, 2, 50m);
        int itemId = addedSale.Items.First().Id;

        var result = await service.RemoveItemAsync(sale.Id, itemId, 50m);

        Assert.Empty(result.Items);
        Assert.Equal(0m, result.TotalUSD);
        Assert.Equal(0m, result.TotalBsS);
    }

    [Fact]
    public async Task UpdateItemQuantityAsync_UpdatesSubtotalsAndGrandTotals()
    {
        var (service, context, inventoryMock, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var product = new ProductBuilder().WithId(12).WithCostAndMargin(5m, 20m).Build();
        inventoryMock.Setup(i => i.GetProductByIdAsync(12)).ReturnsAsync(product);

        var sale = await service.StartSaleAsync();
        await service.AddItemAsync(sale.Id, 12, 1, 50m);

        var saleEntity = await context.Sales.Include(s => s.Items).FirstAsync(s => s.Id == sale.Id);
        int itemId = saleEntity.Items.First().Id;

        var result = await service.UpdateItemQuantityAsync(sale.Id, itemId, 4, 50m);

        var item = result.Items.First();
        Assert.Equal(4, item.Quantity);
        Assert.Equal(Math.Round(4 * product.PriceRetailUSD, 2, MidpointRounding.AwayFromZero), result.TotalUSD);
    }

    [Fact]
    public async Task UpdatePriceListAsync_SwitchesBetweenRetailAndWholesale()
    {
        var (service, context, inventoryMock, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var product = new ProductBuilder()
            .WithId(13)
            .WithCostAndMargin(10m, 30m)
            .WithWholesale(minQty: 6m, wholesaleMargin: 10m)
            .Build();

        inventoryMock.Setup(i => i.GetProductByIdAsync(13)).ReturnsAsync(product);
        inventoryMock.Setup(i => i.GetProductsByIdsAsync(It.IsAny<IEnumerable<int>>())).ReturnsAsync(new List<Product> { product });

        var sale = await service.StartSaleAsync();
        await service.AddItemAsync(sale.Id, 13, 10, 50m);

        // Cambiar a precio mayorista
        var wholesaleSale = await service.UpdatePriceListAsync(sale.Id, "Wholesale");
        var wholesaleItem = wholesaleSale.Items.First();
        Assert.Equal(product.PriceWholesaleUSD, wholesaleItem.UnitPrice);

        // Cambiar de vuelta a minorista
        var retailSale = await service.UpdatePriceListAsync(sale.Id, "Retail");
        var retailItem = retailSale.Items.First();
        Assert.Equal(product.PriceRetailUSD, retailItem.UnitPrice);
    }

    [Fact]
    public async Task CancelSaleAsync_SetsStatusToCancelled()
    {
        var (service, context, _, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var sale = await service.StartSaleAsync();
        var saleEntity = await context.Sales.FindAsync(sale.Id);
        saleEntity!.DeliveryStatus = SaleDeliveryStatus.PendingPickup;
        await context.SaveChangesAsync();

        await service.CancelSaleAsync(sale.Id);

        var saved = await context.Sales.FindAsync(sale.Id);
        Assert.NotNull(saved);
        Assert.Equal(SaleStatus.Cancelled, saved.Status);
    }

    [Fact]
    public async Task CompleteSaleAsync_WithOverpayment_CompletesSaleAndLogsInfo()
    {
        var (service, context, _, mediatorMock, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var sale = new SaleBuilder().WithId(20).WithAppliedRate(50m).WithItem(1, "Prod", 2, 25m).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        // Total USD = 50. Pago = 60 USD (sobrepago de 10 USD)
        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 60m, 3000m, null)
        };

        int invoiceNum = await service.CompleteSaleAsync(20, 50m, payments);

        var saved = await context.Sales.FindAsync(20);
        Assert.NotNull(saved);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.True(invoiceNum > 0);
        mediatorMock.Verify(m => m.Publish(It.IsAny<SaleMadeEvent>(), default), Times.Once);
    }

    [Fact]
    public async Task CompleteSaleAsync_WithZeroAppliedRate_ThrowsInvalidOperationException()
    {
        var (service, context, _, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var sale = new SaleBuilder().WithId(21).WithItem(1, "Prod", 1, 10m).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo> { new PaymentInfo(1, 10m, 500m, null) };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteSaleAsync(21, 0m, payments));
        Assert.Contains("Tasa de cambio AppliedRate inválida", ex.Message);
    }

    [Fact]
    public async Task HoldSaleAsync_WithIdentifiedCustomer_SetsOnHold()
    {
        var (service, context, _, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var customer = new CustomerBuilder().WithId(100).WithCedula("V-99999999").WithName("Pedro Perez").Build();
        context.Customers.Add(customer);

        var sale = new SaleBuilder().WithId(30).WithAppliedRate(50m).WithItem(1, "Item", 2, 20m).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var request = new HoldSaleRequestDto
        {
            CustomerId = customer.Id,
            ExchangeRate = 50m
        };

        var result = await service.HoldSaleAsync(30, request);

        Assert.Equal("OnHold", result.Status);
        Assert.Equal(customer.Name, result.CustomerName);
    }

    [Fact]
    public async Task HoldSaleAsync_WithDefaultCustomer_ThrowsInvalidOperationException()
    {
        var (service, context, _, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var sale = new SaleBuilder().WithId(31).WithItem(1, "Item", 1, 10m).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var request = new HoldSaleRequestDto
        {
            CustomerId = 1, // Consumidor Final
            ExchangeRate = 50m
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.HoldSaleAsync(31, request));
        Assert.Contains("Las ventas en espera requieren un cliente real identificable", ex.Message);
    }

    [Fact]
    public async Task RecalculateOnHoldSalesAsync_UpdatesPricesWithNewRate_PreservingPayments()
    {
        var (service, context, inventoryMock, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var product = new ProductBuilder().WithId(50).WithCostAndMargin(10m, 50m).Build();
        inventoryMock.Setup(i => i.GetProductsByIdsAsync(It.IsAny<IEnumerable<int>>())).ReturnsAsync(new List<Product> { product });

        var sale = new SaleBuilder()
            .WithId(40)
            .WithStatus(SaleStatus.OnHold)
            .WithAppliedRate(40m)
            .WithItem(50, "Prod 50", 1, 15m)
            .WithPayment(1, 5m, 200m)
            .Build();

        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        int updatedCount = await service.RecalculateOnHoldSalesAsync(60m);

        Assert.Equal(1, updatedCount);
        var updated = await context.Sales.Include(s => s.Payments).FirstAsync(s => s.Id == 40);
        Assert.Equal(60m, updated.AppliedRate);
        Assert.Single(updated.Payments);
        Assert.Equal(5m, updated.Payments[0].Amount); // Pagos previos intactos
    }

    [Fact]
    public async Task ConfirmPickupAsync_SetsDeliveredStatus_AndPickupDate()
    {
        var (service, context, _, mediatorMock, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var customer = new CustomerBuilder().WithId(200).WithName("Lucia").Build();
        context.Customers.Add(customer);

        var sale = new SaleBuilder()
            .WithId(50)
            .WithCustomer(200, "Lucia", "V-200")
            .WithStatus(SaleStatus.Completed)
            .WithDeliveryStatus(SaleDeliveryStatus.PendingPickup)
            .WithItem(1, "Custodia Prod", 1, 10m)
            .Build();

        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var detail = await service.ConfirmPickupAsync(50);

        Assert.Equal("Delivered", detail.DeliveryStatus);
        Assert.NotNull(detail.PickupDate);
        mediatorMock.Verify(m => m.Publish(It.IsAny<SaleMadeEvent>(), default), Times.Never);
    }

    [Fact]
    public async Task CompleteSale_WithCashAdvanceProduct_DoesNotDeductStockOrThrow()
    {
        var (service, context, inventoryMock, _, _) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var advProduct = new Product
        {
            Id = 99,
            SKU = "ADV-001",
            Name = "Adelanto de Efectivo",
            IsCashAdvance = true,
            StockQuantity = 0m,
            IsActive = true
        };

        inventoryMock.Setup(i => i.GetProductByIdAsync(99)).ReturnsAsync(advProduct);
        inventoryMock.Setup(i => i.GetTodayExchangeRateAsync()).ReturnsAsync(50m);

        var sale = await service.StartSaleAsync();
        var itemAdded = await service.AddItemAsync(sale.Id, 99, 1, 50m, custom_unit_price_usd: 10m, custom_unit_price_local: 500m);
        Assert.NotNull(itemAdded);

        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 10m, 500m, null)
        };

        int invoiceNum = await service.CompleteSaleAsync(sale.Id, 50m, payments);
        Assert.True(invoiceNum > 0);

        var completed = await context.Sales.FindAsync(sale.Id);
        Assert.NotNull(completed);
        Assert.Equal(SaleStatus.Completed, completed.Status);

        // Verify that stock is not deducted for cash advance products
        inventoryMock.Verify(i => i.UpdateStockAsync(99, It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void GetUtcRange_CalculatesVenezuelaDayBoundsCorrectly()
    {
        var testDate = new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Unspecified);
        var (startUtc, endExclusiveUtc) = Core.Helpers.TimeZoneHelper.GetUtcRange(testDate, testDate);

        Assert.NotNull(startUtc);
        Assert.NotNull(endExclusiveUtc);

        // Venezuela is UTC-4: 2026-09-04 00:00:00 local is 2026-09-04 04:00:00 UTC
        Assert.Equal(new DateTime(2026, 9, 4, 4, 0, 0, DateTimeKind.Utc), startUtc.Value);
        // End of day is 2026-09-05 00:00:00 local, which is 2026-09-05 04:00:00 UTC
        Assert.Equal(new DateTime(2026, 9, 5, 4, 0, 0, DateTimeKind.Utc), endExclusiveUtc.Value);
    }

    [Fact]
    public void GetUtcRange_WithNullEndDate_DefaultsToEndOfToday()
    {
        var testDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var (startUtc, endExclusiveUtc) = Core.Helpers.TimeZoneHelper.GetUtcRange(testDate, null, defaultEndDateToToday: true);

        Assert.NotNull(startUtc);
        Assert.NotNull(endExclusiveUtc);

        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var expectedTomorrowUtc = Core.Helpers.TimeZoneHelper.ToVenezuelaTime(DateTime.UtcNow).Date.AddDays(1);
        var tz = Core.Helpers.TimeZoneHelper.GetVenezuelaTimeZone();
        var expectedEndUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(expectedTomorrowUtc, DateTimeKind.Unspecified), tz);

        Assert.Equal(expectedEndUtc, endExclusiveUtc.Value);
    }

    [Fact]
    public async Task GetSalesHistoryAsync_IncludesLateNightSales_Between8pmAndMidnight()
    {
        var (service, context, _, _, _) = CreateService();

        // 1. Venta nocturna a las 10:45 PM hora local (2026-09-04 22:45 VET = 2026-09-05 02:45 UTC)
        var lateNightSale = new Sale
        {
            Id = 101,
            InvoiceNumber = 216,
            Date = new DateTime(2026, 9, 5, 2, 45, 0, DateTimeKind.Utc),
            Status = SaleStatus.Completed,
            TotalUSD = 10m,
            TotalBsS = 500m,
            FinalPaidAmountBsS = 500m,
            AppliedRate = 50m
        };

        // 2. Venta matutina a las 10:00 AM hora local (2026-09-04 10:00 VET = 2026-09-04 14:00 UTC)
        var morningSale = new Sale
        {
            Id = 102,
            InvoiceNumber = 215,
            Date = new DateTime(2026, 9, 4, 14, 0, 0, DateTimeKind.Utc),
            Status = SaleStatus.Completed,
            TotalUSD = 20m,
            TotalBsS = 1000m,
            FinalPaidAmountBsS = 1000m,
            AppliedRate = 50m
        };

        // 3. Venta del día anterior (2026-09-03 23:55 VET = 2026-09-04 03:55 UTC)
        var previousDaySale = new Sale
        {
            Id = 103,
            InvoiceNumber = 214,
            Date = new DateTime(2026, 9, 4, 3, 55, 0, DateTimeKind.Utc),
            Status = SaleStatus.Completed,
            TotalUSD = 5m,
            TotalBsS = 250m,
            FinalPaidAmountBsS = 250m,
            AppliedRate = 50m
        };

        // 4. Venta del día posterior (2026-09-05 00:05 VET = 2026-09-05 04:05 UTC)
        var nextDaySale = new Sale
        {
            Id = 104,
            InvoiceNumber = 217,
            Date = new DateTime(2026, 9, 5, 4, 5, 0, DateTimeKind.Utc),
            Status = SaleStatus.Completed,
            TotalUSD = 15m,
            TotalBsS = 750m,
            FinalPaidAmountBsS = 750m,
            AppliedRate = 50m
        };

        context.Sales.AddRange(lateNightSale, morningSale, previousDaySale, nextDaySale);
        await context.SaveChangesAsync();

        // Filtrar por el día 4 de septiembre de 2026
        var filterDate = new DateTime(2026, 9, 4);
        var (items, totalCount) = await service.GetSalesHistoryAsync(1, 20, filterDate, filterDate);

        var list = items.ToList();
        Assert.Equal(2, totalCount);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, s => s.InvoiceNumber == 216); // Venta nocturna incluida!
        Assert.Contains(list, s => s.InvoiceNumber == 215); // Venta matutina incluida!
        Assert.DoesNotContain(list, s => s.InvoiceNumber == 214); // Día anterior excluida!
        Assert.DoesNotContain(list, s => s.InvoiceNumber == 217); // Día siguiente excluida!

        // Comprobar que DateLocal devuelve la hora local de Venezuela (22:45 VET)
        var retrievedLateNight = list.First(s => s.InvoiceNumber == 216);
        Assert.Equal(22, retrievedLateNight.DateLocal.Hour);
        Assert.Equal(45, retrievedLateNight.DateLocal.Minute);
        Assert.Equal(4, retrievedLateNight.DateLocal.Day);
        Assert.Equal(9, retrievedLateNight.DateLocal.Month);
    }

    [Fact]
    public async Task GetSalesHistoryAsync_AgainstRealPostgreSql_ReturnsInvoicesFromTonight()
    {
        var connStr = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")
            ?? "Host=localhost;Database=CommandCenterDb;Username=postgres;Password=123456";

        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql(connStr)
            .Options;
        using var realContext = new SalesDbContext(options);

        var inventoryMock = new Mock<IInventoryService>();
        var mediatorMock = new Mock<IMediator>();
        var cashDrawerMock = new Mock<ICashDrawerService>();
        var settingsMock = new Mock<ISystemSettingsService>();

        var realService = new SalesService(realContext, inventoryMock.Object, mediatorMock.Object, cashDrawerMock.Object, settingsMock.Object);

        // Consultar con filtro del 4 de septiembre de 2026
        var filterDate = new DateTime(2026, 9, 4);
        var (items, totalCount) = await realService.GetSalesHistoryAsync(1, 25, filterDate, filterDate);

        Assert.True(totalCount >= 5, $"Se esperaban al menos 5 ventas de hoy en PostgreSQL real, pero se obtuvieron {totalCount}.");
        var list = items.ToList();
        Assert.Contains(list, s => s.InvoiceNumber == 216);
        Assert.Contains(list, s => s.InvoiceNumber == 217);
        Assert.Contains(list, s => s.InvoiceNumber == 218);
        Assert.Contains(list, s => s.InvoiceNumber == 219);
        Assert.Contains(list, s => s.InvoiceNumber == 220);

        // Verificar hora local de factura 216 (emitida a las 22:28 VET)
        var inv216 = list.First(s => s.InvoiceNumber == 216);
        Assert.Equal(22, inv216.DateLocal.Hour);
        Assert.Equal(4, inv216.DateLocal.Day);
        Assert.Equal(9, inv216.DateLocal.Month);

        // Verificar GetSaleHistoryDetailAsync para factura 218
        var inv218 = list.First(s => s.InvoiceNumber == 218);
        var detail218 = await realService.GetSaleHistoryDetailAsync(inv218.Id);
        Assert.NotNull(detail218);
        Assert.NotEmpty(detail218.Items);
        Assert.NotEmpty(detail218.Payments);
        Assert.True(detail218.AppliedRate > 0);
        Assert.True(detail218.TotalUSD > 0);
        Assert.True(detail218.TotalBsS > 0);

        // Probar serialización/deserialización HTTP hacia Desktop.Client.Services.SaleHistoryDto
        var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var jsonStr = System.Text.Json.JsonSerializer.Serialize(detail218, jsonOptions);
        var clientDto = System.Text.Json.JsonSerializer.Deserialize<Desktop.Client.Services.SaleHistoryDto>(jsonStr, jsonOptions);
        Assert.NotNull(clientDto);
        Assert.NotEmpty(clientDto.Items);
        Assert.NotEmpty(clientDto.Payments);
        Assert.Equal(detail218.AppliedRate, clientDto.AppliedRate);
        Assert.Equal(detail218.TotalUSD, clientDto.TotalUSD);
        Assert.Equal(detail218.TotalBsS, clientDto.TotalBsS);
        Assert.Equal(detail218.InvoiceNumber, clientDto.InvoiceNumber);
    }

    [Fact]
    public async Task SalesHistoryViewModel_WhenSelectedSaleChanges_PreloadsImmediately_ThenLoadsFullDetails()
    {
        var mockClientSalesService = new Mock<Desktop.Client.Services.ISalesService>();
        var sampleDetail = new Desktop.Client.Services.SaleHistoryDto
        {
            Id = 218,
            InvoiceNumber = 218,
            AppliedRate = 813.74m,
            TotalUSD = 0.81m,
            TotalBsS = 659.13m,
            Date = new DateTime(2026, 9, 4, 22, 44, 0, DateTimeKind.Utc),
            Items = new List<Desktop.Client.Services.SaleItemHistoryDto>
            {
                new() { Id = 1, ProductName = "Papas Lays", Quantity = 1, UnitPrice = 0.81m, UnitPriceBsS = 659.13m, SubtotalBsS = 659.13m }
            },
            Payments = new List<Desktop.Client.Services.PaymentDetailDto>
            {
                new() { MethodName = "Punto de Venta", AmountBsS = 659.13m, Reference = "1234" }
            }
        };

        mockClientSalesService.Setup(s => s.GetSaleHistoryDetailAsync(218, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(sampleDetail);

        // Inyección de dispatcher síncrono para pruebas unitarias sin dependencias de Application.Current
        bool dispatcherInvoked = false;
        Action<Action> testDispatcher = act =>
        {
            dispatcherInvoked = true;
            act();
        };

        var vm = new Desktop.Client.ViewModels.SalesHistoryViewModel(mockClientSalesService.Object, testDispatcher);
        var selectedItem = new Desktop.Client.Services.SaleHistoryDto
        {
            Id = 218,
            InvoiceNumber = 218,
            AppliedRate = 813.74m,
            TotalUSD = 0.81m,
            TotalBsS = 659.13m,
            Date = new DateTime(2026, 9, 4, 22, 44, 0, DateTimeKind.Utc)
        };

        // 1. Asignar selección: verificar precarga inmediata
        vm.SelectedSale = selectedItem;

        Assert.Equal(813.74m, vm.DetailAppliedRate);
        Assert.Equal(0.81m, vm.DetailTotalUSD);
        Assert.Equal(659.13m, vm.DetailTotalBsS);
        Assert.False(string.IsNullOrWhiteSpace(vm.DetailDateLocalFormatted));
        Assert.Equal(0, vm.DetailSubtotalBsS); // Subtotal de items en 0 hasta recibir respuesta completa
        Assert.True(dispatcherInvoked);

        // 2. Esperar debounce de 200ms + llamada a la API
        await Task.Delay(400);

        Assert.NotEmpty(vm.SelectedSaleItems);
        Assert.Single(vm.SelectedSaleItems);
        Assert.Equal("Papas Lays", vm.SelectedSaleItems[0].ProductName);
        Assert.NotEmpty(vm.SelectedSalePayments);
        Assert.Equal(659.13m, vm.DetailSubtotalBsS);
        Assert.Equal(813.74m, vm.DetailAppliedRate);
        Assert.False(vm.IsDetailFetching);

        // 3. Deseleccionar: verificar que se limpia todo el estado de detalle
        vm.SelectedSale = null;
        Assert.Empty(vm.SelectedSaleItems);
        Assert.Empty(vm.SelectedSalePayments);
        Assert.Equal(0, vm.DetailAppliedRate);
        Assert.Equal(0, vm.DetailTotalUSD);
        Assert.Equal(0, vm.DetailTotalBsS);
        Assert.Equal(string.Empty, vm.DetailDateLocalFormatted);
    }
}

