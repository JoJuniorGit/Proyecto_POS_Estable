using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// Custodia de entregas parciales contra PostgreSQL real en base AISLADA por test (carril
/// <see cref="PostgresTestLane"/>): xmin obsoleto y clave de idempotencia duplicada deben fallar
/// de forma determinista. Sin conexión utilizable el carril falla EXPLÍCITO (nunca silent-pass).
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class CustodyPartialDeliveryPostgresTests
{
    private static SalesService CreateService(SalesDbContext context) =>
        new(context,
            Mock.Of<IInventoryService>(),
            Mock.Of<IMediator>(),
            Mock.Of<ICashDrawerService>(),
            Mock.Of<ISystemSettingsService>());

    private static async Task<(int SaleId, int SaleItemId)> CreatePendingSaleAsync(IsolatedPostgresDatabase database)
    {
        await using var context = database.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var saleId = Random.Shared.Next(1_500_000_000, 2_000_000_000);
        var saleItemId = Random.Shared.Next(1_500_000_000, 2_000_000_000);
        var sale = new SaleBuilder()
            .WithId(saleId)
            .WithAppliedRate(40m)
            .WithStatus(SaleStatus.Completed)
            .WithDeliveryStatus(SaleDeliveryStatus.PendingPickup)
            .WithItem(7, "PostgreSQL delivery item", 10m, 2m)
            .Build();
        sale.InvoiceNumber = null;
        sale.Items.Single().Id = saleItemId;

        context.Sales.Add(sale);
        await context.SaveChangesAsync();
        return (sale.Id, sale.Items.Single().Id);
    }

    [Fact]
    public async Task DeliverPartial_PostgreSqlStaleXmin_ThrowsAndPersistsOnlyWinningDelivery()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync();
        var (saleId, saleItemId) = await CreatePendingSaleAsync(database);

        await using var staleContext = database.CreateSalesDbContext();
        var staleSale = await staleContext.Sales
            .Include(candidate => candidate.Items)
            .SingleAsync(candidate => candidate.Id == saleId);
        var staleItem = Assert.Single(staleSale.Items);
        var staleSaleXmin = staleContext.Entry(staleSale).Property<uint>("xmin").OriginalValue;
        var staleItemXmin = staleContext.Entry(staleItem).Property<uint>("xmin").OriginalValue;

        await using var winnerContext = database.CreateSalesDbContext();
        var requestItems = new[] { (SaleItemId: saleItemId, Quantity: 2m) };
        var winnerReceipt = await CreateService(winnerContext).DeliverPartialAsync(
            saleId,
            requestItems,
            notes: "winning delivery");

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            CreateService(staleContext).DeliverPartialAsync(
                saleId,
                requestItems,
                notes: "stale delivery"));

        await using var verifyContext = database.CreateSalesDbContext();
        var persistedSale = await verifyContext.Sales
            .Include(candidate => candidate.Items)
            .SingleAsync(candidate => candidate.Id == saleId);
        var persistedItem = Assert.Single(persistedSale.Items);
        var deliveries = await verifyContext.SaleDeliveries
            .Include(delivery => delivery.Items)
            .Where(delivery => delivery.SaleId == saleId)
            .ToListAsync();
        var delivery = Assert.Single(deliveries);

        Assert.Equal(2m, persistedItem.DeliveredQuantity);
        Assert.Equal(SaleDeliveryStatus.PartiallyDelivered, persistedSale.DeliveryStatus);
        Assert.Null(persistedSale.PickupDate);
        Assert.Equal(winnerReceipt.DeliveryId, delivery.Id);
        Assert.Equal(2m, Assert.Single(delivery.Items).QuantityDelivered);
        Assert.NotEqual(staleSaleXmin, verifyContext.Entry(persistedSale).Property<uint>("xmin").OriginalValue);
        Assert.NotEqual(staleItemXmin, verifyContext.Entry(persistedItem).Property<uint>("xmin").OriginalValue);
    }

    [Fact]
    public async Task DeliverPartial_PostgreSqlDuplicateIdempotencyKey_RollsBackSecondDelivery()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync();
        var (saleId, saleItemId) = await CreatePendingSaleAsync(database);
        var idempotencyKey = $"DELIVERY-PG-{Guid.NewGuid():N}";
        var requestPath = $"/api/sales/{saleId}/deliveries";
        var payloadHash = SHA256.HashData(Encoding.UTF8.GetBytes($"{saleId}:{saleItemId}:2"));
        var requestItems = new[] { (SaleItemId: saleItemId, Quantity: 2m) };

        await using (var firstContext = database.CreateSalesDbContext())
        {
            await CreateService(firstContext).DeliverPartialAsync(
                saleId,
                requestItems,
                notes: null,
                idempotencyKey: idempotencyKey,
                idempotencyPayloadHash: payloadHash,
                requestPath: requestPath);
        }

        DbUpdateException duplicateKeyException;
        await using (var secondContext = database.CreateSalesDbContext())
        {
            duplicateKeyException = await Assert.ThrowsAsync<DbUpdateException>(() =>
                CreateService(secondContext).DeliverPartialAsync(
                    saleId,
                    requestItems,
                    notes: null,
                    idempotencyKey: idempotencyKey,
                    idempotencyPayloadHash: payloadHash,
                    requestPath: requestPath));
        }

        var postgresException = Assert.IsType<PostgresException>(duplicateKeyException.InnerException);
        Assert.Equal("23505", postgresException.SqlState);

        await using var verifyContext = database.CreateSalesDbContext();
        var persistedSale = await verifyContext.Sales
            .Include(candidate => candidate.Items)
            .SingleAsync(candidate => candidate.Id == saleId);
        var deliveries = await verifyContext.SaleDeliveries
            .Include(delivery => delivery.Items)
            .Where(delivery => delivery.SaleId == saleId)
            .ToListAsync();
        var delivery = Assert.Single(deliveries);
        var idempotencyRecordCount = await verifyContext.IdempotentRequests
            .CountAsync(record => record.Key == idempotencyKey && record.RequestPath == requestPath);

        Assert.Equal(2m, Assert.Single(persistedSale.Items).DeliveredQuantity);
        Assert.Equal(SaleDeliveryStatus.PartiallyDelivered, persistedSale.DeliveryStatus);
        Assert.Equal(2m, Assert.Single(delivery.Items).QuantityDelivered);
        Assert.Equal(1, idempotencyRecordCount);
    }
}
