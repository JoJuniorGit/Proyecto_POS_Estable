using System;
using System.Threading.Tasks;
using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Entities;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// Fase 4 del carril PostgreSQL real (8.188): la inmutabilidad del snapshot histórico se ejercita
/// sobre una base AISLADA por test (migraciones reales de Sales + Inventory), nunca sobre la base
/// apuntada por TEST_POSTGRES_CONNECTION. Sin conexión configurada el carril falla EXPLÍCITO.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class HistoryImmutabilityTests
{
    private const int SaleId = 99001;

    [Fact]
    public async Task CompletedSaleSnapshot_AfterRateChange_RemainsUnchanged()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(
            applyMigrations: true,
            applyInventoryMigrations: true);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using (var sales = database.CreateSalesDbContext())
        {
            sales.Sales.Add(new Sale
            {
                Id = SaleId,
                InvoiceNumber = 99001,
                Date = DateTime.UtcNow.AddDays(-1),
                Status = SaleStatus.Completed,
                PriceListType = "Retail",
                DeliveryStatus = SaleDeliveryStatus.Delivered,
                Subtotal = 10m,
                TotalUSD = 10m,
                TotalBsS = 730m,
                SubtotalBsS = 730m,
                AppliedRate = 73m,
                RoundingAdjustment = 0m,
                FinalPaidAmountBsS = 730m
            });
            await sales.SaveChangesAsync();
        }

        await using (var inventory = database.CreateInventoryDbContext())
        {
            inventory.ExchangeRateHistory.Add(new ExchangeRateHistory
            {
                Date = today,
                Rate = 75m,
                UpdatedAt = DateTime.UtcNow
            });
            await inventory.SaveChangesAsync();
        }

        await using var salesRead = database.CreateSalesDbContext();
        var sale = await salesRead.Sales
            .AsNoTracking()
            .SingleAsync(s => s.Id == SaleId);

        Assert.Equal(73m, sale.AppliedRate);
        Assert.Equal(10m, sale.TotalUSD);
        Assert.Equal(730m, sale.TotalBsS);
        Assert.Equal(730m, sale.FinalPaidAmountBsS);
        Assert.Equal(0m, sale.RoundingAdjustment);
    }
}
