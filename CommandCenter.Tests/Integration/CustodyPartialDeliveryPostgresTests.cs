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

// PostgreSQL-gated by TEST_POSTGRES_CONNECTION, matching the existing integration-test pattern.
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

    private static async Task<(int SaleId, int SaleItemId)> CreatePendingSaleAsync()
    {
        await using var context = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
        Assert.NotNull(context);
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

    private static async Task DeleteTestSaleAsync(int saleId, string? idempotencyKey = null, string? requestPath = null)
    {
        await using var context = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
        Assert.NotNull(context);

        if (idempotencyKey is not null && requestPath is not null)
        {
            await context.IdempotentRequests
                .Where(record => record.Key == idempotencyKey && record.RequestPath == requestPath)
                .ExecuteDeleteAsync();
        }

        await context.SaleDeliveries
            .Where(delivery => delivery.SaleId == saleId)
            .ExecuteDeleteAsync();
        await context.Sales
            .Where(sale => sale.Id == saleId)
            .ExecuteDeleteAsync();
    }

    [Fact]
    public async Task DeliverPartial_PostgreSqlStaleXmin_ThrowsAndPersistsOnlyWinningDelivery()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var (saleId, saleItemId) = await CreatePendingSaleAsync();
        try
        {
            await using var staleContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
            Assert.NotNull(staleContext);
            var staleSale = await staleContext.Sales
                .Include(candidate => candidate.Items)
                .SingleAsync(candidate => candidate.Id == saleId);
            var staleItem = Assert.Single(staleSale.Items);
            var staleSaleXmin = staleContext.Entry(staleSale).Property<uint>("xmin").OriginalValue;
            var staleItemXmin = staleContext.Entry(staleItem).Property<uint>("xmin").OriginalValue;

            await using var winnerContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
            Assert.NotNull(winnerContext);
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

            await using var verifyContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
            Assert.NotNull(verifyContext);
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
        finally
        {
            await DeleteTestSaleAsync(saleId);
        }
    }

    [Fact]
    public async Task DeliverPartial_PostgreSqlDuplicateIdempotencyKey_RollsBackSecondDelivery()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var (saleId, saleItemId) = await CreatePendingSaleAsync();
        var idempotencyKey = $"DELIVERY-PG-{Guid.NewGuid():N}";
        var requestPath = $"/api/sales/{saleId}/deliveries";
        var payloadHash = SHA256.HashData(Encoding.UTF8.GetBytes($"{saleId}:{saleItemId}:2"));
        var requestItems = new[] { (SaleItemId: saleItemId, Quantity: 2m) };

        try
        {
            await using (var firstContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext())
            {
                Assert.NotNull(firstContext);
                await CreateService(firstContext).DeliverPartialAsync(
                    saleId,
                    requestItems,
                    notes: null,
                    idempotencyKey: idempotencyKey,
                    idempotencyPayloadHash: payloadHash,
                    requestPath: requestPath);
            }

            DbUpdateException duplicateKeyException;
            await using (var secondContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext())
            {
                Assert.NotNull(secondContext);
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

            await using var verifyContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
            Assert.NotNull(verifyContext);
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
        finally
        {
            await DeleteTestSaleAsync(saleId, idempotencyKey, requestPath);
        }
    }
}
