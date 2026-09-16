using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class DailyClosureServiceWindowTests
{
    private static readonly TimeZoneInfo VetTz = Core.Helpers.TimeZoneHelper.GetVenezuelaTimeZone();

    private SalesDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private DailyClosureService CreateService(SalesDbContext context)
    {
        return new DailyClosureService(context);
    }

    private static DateTime ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), VetTz);
    }

    [Fact]
    public async Task GetExpectedTotals_SalesBeforeSessionAreExcluded()
    {
        using var context = CreateInMemoryContext();
        await SeedPaymentMethods(context);

        var queryUtc = ToUtc(new DateOnly(2026, 9, 15), new TimeOnly(0, 15, 0));

        var sessionOpenedAt = ToUtc(new DateOnly(2026, 9, 14), new TimeOnly(20, 0, 0));
        context.CashDrawerSessions.Add(new CashDrawerSession
        {
            Id = 1,
            OpenedAt = sessionOpenedAt,
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = 0m,
            OpeningExchangeRate = 36.50m
        });

        var saleBeforeSession = new Sale
        {
            Id = 1,
            Status = SaleStatus.Completed,
            Date = ToUtc(new DateOnly(2026, 9, 14), new TimeOnly(19, 0, 0)),
            TotalUSD = 10m,
            TotalBsS = 365m,
            AppliedRate = 36.50m
        };
        context.Sales.Add(saleBeforeSession);
        context.SalePayments.Add(new SalePayment
        {
            Id = 1,
            SaleId = 1,
            PaymentMethodId = 1,
            AmountBsS = 365m
        });

        var saleInWindow = new Sale
        {
            Id = 2,
            Status = SaleStatus.Completed,
            Date = ToUtc(new DateOnly(2026, 9, 14), new TimeOnly(22, 0, 0)),
            TotalUSD = 5m,
            TotalBsS = 182.50m,
            AppliedRate = 36.50m
        };
        context.Sales.Add(saleInWindow);
        context.SalePayments.Add(new SalePayment
        {
            Id = 2,
            SaleId = 2,
            PaymentMethodId = 1,
            AmountBsS = 182.50m
        });

        await context.SaveChangesAsync();

        var service = CreateService(context);
        var totals = await service.GetExpectedTotalsByPaymentMethodAsync(queryUtc);

        var cashTotal = totals.Find(t => t.PaymentMethodId == 1);
        Assert.NotNull(cashTotal);
        Assert.Equal(182.50m, cashTotal.ExpectedAmountBsS);
    }

    [Fact]
    public async Task GetExpectedTotals_IncludesSaleExactlyAtSessionOpenedAt()
    {
        using var context = CreateInMemoryContext();
        await SeedPaymentMethods(context);

        var queryUtc = ToUtc(new DateOnly(2026, 9, 15), new TimeOnly(12, 0, 0));

        var sessionOpenedAt = ToUtc(new DateOnly(2026, 9, 15), new TimeOnly(22, 0, 0));
        context.CashDrawerSessions.Add(new CashDrawerSession
        {
            Id = 1,
            OpenedAt = sessionOpenedAt,
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = 0m,
            OpeningExchangeRate = 36.50m
        });

        var saleAtStart = new Sale
        {
            Id = 1,
            Status = SaleStatus.Completed,
            Date = sessionOpenedAt,
            TotalUSD = 10m,
            TotalBsS = 365m,
            AppliedRate = 36.50m
        };
        context.Sales.Add(saleAtStart);
        context.SalePayments.Add(new SalePayment
        {
            Id = 1,
            SaleId = 1,
            PaymentMethodId = 1,
            AmountBsS = 365m
        });

        await context.SaveChangesAsync();

        var service = CreateService(context);
        var totals = await service.GetExpectedTotalsByPaymentMethodAsync(queryUtc);

        var cashTotal = totals.Find(t => t.PaymentMethodId == 1);
        Assert.NotNull(cashTotal);
        Assert.Equal(365m, cashTotal.ExpectedAmountBsS);
    }

    [Fact]
    public async Task GetExpectedTotals_ExcludesSaleExactlyAtEndExclusiveUtc()
    {
        using var context = CreateInMemoryContext();
        await SeedPaymentMethods(context);

        var queryUtc = ToUtc(new DateOnly(2026, 9, 15), new TimeOnly(12, 0, 0));

        var sessionOpenedAt = ToUtc(new DateOnly(2026, 9, 14), new TimeOnly(20, 0, 0));
        context.CashDrawerSessions.Add(new CashDrawerSession
        {
            Id = 1,
            OpenedAt = sessionOpenedAt,
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = 0m,
            OpeningExchangeRate = 36.50m
        });

        var endExclusiveUtc = ToUtc(new DateOnly(2026, 9, 16), TimeOnly.MinValue);

        var saleAtEnd = new Sale
        {
            Id = 1,
            Status = SaleStatus.Completed,
            Date = endExclusiveUtc,
            TotalUSD = 10m,
            TotalBsS = 365m,
            AppliedRate = 36.50m
        };
        context.Sales.Add(saleAtEnd);
        context.SalePayments.Add(new SalePayment
        {
            Id = 1,
            SaleId = 1,
            PaymentMethodId = 1,
            AmountBsS = 365m
        });

        await context.SaveChangesAsync();

        var service = CreateService(context);
        var totals = await service.GetExpectedTotalsByPaymentMethodAsync(queryUtc);

        var cashTotal = totals.Find(t => t.PaymentMethodId == 1);
        Assert.NotNull(cashTotal);
        Assert.Equal(0m, cashTotal.ExpectedAmountBsS);
    }

    [Fact]
    public async Task PersistedClosure_IsNeverRewrittenByLaterArqueo()
    {
        using var context = CreateInMemoryContext();
        await SeedPaymentMethods(context);

        var closureDate = ToUtc(new DateOnly(2026, 9, 15), new TimeOnly(0, 10, 0));
        var originalExpected = 500m;

        context.DailyClosures.Add(new DailyClosure
        {
            Id = 1,
            ClosureDate = closureDate,
            UserId = "Admin",
            ExchangeRate = 36.50m,
            TotalExpectedBsS = originalExpected,
            TotalActualBsS = 498m,
            TotalDifferenceBsS = -2m,
            Details = new List<ClosureDetail>
            {
                new ClosureDetail
                {
                    PaymentMethodId = 1,
                    PaymentMethodName = "Efectivo USD",
                    ExpectedAmountBsS = originalExpected,
                    ActualAmountBsS = 498m,
                    DifferenceBsS = -2m
                }
            }
        });

        await context.SaveChangesAsync();

        var persistedClosure = await context.DailyClosures
            .Include(dc => dc.Details)
            .FirstAsync(dc => dc.Id == 1);

        Assert.Equal(originalExpected, persistedClosure.Details[0].ExpectedAmountBsS);
        Assert.Equal(498m, persistedClosure.Details[0].ActualAmountBsS);
        Assert.Equal(-2m, persistedClosure.Details[0].DifferenceBsS);

        var service = CreateService(context);
        await service.GetExpectedTotalsByPaymentMethodAsync(closureDate);

        var afterPreview = await context.DailyClosures
            .Include(dc => dc.Details)
            .FirstAsync(dc => dc.Id == 1);

        Assert.Equal(originalExpected, afterPreview.Details[0].ExpectedAmountBsS);
        Assert.Equal(498m, afterPreview.Details[0].ActualAmountBsS);
        Assert.Equal(-2m, afterPreview.Details[0].DifferenceBsS);
    }

    [Fact]
    public async Task GetExpectedTotals_CashTransactionExactlyAtStart_Included()
    {
        using var context = CreateInMemoryContext();
        await SeedPaymentMethods(context);

        var queryUtc = ToUtc(new DateOnly(2026, 9, 15), new TimeOnly(12, 0, 0));
        var sessionOpenedAt = ToUtc(new DateOnly(2026, 9, 15), new TimeOnly(0, 0, 0));

        context.CashDrawerSessions.Add(new CashDrawerSession
        {
            Id = 1,
            OpenedAt = sessionOpenedAt,
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = 0m,
            OpeningExchangeRate = 36.50m
        });

        context.CashTransactions.Add(new CashTransaction
        {
            Id = 1,
            SessionId = 1,
            TransactionTime = sessionOpenedAt,
            Type = CashTransactionType.Expense,
            Source = CashTransactionSource.SalePayment,
            IsPhysicalCash = true,
            PaymentMethodId = 1,
            AmountLocal = 50m
        });

        await context.SaveChangesAsync();

        var service = CreateService(context);
        var totals = await service.GetExpectedTotalsByPaymentMethodAsync(queryUtc);

        var cashTotal = totals.Find(t => t.PaymentMethodId == 1);
        Assert.NotNull(cashTotal);
        Assert.Equal(-50m, cashTotal.ExpectedAmountBsS);
    }

    [Fact]
    public async Task GetExpectedTotals_CashTransactionExactlyAtEndExclusive_Excluded()
    {
        using var context = CreateInMemoryContext();
        await SeedPaymentMethods(context);

        var queryUtc = ToUtc(new DateOnly(2026, 9, 15), new TimeOnly(12, 0, 0));
        var sessionOpenedAt = ToUtc(new DateOnly(2026, 9, 14), new TimeOnly(20, 0, 0));
        var endExclusiveUtc = ToUtc(new DateOnly(2026, 9, 16), TimeOnly.MinValue);

        context.CashDrawerSessions.Add(new CashDrawerSession
        {
            Id = 1,
            OpenedAt = sessionOpenedAt,
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = 0m,
            OpeningExchangeRate = 36.50m
        });

        context.CashTransactions.Add(new CashTransaction
        {
            Id = 1,
            SessionId = 1,
            TransactionTime = endExclusiveUtc,
            Type = CashTransactionType.Expense,
            Source = CashTransactionSource.SalePayment,
            IsPhysicalCash = true,
            PaymentMethodId = 1,
            AmountLocal = 50m
        });

        await context.SaveChangesAsync();

        var service = CreateService(context);
        var totals = await service.GetExpectedTotalsByPaymentMethodAsync(queryUtc);

        var cashTotal = totals.Find(t => t.PaymentMethodId == 1);
        Assert.NotNull(cashTotal);
        Assert.Equal(0m, cashTotal.ExpectedAmountBsS);
    }

    private static async Task SeedPaymentMethods(SalesDbContext context)
    {
        if (!await context.PaymentMethods.AnyAsync())
        {
            context.PaymentMethods.AddRange(
                new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true, IsActive = true, DisplayOrder = 1 },
                new PaymentMethod { Id = 2, Name = "Efectivo Bs.S", IsCash = true, IsActive = true, DisplayOrder = 2 },
                new PaymentMethod { Id = 3, Name = "Punto de Venta", IsCash = false, IsActive = true, DisplayOrder = 3 }
            );
            await context.SaveChangesAsync();
        }
    }
}
