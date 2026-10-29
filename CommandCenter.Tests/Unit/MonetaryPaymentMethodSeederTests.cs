using System;
using System.Linq;
using System.Threading.Tasks;
using Core.Constants;
using Core.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.211-T3c3b3 (S7/S11): a fresh Single 2dp install must get a usable payment-method catalog in
// the primary currency. Legacy seed duplicates are deactivated only when unused and the whole
// operation is idempotent: a used method is never rewritten.
public class MonetaryPaymentMethodSeederTests
{
    private const string CashMethod = "Efectivo";
    private const string CardMethod = "Tarjeta (Punto de Venta)";
    private const string TransferMethod = "Transferencia / Pago Móvil";

    public enum UsedMethodSource
    {
        SalePayment,
        CashTransaction,
        ClosureDetail,
        ReceivablePayment
    }

    private static SalesDbContext CreateSalesDb()
        => new(new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    private static MonetaryProfileDto SingleProfile(string currencyCode) => new()
    {
        Mode = MonetaryMode.Single,
        PrimaryCurrencyCode = currencyCode,
        PrimaryCurrencySymbol = "$",
        SecondaryCurrencyCode = null,
        SecondaryCurrencySymbol = null,
        IsConfigured = true
    };

    private static MonetaryProfileDto DualProfile() => new()
    {
        Mode = MonetaryMode.Dual,
        PrimaryCurrencyCode = "USD",
        PrimaryCurrencySymbol = "$",
        SecondaryCurrencyCode = "VES",
        SecondaryCurrencySymbol = "Bs.S",
        IsConfigured = true
    };

    private static PaymentMethod[] ProductionLikeSeeds() => new[]
    {
        new PaymentMethod { Name = CashMethod, IsActive = true, IsCash = true, RequiresReference = false, DisplayOrder = 1, Currency = "Bs.S" },
        new PaymentMethod { Name = CardMethod, IsActive = true, IsCash = false, RequiresReference = true, DisplayOrder = 2, Currency = "Bs.S" },
        new PaymentMethod { Name = TransferMethod, IsActive = true, IsCash = false, RequiresReference = true, DisplayOrder = 3, Currency = "Bs.S" },
        new PaymentMethod { Name = "Divisas (USD)", IsActive = true, IsCash = true, RequiresReference = false, DisplayOrder = 4, Currency = "USD" },
        new PaymentMethod { Name = "Cash", IsActive = true, IsCash = true, RequiresReference = false, DisplayOrder = 5, Currency = "Bs.S" },
        new PaymentMethod { Name = "Card", IsActive = true, IsCash = false, RequiresReference = true, DisplayOrder = 6, Currency = "Bs.S" }
    };

    [Fact]
    public async Task EnsureForProfile_SingleCopOnEmptyCatalog_CreatesThreeCopMethods()
    {
        using var db = CreateSalesDb();
        var seeder = new MonetaryPaymentMethodSeeder(db);

        await seeder.EnsureForProfileAsync(SingleProfile("COP"));

        var methods = await db.PaymentMethods.AsNoTracking()
            .Where(m => !m.IsDeleted)
            .ToListAsync();

        Assert.Equal(3, methods.Count);
        Assert.All(methods, m => Assert.Equal("COP", m.Currency));
        Assert.All(methods, m => Assert.True(m.IsActive));
        Assert.Equal(1, methods.Count(m => m.IsCash && !m.RequiresReference));
        Assert.Equal(2, methods.Count(m => !m.IsCash && m.RequiresReference));
        var ordered = methods.OrderBy(m => m.DisplayOrder).ToList();
        Assert.Equal(new[] { CashMethod, CardMethod, TransferMethod }, ordered.Select(m => m.Name));
        Assert.Equal(new[] { 1, 2, 3 }, ordered.Select(m => m.DisplayOrder));
    }

    [Fact]
    public async Task EnsureForProfile_ProductionLikeSeedsOnSingleCop_ReusesCanonicalAndDeactivatesLegacy()
    {
        using var db = CreateSalesDb();
        db.PaymentMethods.AddRange(ProductionLikeSeeds());
        await db.SaveChangesAsync();
        var seeder = new MonetaryPaymentMethodSeeder(db);

        await seeder.EnsureForProfileAsync(SingleProfile("COP"));

        var methods = await db.PaymentMethods.AsNoTracking().ToListAsync();
        Assert.Equal(6, methods.Count);

        var active = methods.Where(m => m.IsActive && !m.IsDeleted).ToList();
        Assert.Equal(3, active.Count);
        Assert.All(active, m => Assert.Equal("COP", m.Currency));
        Assert.Equal(
            new[] { CashMethod, CardMethod, TransferMethod },
            active.OrderBy(m => m.DisplayOrder).Select(m => m.Name));

        Assert.All(
            methods.Where(m => m.Name is "Cash" or "Card" or "Divisas (USD)"),
            m => Assert.False(m.IsActive));

        await seeder.EnsureForProfileAsync(SingleProfile("COP"));

        var afterSecondRun = await db.PaymentMethods.AsNoTracking().ToListAsync();
        Assert.Equal(6, afterSecondRun.Count);
        Assert.Equal(3, afterSecondRun.Count(m => m.IsActive && !m.IsDeleted));
    }

    [Theory]
    [InlineData(UsedMethodSource.SalePayment)]
    [InlineData(UsedMethodSource.CashTransaction)]
    [InlineData(UsedMethodSource.ClosureDetail)]
    [InlineData(UsedMethodSource.ReceivablePayment)]
    public async Task EnsureForProfile_WhenCanonicalMethodIsUsed_LeavesItUntouchedAndCreatesSuffixedCash(
        UsedMethodSource source)
    {
        using var db = CreateSalesDb();
        var used = new PaymentMethod
        {
            Name = CashMethod,
            IsActive = true,
            IsCash = true,
            RequiresReference = false,
            DisplayOrder = 1,
            Currency = "Bs.S"
        };
        db.PaymentMethods.Add(used);
        await db.SaveChangesAsync();
        await AddUsageAsync(db, source, used.Id);

        var seeder = new MonetaryPaymentMethodSeeder(db);
        await seeder.EnsureForProfileAsync(SingleProfile("COP"));

        var original = await db.PaymentMethods.AsNoTracking().SingleAsync(m => m.Id == used.Id);
        Assert.Equal(CashMethod, original.Name);
        Assert.Equal("Bs.S", original.Currency);
        Assert.True(original.IsCash);
        Assert.False(original.RequiresReference);
        Assert.True(original.IsActive);
        Assert.Equal(1, original.DisplayOrder);

        var suffixed = await db.PaymentMethods.AsNoTracking()
            .SingleAsync(m => m.Name == $"{CashMethod} COP" && !m.IsDeleted);
        Assert.Equal("COP", suffixed.Currency);
        Assert.True(suffixed.IsCash);
        Assert.False(suffixed.RequiresReference);
        Assert.True(suffixed.IsActive);
        Assert.Equal(2, suffixed.DisplayOrder);

        var activeCashCop = await db.PaymentMethods.AsNoTracking()
            .Where(m => !m.IsDeleted && m.IsActive && m.IsCash && m.Currency == "COP")
            .ToListAsync();
        Assert.Single(activeCashCop);

        var suffixedDisplayOrder = suffixed.DisplayOrder;
        await seeder.EnsureForProfileAsync(SingleProfile("COP"));

        var afterSecondRun = await db.PaymentMethods.AsNoTracking().ToListAsync();
        Assert.Equal(4, afterSecondRun.Count);
        Assert.Equal(
            suffixedDisplayOrder,
            afterSecondRun.Single(m => m.Name == $"{CashMethod} COP").DisplayOrder);
    }

    [Fact]
    public async Task EnsureForProfile_DualProfile_DoesNotChangeTheCatalog()
    {
        using var db = CreateSalesDb();
        db.PaymentMethods.AddRange(
            new PaymentMethod { Name = CashMethod, IsActive = true, IsCash = true, RequiresReference = false, DisplayOrder = 1, Currency = "Bs.S" },
            new PaymentMethod { Name = "Divisas (USD)", IsActive = true, IsCash = true, RequiresReference = false, DisplayOrder = 2, Currency = "USD" });
        await db.SaveChangesAsync();

        var notifier = new Mock<IPaymentMethodNotifier>();
        var seeder = new MonetaryPaymentMethodSeeder(db, notifier: notifier.Object);

        await seeder.EnsureForProfileAsync(DualProfile());

        var methods = await db.PaymentMethods.AsNoTracking().ToListAsync();
        Assert.Equal(2, methods.Count);
        Assert.All(methods, m => Assert.True(m.IsActive));
        Assert.Equal("Bs.S", methods.Single(m => m.Name == CashMethod).Currency);
        notifier.Verify(n => n.NotifyPaymentMethodsUpdatedAsync(), Times.Never);
    }

    [Fact]
    public async Task EnsureForProfile_SingleVes_DoesNotChangeTheCatalog()
    {
        using var db = CreateSalesDb();
        db.PaymentMethods.AddRange(ProductionLikeSeeds());
        await db.SaveChangesAsync();

        var notifier = new Mock<IPaymentMethodNotifier>();
        var seeder = new MonetaryPaymentMethodSeeder(db, notifier: notifier.Object);

        await seeder.EnsureForProfileAsync(SingleProfile("VES"));

        var methods = await db.PaymentMethods.AsNoTracking().ToListAsync();
        Assert.Equal(6, methods.Count);
        Assert.All(methods, m => Assert.True(m.IsActive));
        notifier.Verify(n => n.NotifyPaymentMethodsUpdatedAsync(), Times.Never);
    }

    [Fact]
    public async Task EnsureForProfile_SingleUsd_ReassignsCanonicalMethodsAndDeactivatesLegacyDuplicates()
    {
        using var db = CreateSalesDb();
        db.PaymentMethods.AddRange(ProductionLikeSeeds());
        await db.SaveChangesAsync();
        var seeder = new MonetaryPaymentMethodSeeder(db);

        await seeder.EnsureForProfileAsync(SingleProfile("USD"));

        var active = await db.PaymentMethods.AsNoTracking()
            .Where(m => !m.IsDeleted && m.IsActive)
            .OrderBy(m => m.DisplayOrder)
            .ToListAsync();
        Assert.Equal(new[] { CashMethod, CardMethod, TransferMethod }, active.Select(m => m.Name));
        Assert.All(active, m => Assert.Equal("USD", m.Currency));

        var legacyNames = new[] { "Cash", "Card", "Divisas (USD)" };
        var legacy = await db.PaymentMethods.AsNoTracking()
            .Where(m => legacyNames.Contains(m.Name))
            .ToListAsync();
        Assert.Equal(3, legacy.Count);
        Assert.All(legacy, m => Assert.False(m.IsActive));
    }

    [Fact]
    public async Task EnsureForProfile_WhenCatalogChanges_InvalidatesCacheAndNotifies()
    {
        using var db = CreateSalesDb();
        var cache = new Mock<IMemoryCache>();
        var notifier = new Mock<IPaymentMethodNotifier>();
        var seeder = new MonetaryPaymentMethodSeeder(db, cache.Object, notifier.Object);

        await seeder.EnsureForProfileAsync(SingleProfile("COP"));

        cache.Verify(c => c.Remove(CacheKeys.ActivePaymentMethods), Times.Once);
        cache.Verify(c => c.Remove(CacheKeys.AllPaymentMethods), Times.Once);
        notifier.Verify(n => n.NotifyPaymentMethodsUpdatedAsync(), Times.Once);

        await seeder.EnsureForProfileAsync(SingleProfile("COP"));

        notifier.Verify(n => n.NotifyPaymentMethodsUpdatedAsync(), Times.Once);
    }

    private static async Task AddUsageAsync(
        SalesDbContext db,
        UsedMethodSource source,
        int paymentMethodId)
    {
        switch (source)
        {
            case UsedMethodSource.SalePayment:
                db.SalePayments.Add(new SalePayment
                {
                    SaleId = 1,
                    PaymentMethodId = paymentMethodId,
                    Amount = 1m,
                    AmountBsS = 1m
                });
                break;

            case UsedMethodSource.CashTransaction:
                db.CashTransactions.Add(new CashTransaction
                {
                    SessionId = 1,
                    Type = CashTransactionType.Income,
                    Source = CashTransactionSource.ManualAdjustment,
                    AmountUsd = 1m,
                    AmountLocal = 1m,
                    PaymentMethodId = paymentMethodId
                });
                break;

            case UsedMethodSource.ClosureDetail:
                db.ClosureDetails.Add(new ClosureDetail
                {
                    DailyClosureId = 1,
                    PaymentMethodId = paymentMethodId,
                    PaymentMethodName = CashMethod
                });
                break;

            case UsedMethodSource.ReceivablePayment:
                db.ReceivablePayments.Add(new ReceivablePayment
                {
                    CustomerId = 1,
                    AmountUSD = 1m,
                    AmountBsS = 1m,
                    PaymentRate = 1m,
                    PaymentMethodId = paymentMethodId
                });
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, null);
        }

        await db.SaveChangesAsync();
    }
}
