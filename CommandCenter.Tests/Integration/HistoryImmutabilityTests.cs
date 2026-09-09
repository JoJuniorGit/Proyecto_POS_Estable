using System;
using System.Threading.Tasks;
using Core.Entities;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Xunit;

namespace CommandCenter.Tests.Integration;

[Trait("Category", "RequiresDocker")]
public class HistoryImmutabilityTests
{
    private const int SaleId = 99001;

    private static string ConnectionString() =>
        Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION") ?? string.Empty;

    private static SalesDbContext CreateSales(string connString) =>
        new(new DbContextOptionsBuilder<SalesDbContext>().UseNpgsql(connString).Options);

    private static InventoryDbContext CreateInventory(string connString) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(connString).Options);

    [Fact]
    public async Task CompletedSaleSnapshot_AfterRateChange_RemainsUnchanged()
    {
        var connStr = ConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
                throw new InvalidOperationException("TEST_POSTGRES_CONNECTION no definida en CI.");
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        decimal? originalRate = null;

        try
        {
            await using (var sales = CreateSales(connStr))
            {
                await sales.Database.EnsureCreatedAsync();
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

            await using (var inventory = CreateInventory(connStr))
            {
                await inventory.Database.EnsureCreatedAsync();
                await inventory.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS "ExchangeRateHistory" (
                        "Date" date NOT NULL,
                        "Rate" numeric(18,4) NOT NULL,
                        "UpdatedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "PK_ExchangeRateHistory" PRIMARY KEY ("Date")
                    );
                    """);
                var existing = await inventory.ExchangeRateHistory
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Date == today);
                originalRate = existing?.Rate;
                var rate = inventory.ExchangeRateHistory.FirstOrDefault(e => e.Date == today);
                if (rate == null)
                {
                    inventory.ExchangeRateHistory.Add(new ExchangeRateHistory
                    {
                        Date = today,
                        Rate = 75m,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    rate.Rate = 75m;
                    rate.UpdatedAt = DateTime.UtcNow;
                }
                await inventory.SaveChangesAsync();
            }

            await using var salesRead = CreateSales(connStr);
            var sale = await salesRead.Sales
                .AsNoTracking()
                .SingleAsync(s => s.Id == SaleId);

            Assert.Equal(73m, sale.AppliedRate);
            Assert.Equal(10m, sale.TotalUSD);
            Assert.Equal(730m, sale.TotalBsS);
            Assert.Equal(730m, sale.FinalPaidAmountBsS);
            Assert.Equal(0m, sale.RoundingAdjustment);
        }
        finally
        {
            await using (var sales = CreateSales(connStr))
            {
                var seeded = await sales.Sales.FirstOrDefaultAsync(s => s.Id == SaleId);
                if (seeded != null)
                {
                    sales.Sales.Remove(seeded);
                    await sales.SaveChangesAsync();
                }
            }

            await using (var inventory = CreateInventory(connStr))
            {
                var rate = await inventory.ExchangeRateHistory.FirstOrDefaultAsync(e => e.Date == today);
                if (rate != null)
                {
                    if (originalRate.HasValue)
                    {
                        rate.Rate = originalRate.Value;
                        rate.UpdatedAt = DateTime.UtcNow;
                    }
                    else
                    {
                        inventory.ExchangeRateHistory.Remove(rate);
                    }
                    await inventory.SaveChangesAsync();
                }
            }
        }
    }
}