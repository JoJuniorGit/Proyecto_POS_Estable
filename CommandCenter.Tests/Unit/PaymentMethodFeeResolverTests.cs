using CommandCenter.Tests.Builders;
using Core.DTOs;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 14 (F1-WU4, S7): resolver de la regla de comision de medios de pago. La comision es un
/// costo del negocio: se congela en el snapshot del cobro y nunca modifica el monto cobrado.
/// </summary>
public class PaymentMethodFeeResolverTests
{
    private static readonly DateTime PaymentDateUtc = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private const string LocalCurrency = "VES";
    private const int CardMethodId = 10;
    private const int CashMethodId = 11;

    [Fact]
    public async Task ResolveAsync_UsdPercentage_KeepsFourDecimalUsdPrecision()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(
            context,
            CardMethodId,
            PaymentMethodFeeType.Percentage,
            percentage: 1.5m,
            currencyCode: "USD");

        var snapshot = await PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 1.00m,
            amountLocal: 4000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        Assert.True(snapshot.FeeConfigured);
        Assert.Equal(0.0150m, snapshot.FeeAmountUsd);
        Assert.Equal(60.00m, snapshot.FeeAmountLocal);
        Assert.Equal(4000m, snapshot.FeeRate);
        Assert.Equal(PaymentMethodFeeType.Percentage, snapshot.FeeType);
        Assert.Equal(1.5m, snapshot.FeePercentage);
        Assert.Equal(PaymentDateUtc.AddDays(-1), snapshot.FeeRuleEffectiveFromUtc);
    }

    [Fact]
    public async Task ResolveAsync_UsdFixed_ConvertsWholeFixedAmountToLocal()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(
            context,
            CardMethodId,
            PaymentMethodFeeType.Fixed,
            fixedAmount: 0.10m,
            currencyCode: "USD");

        var snapshot = await PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 1.00m,
            amountLocal: 4000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        Assert.True(snapshot.FeeConfigured);
        Assert.Equal(0.10m, snapshot.FeeAmountUsd);
        Assert.Equal(400.00m, snapshot.FeeAmountLocal);
    }

    [Fact]
    public async Task ResolveAsync_UsdPercentagePlusFixed_AddsBothComponents()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(
            context,
            CardMethodId,
            PaymentMethodFeeType.PercentagePlusFixed,
            percentage: 1.5m,
            fixedAmount: 0.10m,
            currencyCode: "USD");

        var snapshot = await PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 100m,
            amountLocal: 400000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        Assert.Equal(1.60m, snapshot.FeeAmountUsd);
        Assert.Equal(6400.00m, snapshot.FeeAmountLocal);
    }

    [Fact]
    public async Task ResolveAsync_LocalPercentage_UsesLocalBaseAndFourDecimalUsdEquivalent()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(
            context,
            CardMethodId,
            PaymentMethodFeeType.Percentage,
            percentage: 2m,
            currencyCode: LocalCurrency);

        var snapshot = await PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 0.25m,
            amountLocal: 1000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        Assert.True(snapshot.FeeConfigured);
        Assert.Equal(20.00m, snapshot.FeeAmountLocal);
        Assert.Equal(0.0050m, snapshot.FeeAmountUsd);
    }

    [Fact]
    public async Task ResolveAsync_WithoutActiveRule_ReturnsNoneWithoutAmounts()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);

        var snapshot = await PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 100m,
            amountLocal: 400000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        Assert.False(snapshot.FeeConfigured);
        Assert.Equal(0m, snapshot.FeeAmountUsd);
        Assert.Equal(0m, snapshot.FeeAmountLocal);
        Assert.Null(snapshot.RuleId);
    }

    [Fact]
    public async Task ResolveAsync_CashMethodWithRule_ReturnsNone()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CashMethodId, isCash: true);
        await SeedRuleAsync(
            context,
            CashMethodId,
            PaymentMethodFeeType.Percentage,
            percentage: 5m,
            currencyCode: "USD");

        var snapshot = await PaymentMethodFeeResolver.ResolveAsync(
            context,
            CashMethodId,
            PaymentDateUtc,
            amountUsd: 100m,
            amountLocal: 400000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        Assert.False(snapshot.FeeConfigured);
        Assert.Equal(0m, snapshot.FeeAmountUsd);
        Assert.Equal(0m, snapshot.FeeAmountLocal);
    }

    [Fact]
    public async Task ResolveAsync_ExpiredRule_ReturnsNone()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(
            context,
            CardMethodId,
            PaymentMethodFeeType.Percentage,
            percentage: 1.5m,
            effectiveFromUtc: PaymentDateUtc.AddDays(-10),
            effectiveToUtc: PaymentDateUtc.AddDays(-1));

        var snapshot = await PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 100m,
            amountLocal: 400000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        Assert.False(snapshot.FeeConfigured);
    }

    [Fact]
    public async Task ResolveAsync_FutureRule_ReturnsNone()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(
            context,
            CardMethodId,
            PaymentMethodFeeType.Percentage,
            percentage: 1.5m,
            effectiveFromUtc: PaymentDateUtc.AddDays(1));

        var snapshot = await PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 100m,
            amountLocal: 400000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        Assert.False(snapshot.FeeConfigured);
    }

    [Fact]
    public async Task ResolveAsync_RuleEndingExactlyAtPaymentDate_ReturnsNone()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(
            context,
            CardMethodId,
            PaymentMethodFeeType.Percentage,
            percentage: 1.5m,
            effectiveFromUtc: PaymentDateUtc.AddDays(-10),
            effectiveToUtc: PaymentDateUtc);

        var snapshot = await PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 100m,
            amountLocal: 400000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        Assert.False(snapshot.FeeConfigured);
    }

    [Fact]
    public async Task ResolveAsync_OverlappingActiveRules_Throws()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(
            context,
            CardMethodId,
            PaymentMethodFeeType.Percentage,
            percentage: 1m,
            effectiveFromUtc: PaymentDateUtc.AddDays(-3));
        await SeedRuleAsync(
            context,
            CardMethodId,
            PaymentMethodFeeType.Percentage,
            percentage: 2m,
            effectiveFromUtc: PaymentDateUtc.AddDays(-1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 100m,
            amountLocal: 400000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency));

        Assert.Contains("comisión", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_UnsupportedRuleCurrency_Throws()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(
            context,
            CardMethodId,
            PaymentMethodFeeType.Percentage,
            percentage: 1m,
            currencyCode: "EUR");

        await Assert.ThrowsAsync<InvalidOperationException>(() => PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 100m,
            amountLocal: 400000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency));
    }

    [Fact]
    public async Task ResolveAsync_MissingPaymentMethod_ThrowsConfigurationError()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => PaymentMethodFeeResolver.ResolveAsync(
            context,
            9999,
            PaymentDateUtc,
            amountUsd: 100m,
            amountLocal: 400000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency));

        Assert.Contains("método de pago", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_PercentageRuleWithoutPercentage_Throws()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(
            context,
            CardMethodId,
            PaymentMethodFeeType.Percentage,
            percentage: null,
            currencyCode: "USD");

        await Assert.ThrowsAsync<InvalidOperationException>(() => PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 100m,
            amountLocal: 400000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency));
    }

    [Fact]
    public async Task ResolveAsync_PercentageAboveOneHundred_Throws()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(
            context,
            CardMethodId,
            PaymentMethodFeeType.Percentage,
            percentage: 150m,
            currencyCode: "USD");

        await Assert.ThrowsAsync<InvalidOperationException>(() => PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 100m,
            amountLocal: 400000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency));
    }

    [Fact]
    public async Task ResolveAsync_PercentagePlusFixedWithNegativeFixed_Throws()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(
            context,
            CardMethodId,
            PaymentMethodFeeType.PercentagePlusFixed,
            percentage: 1m,
            fixedAmount: -100m,
            currencyCode: "USD");

        await Assert.ThrowsAsync<InvalidOperationException>(() => PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 100m,
            amountLocal: 400000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency));
    }

    [Fact]
    public async Task ResolveAsync_UsdPercentageMidpoint_RoundsAwayFromZero()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(
            context,
            CardMethodId,
            PaymentMethodFeeType.Percentage,
            percentage: 1.225m,
            currencyCode: "USD");

        var snapshot = await PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 1.00m,
            amountLocal: 4000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        // 1.00 * 1.225% = 0.01225 -> 4dp AwayFromZero = 0.0123 (ToEven daria 0.0122).
        Assert.Equal(0.0123m, snapshot.FeeAmountUsd);
    }

    [Fact]
    public async Task ResolveAsync_RuleStartingExactlyAtPaymentDate_IsActive()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedRuleAsync(
            context,
            CardMethodId,
            PaymentMethodFeeType.Percentage,
            percentage: 1m,
            currencyCode: "USD",
            effectiveFromUtc: PaymentDateUtc);

        var snapshot = await PaymentMethodFeeResolver.ResolveAsync(
            context,
            CardMethodId,
            PaymentDateUtc,
            amountUsd: 100m,
            amountLocal: 400000m,
            rate: 4000m,
            localCurrencyCode: LocalCurrency);

        Assert.True(snapshot.FeeConfigured);
    }

    [Fact]
    public void ResolveLocalCurrencyCode_DualUsesSecondaryAndSingleUsesPrimary()
    {
        var dual = new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "USD",
            SecondaryCurrencyCode = "COP"
        };
        var dualWithoutSecondary = new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "USD",
            SecondaryCurrencyCode = null
        };
        var single = new MonetaryProfileDto
        {
            Mode = MonetaryMode.Single,
            PrimaryCurrencyCode = "COP",
            SecondaryCurrencyCode = null
        };

        Assert.Equal("COP", PaymentMethodFeeResolver.ResolveLocalCurrencyCode(dual));
        Assert.Equal("USD", PaymentMethodFeeResolver.ResolveLocalCurrencyCode(dualWithoutSecondary));
        Assert.Equal("COP", PaymentMethodFeeResolver.ResolveLocalCurrencyCode(single));
    }

    [Fact]
    public void ApplyFeeSnapshot_CopiesAllTenFieldsToBothPaymentTypes()
    {
        var snapshot = new PaymentMethodFeeSnapshot(
            RuleId: 7,
            FeeType: PaymentMethodFeeType.PercentagePlusFixed,
            FeePercentage: 1.5m,
            FeeFixedAmount: 0.10m,
            FeeCurrencyCode: "USD",
            FeeAmountUsd: 1.60m,
            FeeAmountLocal: 6400.00m,
            FeeRate: 4000m,
            FeeConfigured: true,
            FeeRuleEffectiveFromUtc: PaymentDateUtc);

        var salePayment = new SalePayment();
        salePayment.ApplyFeeSnapshot(snapshot);

        Assert.Equal(7, salePayment.PaymentMethodFeeRuleId);
        Assert.Equal(PaymentMethodFeeType.PercentagePlusFixed, salePayment.FeeType);
        Assert.Equal(1.5m, salePayment.FeePercentage);
        Assert.Equal(0.10m, salePayment.FeeFixedAmount);
        Assert.Equal("USD", salePayment.FeeCurrencyCode);
        Assert.Equal(1.60m, salePayment.FeeAmountUsd);
        Assert.Equal(6400.00m, salePayment.FeeAmountLocal);
        Assert.Equal(4000m, salePayment.FeeRate);
        Assert.True(salePayment.FeeConfigured);
        Assert.Equal(PaymentDateUtc, salePayment.FeeRuleEffectiveFromUtc);

        var receivablePayment = new ReceivablePayment();
        receivablePayment.ApplyFeeSnapshot(snapshot);

        Assert.Equal(7, receivablePayment.PaymentMethodFeeRuleId);
        Assert.Equal(PaymentMethodFeeType.PercentagePlusFixed, receivablePayment.FeeType);
        Assert.Equal(1.5m, receivablePayment.FeePercentage);
        Assert.Equal(0.10m, receivablePayment.FeeFixedAmount);
        Assert.Equal("USD", receivablePayment.FeeCurrencyCode);
        Assert.Equal(1.60m, receivablePayment.FeeAmountUsd);
        Assert.Equal(6400.00m, receivablePayment.FeeAmountLocal);
        Assert.Equal(4000m, receivablePayment.FeeRate);
        Assert.True(receivablePayment.FeeConfigured);
        Assert.Equal(PaymentDateUtc, receivablePayment.FeeRuleEffectiveFromUtc);
    }

    private static async Task SeedMethodAsync(SalesDbContext context, int id, bool isCash)
    {
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = id,
            Name = isCash ? $"Efectivo {id}" : $"Tarjeta {id}",
            IsCash = isCash,
            IsActive = true,
            IsDeleted = false,
            Currency = isCash ? "USD" : "Bs.S",
            DisplayOrder = id
        });
        await context.SaveChangesAsync();
    }

    private static async Task SeedRuleAsync(
        SalesDbContext context,
        int paymentMethodId,
        PaymentMethodFeeType feeType,
        decimal? percentage = null,
        decimal? fixedAmount = null,
        string currencyCode = "USD",
        DateTime? effectiveFromUtc = null,
        DateTime? effectiveToUtc = null,
        bool isActive = true)
    {
        context.PaymentMethodFeeRules.Add(new PaymentMethodFeeRule
        {
            PaymentMethodId = paymentMethodId,
            FeeType = feeType,
            Percentage = percentage,
            FixedAmount = fixedAmount,
            CurrencyCode = currencyCode,
            EffectiveFromUtc = effectiveFromUtc ?? PaymentDateUtc.AddDays(-1),
            EffectiveToUtc = effectiveToUtc,
            IsActive = isActive
        });
        await context.SaveChangesAsync();
    }
}
