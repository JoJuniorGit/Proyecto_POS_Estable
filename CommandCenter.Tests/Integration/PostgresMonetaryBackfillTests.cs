using System;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Sales.Module.Entities;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.211-T6-U17: seeded legacy rows prove the 8.211 currency backfills preserve ISO codes
/// (COP) instead of collapsing them to Bs.S. The test executes the migration SQL against a
/// fully migrated isolated database; amounts and rates are never recalculated.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class PostgresMonetaryBackfillTests
{
    private static async Task ExecuteSqlAsync(Microsoft.EntityFrameworkCore.DbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    [Fact]
    public async Task PaymentAndCashBackfills_PreserveIsoSecondaryCurrencyOnSeededRows()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync();
        await using (var context = database.CreateSalesDbContext())
        {
            var method = new PaymentMethod
            {
                Id = 7001,
                Name = "Efectivo COP",
                Currency = "COP",
                IsCash = true,
                IsActive = true
            };
            var sale = new Sale
            {
                Id = 7001,
                Status = SaleStatus.Completed,
                Date = DateTime.UtcNow,
                Subtotal = 10m,
                TotalUSD = 10m,
                TotalBsS = 40000m,
                AppliedRate = 4000m
            };
            var payment = new SalePayment
            {
                SaleId = sale.Id,
                PaymentMethodId = method.Id,
                Amount = 10m,
                AmountBsS = 40000m,
                ExchangeRate = 4000m,
                CreatedAt = DateTime.UtcNow,
                CurrencyCode = null
            };
            var session = new CashDrawerSession
            {
                Id = 7001,
                OpenedAt = DateTime.UtcNow,
                Status = CashDrawerStatus.Open,
                OpeningBalanceLocal = 0m,
                OpeningBalanceUsd = 0m,
                OpeningExchangeRate = 4000m
            };
            var cash = new CashTransaction
            {
                SessionId = session.Id,
                PaymentMethodId = method.Id,
                Type = CashTransactionType.Income,
                Source = CashTransactionSource.SalePayment,
                AmountUsd = 10m,
                AmountLocal = 40000m,
                ExchangeRate = 4000m,
                IsPhysicalCash = true,
                TransactionTime = DateTime.UtcNow,
                CurrencyCode = null
            };

            context.PaymentMethods.Add(method);
            context.Sales.Add(sale);
            context.SalePayments.Add(payment);
            context.CashDrawerSessions.Add(session);
            context.CashTransactions.Add(cash);
            await context.SaveChangesAsync();

            var migrations = context.GetService<IMigrationsAssembly>();
            foreach (var key in migrations.Migrations.Keys.Where(key =>
                         key.Contains("AddSalePaymentCurrencySnapshotRev8211", StringComparison.Ordinal)
                         || key.Contains("AddCashTransactionCurrencySnapshotRev8211", StringComparison.Ordinal)))
            {
                var migration = (Migration)Activator.CreateInstance(migrations.Migrations[key].AsType())!;
                foreach (var operation in migration.UpOperations.OfType<SqlOperation>())
                {
                    await ExecuteSqlAsync(context, operation.Sql);
                }
            }
        }

        await using var verify = database.CreateSalesDbContext();
        Assert.Equal("COP", (await verify.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == 7001)).CurrencyCode);
        Assert.Equal("COP", (await verify.CashTransactions.AsNoTracking().SingleAsync(t => t.SessionId == 7001)).CurrencyCode);
        Assert.Equal(40000m, (await verify.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == 7001)).AmountBsS);
        Assert.Equal(40000m, (await verify.CashTransactions.AsNoTracking().SingleAsync(t => t.SessionId == 7001)).AmountLocal);
    }
}


