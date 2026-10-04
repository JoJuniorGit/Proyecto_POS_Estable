using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
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

namespace CommandCenter.Tests.Unit;

public class CustodyPartialDeliveryTests
{
    private static SalesDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private static SalesService CreateSalesService(SalesDbContext context)
    {
        return new SalesService(
            context,
            Mock.Of<IInventoryService>(),
            Mock.Of<IMediator>(),
            Mock.Of<ICashDrawerService>(),
            Mock.Of<ISystemSettingsService>());
    }

    private static SaleBuilder CustodySale(
        int saleId,
        SaleDeliveryStatus deliveryStatus = SaleDeliveryStatus.PendingPickup,
        decimal quantity = 10m,
        decimal deliveredQuantity = 0m)
    {
        return new SaleBuilder()
            .WithId(saleId)
            .WithInvoiceNumber(saleId + 1000)
            .WithCustomer(1, "Marta Ruiz", "V-12345678")
            .WithAppliedRate(40m)
            .WithStatus(SaleStatus.Completed)
            .WithDeliveryStatus(deliveryStatus)
            .WithItem(7, "Test Product", quantity, 2m)
            .WithItemDeliveredQuantity(1, deliveredQuantity);
    }

    [Fact]
    public async Task DeliverPartial_ValidSubset_UpdatesCountersStatusPartial_AndWritesEvent()
    {
        using var context = CreateContext();
        var sale = CustodySale(100).Build();
        context.Sales.Add(sale);
        context.Users.Add(new User
        {
            Id = 5,
            Cedula = "V-87654321",
            Name = "Ana Paredes",
            FullName = "Ana Paredes de Gómez",
            Username = "ana"
        });
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        var receipt = await service.DeliverPartialAsync(
            sale.Id,
            new List<(int SaleItemId, decimal Quantity)> { (1, 4m) },
            "first dispatch",
            actingUserId: 5);

        var updatedSale = await context.Sales
            .AsNoTracking()
            .Include(item => item.Items)
            .SingleAsync(item => item.Id == sale.Id);
        var delivery = await context.SaleDeliveries
            .AsNoTracking()
            .Include(item => item.Items)
            .SingleAsync();

        Assert.Equal(4m, updatedSale.Items[0].DeliveredQuantity);
        Assert.Equal(SaleDeliveryStatus.PartiallyDelivered, updatedSale.DeliveryStatus);
        Assert.Null(updatedSale.PickupDate);
        Assert.Equal("Ana Paredes", delivery.DeliveredByName);
        Assert.Equal(5, delivery.DeliveredByUserId);
        Assert.Equal("first dispatch", delivery.Notes);
        Assert.Single(delivery.Items);
        Assert.Equal(1, delivery.Items[0].SaleItemId);
        Assert.Equal(7, delivery.Items[0].ProductId);
        Assert.Equal("Test Product", delivery.Items[0].ProductName);
        Assert.Equal(4m, delivery.Items[0].QuantityDelivered);
        Assert.Equal(delivery.Id, receipt.DeliveryId);
        Assert.Equal(100, receipt.SaleId);
        Assert.Equal(1100, receipt.InvoiceNumber);
        Assert.Equal("Ana Paredes", receipt.DeliveredByName);
        Assert.Equal("Marta Ruiz", receipt.CustomerName);
        Assert.Equal("V-12345678", receipt.CustomerCedula);
        Assert.Equal("PartiallyDelivered", receipt.DeliveryStatus);
        Assert.Equal(10m, receipt.TotalUnits);
        Assert.Equal(4m, receipt.DeliveredUnits);
        Assert.Equal(6m, receipt.PendingUnits);
        var receiptItem = Assert.Single(receipt.Items);
        Assert.Equal(1, receiptItem.SaleItemId);
        Assert.Equal(7, receiptItem.ProductId);
        Assert.Equal("Test Product", receiptItem.ProductName);
        Assert.Equal(4m, receiptItem.QuantityDelivered);
        Assert.Equal(2m, receiptItem.UnitPrice);
        Assert.Equal(80m, receiptItem.UnitPriceBsS);
        Assert.Equal(320m, receiptItem.SubtotalBsS);
    }

    [Fact]
    public async Task DeliverPartial_WithIdempotencyDetails_PersistsWebSerializedReceipt()
    {
        using var context = CreateContext();
        var sale = CustodySale(110).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();
        const string idempotencyKey = "DELIVERY-SERVICE-110";
        const string requestPath = "/api/sales/110/deliveries";
        var payloadHash = new byte[] { 1, 2, 3 };

        var receipt = await CreateSalesService(context).DeliverPartialAsync(
            sale.Id,
            new List<(int SaleItemId, decimal Quantity)> { (1, 4m) },
            notes: "first dispatch",
            idempotencyKey: idempotencyKey,
            idempotencyPayloadHash: payloadHash,
            requestPath: requestPath);

        var record = await context.IdempotentRequests.SingleAsync();

        Assert.Equal(idempotencyKey, record.Key);
        Assert.Equal(requestPath, record.RequestPath);
        Assert.Equal(payloadHash, record.PayloadHash);
        Assert.Equal(200, record.StatusCode);
        Assert.Equal(JsonSerializer.Serialize(receipt, JsonSerializerOptions.Web), record.ResponseBody);
    }

    [Fact]
    public async Task DeliverPartial_WithoutPayloadHash_DoesNotPersistIdempotencyRecord()
    {
        using var context = CreateContext();
        var sale = CustodySale(111).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        await CreateSalesService(context).DeliverPartialAsync(
            sale.Id,
            new List<(int SaleItemId, decimal Quantity)> { (1, 4m) },
            notes: null,
            idempotencyKey: "DELIVERY-NO-HASH-111",
            requestPath: "/api/sales/111/deliveries");

        Assert.Single(await context.SaleDeliveries.ToListAsync());
        Assert.Empty(await context.IdempotentRequests.ToListAsync());
    }

    [Fact]
    public async Task DeliverPartial_FullRemaining_SetsDeliveredAndPickupDate()
    {
        using var context = CreateContext();
        var sale = CustodySale(101, SaleDeliveryStatus.PartiallyDelivered, deliveredQuantity: 4m).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var receipt = await CreateSalesService(context).DeliverPartialAsync(
            sale.Id,
            new List<(int SaleItemId, decimal Quantity)> { (1, 6m) },
            notes: null);

        var updatedSale = await context.Sales.AsNoTracking().SingleAsync(item => item.Id == sale.Id);

        Assert.Equal(SaleDeliveryStatus.Delivered, updatedSale.DeliveryStatus);
        Assert.NotNull(updatedSale.PickupDate);
        Assert.Equal("Delivered", receipt.DeliveryStatus);
        Assert.Equal(10m, receipt.DeliveredUnits);
        Assert.Equal(0m, receipt.PendingUnits);
        Assert.Equal(receipt.DeliveredAt, updatedSale.PickupDate);
    }

    [Fact]
    public async Task DeliverPartial_TwoEvents_Accumulate_AndComplete()
    {
        using var context = CreateContext();
        var sale = CustodySale(102).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();
        var service = CreateSalesService(context);

        var firstReceipt = await service.DeliverPartialAsync(
            sale.Id,
            new List<(int SaleItemId, decimal Quantity)> { (1, 6m) },
            notes: "first");
        var secondReceipt = await service.DeliverPartialAsync(
            sale.Id,
            new List<(int SaleItemId, decimal Quantity)> { (1, 4m) },
            notes: "second");

        var updatedSale = await context.Sales
            .AsNoTracking()
            .Include(item => item.Items)
            .SingleAsync(item => item.Id == sale.Id);
        var deliveries = await context.SaleDeliveries
            .AsNoTracking()
            .Include(item => item.Items)
            .OrderBy(item => item.DeliveredAt)
            .ToListAsync();

        Assert.Equal(2, deliveries.Count);
        Assert.Equal(6m, deliveries[0].Items.Single().QuantityDelivered);
        Assert.Equal(4m, deliveries[1].Items.Single().QuantityDelivered);
        Assert.Equal("first", deliveries[0].Notes);
        Assert.Equal("second", deliveries[1].Notes);
        Assert.NotEqual(firstReceipt.DeliveryId, secondReceipt.DeliveryId);
        Assert.Equal(10m, updatedSale.Items.Single().DeliveredQuantity);
        Assert.Equal(SaleDeliveryStatus.Delivered, updatedSale.DeliveryStatus);
        Assert.NotNull(updatedSale.PickupDate);
    }

    [Fact]
    public async Task DeliverPartial_OverPending_Throws_WithExactMessage()
    {
        using var context = CreateContext();
        var sale = CustodySale(103, SaleDeliveryStatus.PartiallyDelivered, deliveredQuantity: 4m).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => CreateSalesService(context).DeliverPartialAsync(
            sale.Id,
            new List<(int SaleItemId, decimal Quantity)> { (1, 7m) },
            notes: null));

        Assert.Equal("La cantidad a entregar supera la cantidad pendiente del producto Test Product.", exception.Message);
        Assert.Empty(await context.SaleDeliveries.ToListAsync());
        Assert.Equal(4m, (await context.SaleItems.SingleAsync(item => item.Id == 1)).DeliveredQuantity);
    }

    [Fact]
    public async Task DeliverPartial_ZeroOrNegative_Throws()
    {
        using var context = CreateContext();
        var sale = CustodySale(104).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();
        var service = CreateSalesService(context);

        foreach (var quantity in new[] { 0m, -1m })
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.DeliverPartialAsync(
                sale.Id,
                new List<(int SaleItemId, decimal Quantity)> { (1, quantity) },
                notes: null));

            Assert.Equal("Debe indicar al menos una cantidad a entregar.", exception.Message);
        }

        Assert.Empty(await context.SaleDeliveries.ToListAsync());
    }

    [Fact]
    public async Task DeliverPartial_EmptyItems_Throws()
    {
        using var context = CreateContext();
        var sale = CustodySale(105).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => CreateSalesService(context).DeliverPartialAsync(
            sale.Id,
            Array.Empty<(int SaleItemId, decimal Quantity)>(),
            notes: null));

        Assert.Equal("Debe indicar al menos una cantidad a entregar.", exception.Message);
        Assert.Empty(await context.SaleDeliveries.ToListAsync());
    }

    [Fact]
    public async Task DeliverPartial_ForeignItem_Throws()
    {
        using var context = CreateContext();
        var sale = CustodySale(106).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => CreateSalesService(context).DeliverPartialAsync(
            sale.Id,
            new List<(int SaleItemId, decimal Quantity)> { (999, 1m) },
            notes: null));

        Assert.Equal("El artículo 999 no pertenece a la venta.", exception.Message);
        Assert.Empty(await context.SaleDeliveries.ToListAsync());
    }

    [Fact]
    public async Task DeliverPartial_DuplicateItem_Throws()
    {
        using var context = CreateContext();
        var sale = CustodySale(107).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => CreateSalesService(context).DeliverPartialAsync(
            sale.Id,
            new List<(int SaleItemId, decimal Quantity)> { (1, 1m), (1, 2m) },
            notes: null));

        Assert.Equal("La solicitud contiene artículos duplicados.", exception.Message);
        Assert.Empty(await context.SaleDeliveries.ToListAsync());
        Assert.Equal(0m, (await context.SaleItems.SingleAsync(item => item.Id == 1)).DeliveredQuantity);
    }

    [Fact]
    public async Task DeliverPartial_NonCustodySale_Throws()
    {
        using var context = CreateContext();
        var sale = CustodySale(108, SaleDeliveryStatus.Delivered).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSalesService(context).DeliverPartialAsync(
            sale.Id,
            new List<(int SaleItemId, decimal Quantity)> { (1, 1m) },
            notes: null));

        Assert.Equal("La venta no se encuentra en estado de retiro pendiente.", exception.Message);
        Assert.Empty(await context.SaleDeliveries.ToListAsync());
    }

    [Fact]
    public async Task DeliverPartial_DoesNotChangeFinancials()
    {
        using var context = CreateContext();
        var sale = CustodySale(109)
            .WithPayment(1, 20m, 800m, "PAY-109")
            .Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        await CreateSalesService(context).DeliverPartialAsync(
            sale.Id,
            new List<(int SaleItemId, decimal Quantity)> { (1, 4m) },
            notes: null);

        var updatedSale = await context.Sales
            .AsNoTracking()
            .Include(item => item.Payments)
            .SingleAsync(item => item.Id == sale.Id);

        Assert.Equal(20m, updatedSale.TotalUSD);
        Assert.Equal(800m, updatedSale.TotalBsS);
        Assert.Equal(40m, updatedSale.AppliedRate);
        Assert.Equal(800m, updatedSale.FinalPaidAmountBsS);
        var payment = Assert.Single(updatedSale.Payments);
        Assert.Equal(20m, payment.Amount);
        Assert.Equal(800m, payment.AmountBsS);
        Assert.Equal(40m, payment.ExchangeRate);
        Assert.Equal("PAY-109", payment.ReferenceNumber);
    }

    [Fact]
    public async Task ConfirmPickup_AfterPartial_DeliversAllRemaining_AsSingleEvent()
    {
        using var context = CreateContext();
        var sale = CustodySale(110, SaleDeliveryStatus.PartiallyDelivered, deliveredQuantity: 4m).Build();
        context.Sales.Add(sale);
        context.SaleDeliveries.Add(new SaleDelivery
        {
            Id = 1,
            SaleId = sale.Id,
            DeliveredAt = DateTime.UtcNow.AddHours(-1),
            DeliveredByName = "First Cashier",
            Items = new List<SaleDeliveryItem>
            {
                new()
                {
                    Id = 1,
                    SaleDeliveryId = 1,
                    SaleItemId = 1,
                    ProductId = 7,
                    ProductName = "Test Product",
                    QuantityDelivered = 4m
                }
            }
        });
        await context.SaveChangesAsync();

        var result = await CreateSalesService(context).ConfirmPickupAsync(sale.Id);

        var updatedSale = await context.Sales
            .AsNoTracking()
            .Include(item => item.Items)
            .SingleAsync(item => item.Id == sale.Id);
        var deliveries = await context.SaleDeliveries
            .AsNoTracking()
            .Include(item => item.Items)
            .OrderBy(item => item.Id)
            .ToListAsync();

        Assert.Equal("Delivered", result.DeliveryStatus);
        Assert.Equal(10m, updatedSale.Items.Single().DeliveredQuantity);
        Assert.Equal(SaleDeliveryStatus.Delivered, updatedSale.DeliveryStatus);
        Assert.NotNull(updatedSale.PickupDate);
        Assert.Equal(2, deliveries.Count);
        Assert.Single(deliveries[1].Items);
        Assert.Equal(6m, deliveries[1].Items[0].QuantityDelivered);
        Assert.Null(deliveries[1].Notes);
    }

    [Fact]
    public async Task GetPendingPickups_IncludesPartiallyDelivered_WithPendingQuantities_AndAggregates()
    {
        using var context = CreateContext();
        var sale = new SaleBuilder()
            .WithId(111)
            .WithInvoiceNumber(1111)
            .WithCustomer(1, "Marta Ruiz", "V-12345678")
            .WithAppliedRate(40m)
            .WithStatus(SaleStatus.Completed)
            .WithDeliveryStatus(SaleDeliveryStatus.PartiallyDelivered)
            .WithItem(7, "Test Product", 10m, 2m)
            .WithItem(8, "Second Product", 4m, 3m)
            .WithItemDeliveredQuantity(1, 4m)
            .WithItemDeliveredQuantity(2, 2m)
            .Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateSalesService(context);
        var pickup = Assert.Single(await service.GetPendingPickupsAsync());

        Assert.Equal(sale.Id, pickup.SaleId);
        Assert.Equal("PartiallyDelivered", pickup.DeliveryStatus);
        Assert.Equal(14m, pickup.TotalUnits);
        Assert.Equal(6m, pickup.DeliveredUnits);
        Assert.Equal(2, pickup.Items.Count);
        Assert.Collection(
            pickup.Items,
            item =>
            {
                Assert.Equal(1, item.SaleItemId);
                Assert.Equal(7, item.ProductId);
                Assert.Equal("Test Product", item.ProductName);
                Assert.Equal(10m, item.Quantity);
                Assert.Equal(4m, item.DeliveredQuantity);
                Assert.Equal(6m, item.PendingQuantity);
                Assert.Equal(2m, item.UnitPrice);
                Assert.Equal(80m, item.UnitPriceBsS);
                Assert.Equal(800m, item.SubtotalBsS);
            },
            item =>
            {
                Assert.Equal(2, item.SaleItemId);
                Assert.Equal(8, item.ProductId);
                Assert.Equal("Second Product", item.ProductName);
                Assert.Equal(4m, item.Quantity);
                Assert.Equal(2m, item.DeliveredQuantity);
                Assert.Equal(2m, item.PendingQuantity);
                Assert.Equal(3m, item.UnitPrice);
                Assert.Equal(120m, item.UnitPriceBsS);
                Assert.Equal(480m, item.SubtotalBsS);
            });
        Assert.Equal(1, await service.CountPendingPickupsAsync());
    }

    [Fact]
    public async Task GetDeliveryNotePdfAsync_ExistingDelivery_ReturnsNonEmptyPdf()
    {
        using var context = CreateContext();
        var sale = CustodySale(112, deliveredQuantity: 4m).Build();
        context.Sales.Add(sale);
        var delivery = new SaleDelivery
        {
            SaleId = sale.Id,
            DeliveredAt = new DateTime(2026, 10, 3, 12, 30, 0, DateTimeKind.Utc),
            DeliveredByName = "Ana Paredes",
            Items = new List<SaleDeliveryItem>
            {
                new()
                {
                    SaleItemId = 1,
                    ProductId = 7,
                    ProductName = "Test Product",
                    QuantityDelivered = 4m
                }
            }
        };
        context.SaleDeliveries.Add(delivery);
        await context.SaveChangesAsync();

        var pdf = CreateSalesService(context).GetDeliveryNotePdfAsync(sale.Id, delivery.Id);

        Assert.NotEmpty(pdf);
    }

    [Fact]
    public async Task GetDeliveryNotePdfAsync_ReprintUsesPendingBalanceAtRecordedEvent()
    {
        using var context = CreateContext();
        var sale = CustodySale(116, SaleDeliveryStatus.PartiallyDelivered, deliveredQuantity: 7m).Build();
        context.Sales.Add(sale);
        var earlierDelivery = new SaleDelivery
        {
            SaleId = sale.Id,
            DeliveredAt = new DateTime(2026, 10, 3, 12, 30, 0, DateTimeKind.Utc),
            DeliveredByName = "Ana Paredes",
            Items = new List<SaleDeliveryItem>
            {
                new()
                {
                    SaleItemId = 1,
                    ProductId = 7,
                    ProductName = "Test Product",
                    QuantityDelivered = 4m
                }
            }
        };
        var laterDelivery = new SaleDelivery
        {
            SaleId = sale.Id,
            DeliveredAt = new DateTime(2026, 10, 4, 12, 30, 0, DateTimeKind.Utc),
            DeliveredByName = "Carlos Perez",
            Items = new List<SaleDeliveryItem>
            {
                new()
                {
                    SaleItemId = 1,
                    ProductId = 7,
                    ProductName = "Test Product",
                    QuantityDelivered = 3m
                }
            }
        };
        context.SaleDeliveries.AddRange(earlierDelivery, laterDelivery);
        await context.SaveChangesAsync();

        var pdf = CreateSalesService(context).GetDeliveryNotePdfAsync(sale.Id, earlierDelivery.Id);
        var content = Encoding.ASCII.GetString(pdf);

        Assert.Contains("(6) Tj", content);
        Assert.DoesNotContain("(3) Tj", content);
    }

    [Fact]
    public async Task GetDeliveryNotePdfAsync_MissingDelivery_ThrowsKeyNotFoundException()
    {
        using var context = CreateContext();
        var sale = CustodySale(113).Build();
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var exception = Assert.Throws<KeyNotFoundException>(() =>
            CreateSalesService(context).GetDeliveryNotePdfAsync(sale.Id, 999));

        Assert.Equal("Entrega no encontrada.", exception.Message);
    }

    [Fact]
    public async Task GetDeliveryNotePdfAsync_DeliveryBelongsToAnotherSale_ThrowsKeyNotFoundException()
    {
        using var context = CreateContext();
        var sale = CustodySale(114).Build();
        var otherSale = CustodySale(115).Build();
        otherSale.Items.Single().Id = 2;
        context.Sales.AddRange(sale, otherSale);
        var delivery = new SaleDelivery
        {
            SaleId = otherSale.Id,
            DeliveredAt = DateTime.UtcNow,
            DeliveredByName = "Ana Paredes",
            Items = new List<SaleDeliveryItem>
            {
                new()
                {
                    SaleItemId = 2,
                    ProductId = 7,
                    ProductName = "Test Product",
                    QuantityDelivered = 1m
                }
            }
        };
        context.SaleDeliveries.Add(delivery);
        await context.SaveChangesAsync();

        var exception = Assert.Throws<KeyNotFoundException>(() =>
            CreateSalesService(context).GetDeliveryNotePdfAsync(sale.Id, delivery.Id));

        Assert.Equal("Entrega no encontrada.", exception.Message);
    }
}
