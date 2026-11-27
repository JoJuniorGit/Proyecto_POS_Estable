using CommandCenter.Tests.Builders;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using System.Data.Common;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 14 (F2-S5): resolución en bloque de reglas de comisión por conjunto de PaymentMethodId
/// para checkout multipago y lote de abonos. El contexto se carga una vez y el snapshot se
/// calcula por pago con la misma matemática del resolver individual.
/// </summary>
public class PaymentMethodFeeResolverBatchTests
{
    private static readonly DateTime PaymentDateUtc = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private const string LocalCurrency = "VES";
    private const int CardMethodId = 10;
    private const int CashMethodId = 11;
    private const int NoRuleMethodId = 12;
    private const int MissingMethodId = 13;
    private const int DeletedMethodId = 14;

    [Fact]
    public async Task LoadRuleContextsAsync_ReturnsContextsForExistingMethodsAndOmitsMissingOnes()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedMethodAsync(context, CashMethodId, isCash: true);
        await SeedMethodAsync(context, NoRuleMethodId, isCash: false);
        await SeedMethodAsync(context, DeletedMethodId, isCash: false, isDeleted: true);
        await SeedRuleAsync(context, CardMethodId, percentage: 1.5m, currencyCode: "USD");
        await SeedRuleAsync(context, CashMethodId, percentage: 5m, currencyCode: "USD");

        var contexts = await PaymentMethodFeeResolver.LoadRuleContextsAsync(
            context,
            new[] { CardMethodId, CashMethodId, NoRuleMethodId, MissingMethodId, DeletedMethodId },
            PaymentDateUtc);

        Assert.Equal(3, contexts.Count);
        Assert.False(contexts[CardMethodId].IsCash);
        Assert.NotNull(contexts[CardMethodId].Rule);
        Assert.True(contexts[CashMethodId].IsCash);
        Assert.Null(contexts[CashMethodId].Rule);
        Assert.Null(contexts[NoRuleMethodId].Rule);
        Assert.False(contexts.ContainsKey(MissingMethodId));
        Assert.False(contexts.ContainsKey(DeletedMethodId));
    }

    [Fact]
    public async Task LoadRuleContextsAsync_EmptyIds_ReturnsEmptyDictionary()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);

        var contexts = await PaymentMethodFeeResolver.LoadRuleContextsAsync(
            context,
            Array.Empty<int>(),
            PaymentDateUtc);

        Assert.Empty(contexts);
    }

    [Fact]
    public async Task LoadRuleContextsAsync_ExpiredAndFutureRules_AreIgnored()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedMethodAsync(context, NoRuleMethodId, isCash: false);
        await SeedRuleAsync(
            context,
            CardMethodId,
            percentage: 1m,
            effectiveFromUtc: PaymentDateUtc.AddDays(-10),
            effectiveToUtc: PaymentDateUtc.AddDays(-1));
        await SeedRuleAsync(
            context,
            NoRuleMethodId,
            percentage: 2m,
            effectiveFromUtc: PaymentDateUtc.AddDays(1));

        var contexts = await PaymentMethodFeeResolver.LoadRuleContextsAsync(
            context,
            new[] { CardMethodId, NoRuleMethodId },
            PaymentDateUtc);

        Assert.Null(contexts[CardMethodId].Rule);
        Assert.Null(contexts[NoRuleMethodId].Rule);
    }

    [Fact]
    public async Task LoadRuleContextsAsync_OverlappingActiveRules_ThrowsConfigurationError()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(
            context,
            CardMethodId,
            percentage: 1m,
            effectiveFromUtc: PaymentDateUtc.AddDays(-3));
        await SeedRuleAsync(
            context,
            CardMethodId,
            percentage: 2m,
            effectiveFromUtc: PaymentDateUtc.AddDays(-1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PaymentMethodFeeResolver.LoadRuleContextsAsync(
                context,
                new[] { CardMethodId },
                PaymentDateUtc));

        Assert.Contains("comisión", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadRuleContextsAsync_CashMethodWithOverlappingRules_DoesNotThrow()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CashMethodId, isCash: true);
        await SeedRuleAsync(
            context,
            CashMethodId,
            percentage: 1m,
            effectiveFromUtc: PaymentDateUtc.AddDays(-3));
        await SeedRuleAsync(
            context,
            CashMethodId,
            percentage: 2m,
            effectiveFromUtc: PaymentDateUtc.AddDays(-1));

        var contexts = await PaymentMethodFeeResolver.LoadRuleContextsAsync(
            context,
            new[] { CashMethodId },
            PaymentDateUtc);

        Assert.True(contexts[CashMethodId].IsCash);
        Assert.Null(contexts[CashMethodId].Rule);
    }

    [Fact]
    public async Task ComputeSnapshot_UsdRule_MatchesSingleResolverSnapshot()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(context, CardMethodId, percentage: 1.5m, currencyCode: "USD");

        var contexts = await PaymentMethodFeeResolver.LoadRuleContextsAsync(
            context,
            new[] { CardMethodId },
            PaymentDateUtc);

        var batchSnapshot = PaymentMethodFeeResolver.ComputeSnapshot(
            contexts[CardMethodId],
            amountUsd: 1.00m,
            amountLocal: 4000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        var singleSnapshot = await PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 1.00m,
            amountLocal: 4000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        Assert.Equal(singleSnapshot, batchSnapshot);
        Assert.Equal(0.0150m, batchSnapshot.FeeAmountUsd);
        Assert.Equal(60.00m, batchSnapshot.FeeAmountLocal);
    }

    [Fact]
    public async Task ComputeSnapshot_LocalRule_MatchesSingleResolverSnapshotAndPrecision()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(context, CardMethodId, percentage: 2m, currencyCode: LocalCurrency);

        var contexts = await PaymentMethodFeeResolver.LoadRuleContextsAsync(
            context,
            new[] { CardMethodId },
            PaymentDateUtc);

        var batchSnapshot = PaymentMethodFeeResolver.ComputeSnapshot(
            contexts[CardMethodId],
            amountUsd: 0.25m,
            amountLocal: 1000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        var singleSnapshot = await PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 0.25m,
            amountLocal: 1000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        Assert.Equal(singleSnapshot, batchSnapshot);
        Assert.Equal(20.00m, batchSnapshot.FeeAmountLocal);
        Assert.Equal(0.0050m, batchSnapshot.FeeAmountUsd);
    }

    [Fact]
    public async Task ComputeSnapshot_CashMethodWithRule_ReturnsNone()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CashMethodId, isCash: true);
        await SeedRuleAsync(context, CashMethodId, percentage: 5m, currencyCode: "USD");

        var contexts = await PaymentMethodFeeResolver.LoadRuleContextsAsync(
            context,
            new[] { CashMethodId },
            PaymentDateUtc);

        var snapshot = PaymentMethodFeeResolver.ComputeSnapshot(
            contexts[CashMethodId],
            amountUsd: 100m,
            amountLocal: 400000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        Assert.False(snapshot.FeeConfigured);
        Assert.Equal(0m, snapshot.FeeAmountUsd);
        Assert.Equal(0m, snapshot.FeeAmountLocal);
    }

    [Fact]
    public async Task ComputeSnapshot_NoActiveRule_ReturnsNone()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, NoRuleMethodId, isCash: false);

        var contexts = await PaymentMethodFeeResolver.LoadRuleContextsAsync(
            context,
            new[] { NoRuleMethodId },
            PaymentDateUtc);

        var snapshot = PaymentMethodFeeResolver.ComputeSnapshot(
            contexts[NoRuleMethodId],
            amountUsd: 100m,
            amountLocal: 400000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        Assert.False(snapshot.FeeConfigured);
        Assert.Null(snapshot.RuleId);
        Assert.Equal(0m, snapshot.FeeAmountUsd);
    }

    [Fact]
    public async Task LoadRuleContextsAsync_MultipleMethods_ResolvesEachRuleIndependently()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedMethodAsync(context, NoRuleMethodId, isCash: false);
        await SeedRuleAsync(context, CardMethodId, percentage: 1.5m, currencyCode: "USD");
        await SeedRuleAsync(context, NoRuleMethodId, percentage: 2m, currencyCode: LocalCurrency);

        var contexts = await PaymentMethodFeeResolver.LoadRuleContextsAsync(
            context,
            new[] { CardMethodId, NoRuleMethodId },
            PaymentDateUtc);

        var cardSnapshot = PaymentMethodFeeResolver.ComputeSnapshot(
            contexts[CardMethodId], 1.00m, 4000m, 4000m, LocalCurrency);
        var localSnapshot = PaymentMethodFeeResolver.ComputeSnapshot(
            contexts[NoRuleMethodId], 0.25m, 1000m, 4000m, LocalCurrency);

        Assert.Equal(0.0150m, cardSnapshot.FeeAmountUsd);
        Assert.Equal(20.00m, localSnapshot.FeeAmountLocal);
    }

    [Fact]
    public async Task ResolveAsync_MissingMethod_StillThrowsConfigurationError()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PaymentMethodFeeResolver.ResolveAsync(
                context,
                MissingMethodId,
                PaymentDateUtc,
                amountUsd: 100m,
                amountLocal: 400000m,
                rate: 4000m,
                localCurrencyCode: LocalCurrency));

        Assert.Contains("método de pago", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadRuleContextsAsync_ThreeDistinctMethods_ExecutesExactlyTwoQueries()
    {
        const int cardA = 21;
        const int cardB = 22;
        const int cardC = 23;

        var interceptor = new ReaderCommandCounterInterceptor();
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        using var context = new SalesDbContext(options);
        context.Database.EnsureCreated();

        await SeedMethodAsync(context, cardA, isCash: false);
        await SeedMethodAsync(context, cardB, isCash: false);
        await SeedMethodAsync(context, cardC, isCash: false);
        await SeedRuleAsync(context, cardA, percentage: 1m);
        await SeedRuleAsync(context, cardB, percentage: 1.5m);
        await SeedRuleAsync(context, cardC, percentage: 2m);

        interceptor.Reset();

        var contexts = await PaymentMethodFeeResolver.LoadRuleContextsAsync(
            context,
            new[] { cardA, cardB, cardC },
            PaymentDateUtc);

        Assert.Equal(3, contexts.Count);
        Assert.Equal(2, interceptor.ReaderCount);
    }

    private sealed class ReaderCommandCounterInterceptor : DbCommandInterceptor
    {
        public int ReaderCount { get; private set; }

        public void Reset() => ReaderCount = 0;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ReaderCount++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCount++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static async Task SeedMethodAsync(
        SalesDbContext context,
        int id,
        bool isCash,
        bool isDeleted = false)
    {
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = id,
            Name = isCash ? $"Efectivo {id}" : $"Tarjeta {id}",
            IsCash = isCash,
            IsActive = true,
            IsDeleted = isDeleted,
            Currency = isCash ? "USD" : "Bs.S",
            DisplayOrder = id
        });
        await context.SaveChangesAsync();
    }

    private static async Task SeedRuleAsync(
        SalesDbContext context,
        int paymentMethodId,
        decimal percentage,
        string currencyCode = "USD",
        DateTime? effectiveFromUtc = null,
        DateTime? effectiveToUtc = null,
        bool isActive = true)
    {
        context.PaymentMethodFeeRules.Add(new PaymentMethodFeeRule
        {
            PaymentMethodId = paymentMethodId,
            FeeType = PaymentMethodFeeType.Percentage,
            Percentage = percentage,
            CurrencyCode = currencyCode,
            EffectiveFromUtc = effectiveFromUtc ?? PaymentDateUtc.AddDays(-1),
            EffectiveToUtc = effectiveToUtc,
            IsActive = isActive
        });
        await context.SaveChangesAsync();
    }
}
