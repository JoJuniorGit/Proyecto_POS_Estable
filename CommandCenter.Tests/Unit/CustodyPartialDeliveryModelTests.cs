using System;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Sales.Module.Data;
using Sales.Module.Entities;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class CustodyPartialDeliveryModelTests
{
    [Fact]
    public void DeliveryStatus_UsesStableValues_AndIncludesPartialDelivery()
    {
        Assert.Equal(0, (int)SaleDeliveryStatus.Delivered);
        Assert.Equal(1, (int)SaleDeliveryStatus.PendingPickup);
        Assert.Equal(2, (int)SaleDeliveryStatus.PartiallyDelivered);
    }

    [Fact]
    public void DeliveredQuantity_DefaultsToZero()
    {
        Assert.Equal(0m, new SaleItem().DeliveredQuantity);

        using var context = CreateNpgsqlModelContext();
        var deliveredQuantity = context.Model.FindEntityType(typeof(SaleItem))!
            .FindProperty(nameof(SaleItem.DeliveredQuantity));

        Assert.NotNull(deliveredQuantity);
        Assert.Equal("numeric(18,3)", deliveredQuantity!.GetColumnType());
    }

    [Fact]
    public async Task SaleDelivery_PersistsItemsReferencingAnExistingSale()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new SalesDbContext(options);

        var saleItem = new SaleItem
        {
            Id = 2,
            SaleId = 1,
            ProductId = 7,
            ProductName = "Test product",
            Quantity = 3m
        };
        var sale = new Sale
        {
            Id = 1,
            Status = SaleStatus.Completed,
            Items = { saleItem }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var delivery = new SaleDelivery
        {
            SaleId = sale.Id,
            DeliveredAt = DateTime.UtcNow,
            DeliveredByName = "Test cashier",
            Items =
            {
                new SaleDeliveryItem
                {
                    SaleItemId = saleItem.Id,
                    ProductId = saleItem.ProductId,
                    ProductName = saleItem.ProductName,
                    QuantityDelivered = 1m
                }
            }
        };
        context.SaleDeliveries.Add(delivery);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        var persistedDelivery = await context.SaleDeliveries
            .Include(candidate => candidate.Items)
            .SingleAsync(candidate => candidate.Id == delivery.Id);

        Assert.Equal(sale.Id, persistedDelivery.SaleId);
        var persistedItem = Assert.Single(persistedDelivery.Items);
        Assert.Equal(saleItem.Id, persistedItem.SaleItemId);
        Assert.Equal(1m, persistedItem.QuantityDelivered);
    }

    [Fact]
    public void DeliveryEntities_UseRestrictiveParentsAndCascadeDeliveryLines()
    {
        using var context = CreateNpgsqlModelContext();
        var deliveryEntity = context.Model.FindEntityType(typeof(SaleDelivery))!;
        var deliverySaleForeignKey = Assert.Single(deliveryEntity.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Sale));
        var deliveryUserForeignKey = Assert.Single(deliveryEntity.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(User));

        Assert.Equal(DeleteBehavior.Restrict, deliverySaleForeignKey.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict, deliveryUserForeignKey.DeleteBehavior);
        Assert.True(deliveryUserForeignKey.Properties.Single().IsNullable);
        var deliveredByName = deliveryEntity.FindProperty(nameof(SaleDelivery.DeliveredByName));
        Assert.NotNull(deliveredByName);
        Assert.False(deliveredByName!.IsNullable);
        Assert.Equal("text", deliveredByName.GetColumnType());
        Assert.Contains("IX_SaleDeliveries_SaleId", deliveryEntity.GetIndexes()
            .Select(index => index.GetDatabaseName()));

        var deliveryItemEntity = context.Model.FindEntityType(typeof(SaleDeliveryItem))!;
        var headerForeignKey = Assert.Single(deliveryItemEntity.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(SaleDelivery));
        var saleItemForeignKey = Assert.Single(deliveryItemEntity.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(SaleItem));

        Assert.Equal(DeleteBehavior.Cascade, headerForeignKey.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict, saleItemForeignKey.DeleteBehavior);
        Assert.Equal("numeric(18,3)", deliveryItemEntity.FindProperty(nameof(SaleDeliveryItem.QuantityDelivered))!.GetColumnType());
        Assert.Contains("IX_SaleDeliveryItems_SaleDeliveryId", deliveryItemEntity.GetIndexes()
            .Select(index => index.GetDatabaseName()));
        Assert.Contains("IX_SaleDeliveryItems_SaleItemId", deliveryItemEntity.GetIndexes()
            .Select(index => index.GetDatabaseName()));
    }

    [Fact]
    public void SaleItem_UsesXminAsAConcurrencyToken()
    {
        using var context = CreateNpgsqlModelContext();

        var saleItemEntity = context.Model.FindEntityType(typeof(SaleItem));
        Assert.NotNull(saleItemEntity);

        var version = saleItemEntity!.FindProperty("xmin");
        Assert.NotNull(version);
        Assert.True(version!.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, version.ValueGenerated);
    }

    [Fact]
    public void SaleItem_DoesNotMapXminForSqliteProvider()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new SalesDbContext(options);

        var saleItemEntity = context.Model.FindEntityType(typeof(SaleItem));
        Assert.NotNull(saleItemEntity);
        Assert.Null(saleItemEntity!.FindProperty("xmin"));
    }

    [Fact]
    public void CustodyMigration_DoesNotCreateOrDropTheSystemXminColumn()
    {
        using var context = CreateNpgsqlModelContext();
        var migrationInfo = context.GetService<IMigrationsAssembly>().Migrations
            .Single(migration => migration.Key.EndsWith("_AddCustodyPartialDeliveries", StringComparison.Ordinal));
        var migration = (Migration)Activator.CreateInstance(migrationInfo.Value.AsType())!;

        Assert.DoesNotContain(migration.UpOperations.OfType<AddColumnOperation>(), operation => operation.Name == "xmin");
        Assert.DoesNotContain(migration.DownOperations.OfType<DropColumnOperation>(), operation => operation.Name == "xmin");
    }

    private static SalesDbContext CreateNpgsqlModelContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused")
            .Options;
        return new SalesDbContext(options);
    }
}
