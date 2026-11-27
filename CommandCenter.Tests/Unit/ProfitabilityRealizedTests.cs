using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs.Profitability;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 14 (F2-WU3, F2-S3): motor percibido en caja. El CMV se prorratea por factura con
/// cobertura de costo completa usando cobros de contado y abonos CxC; el ingreso con costo
/// desconocido queda segregado fuera del margen realizado. La extension de WU3 aplica
/// prorrateo por producto seleccionado (SelectedRevenue/SelectedCogs) sin tocar las lineas
/// globales de comisiones, caja chica y fees.
/// </summary>
public class ProfitabilityRealizedTests
{
    private const string TimeZoneId = "Profitability/UTC-4";

    private static readonly TimeZoneInfo FixedTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        TimeZoneId,
        TimeSpan.FromHours(-4),
        "Profitability UTC-4",
        "Profitability UTC-4");

    private static readonly DateTime FromUtc = new(2026, 11, 2, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ToUtc = new(2026, 11, 3, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime InWindow = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetSummaryAsync_DualCashSale_ProratesKnownCogs()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.Add(CreateSale(1, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                unitCostUsd: 60m, unitCostLocal: 3000m)));
        context.SalePayments.Add(CreatePayment(1, saleId: 1, createdAt: InWindow,
            amountUsd: 100m, amountBsS: 5000m, rate: 50m, feeUsd: 0m, feeLocal: 0m,
            feeConfigured: false, paymentMethodId: 2));

        await context.SaveChangesAsync();

        var realized = (await CreateService(context, MonetaryMode.Dual)
            .GetSummaryAsync(CreateWindowFilter())).Realized;

        Assert.Equal(100m, realized.CollectedRevenue);
        Assert.Equal(100m, realized.KnownCostCollectedRevenue);
        Assert.Equal(0m, realized.UnknownCostCollectedRevenue);
        Assert.Equal(60m, realized.RealizedCogs);
        Assert.Equal(40m, realized.RealizedGrossProfit);
        Assert.Equal(40m, realized.RealizedGrossMarginPercent);
        Assert.Equal(0m, realized.ServiceCommissions);
        Assert.Equal(0m, realized.FxDifference);
        Assert.Equal(0m, realized.PettyCashExpenses);
        Assert.Equal(0m, realized.PaymentMethodFees);
        Assert.Equal(40m, realized.NetProfit);
        Assert.Equal(40m, realized.NetMarginPercent);
    }

    [Fact]
    public async Task GetSummaryAsync_DualCreditSale_IncludesInitialPaymentAndAllocation()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.Add(CreateSale(1, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                unitCostUsd: 60m, unitCostLocal: 3000m)));
        context.Receivables.Add(CreateReceivable(1, saleId: 1, issuedAtUtc: InWindow));
        context.SalePayments.Add(CreatePayment(1, saleId: 1, createdAt: InWindow,
            amountUsd: 20m, amountBsS: 1000m, rate: 50m, feeUsd: 0m, feeLocal: 0m,
            feeConfigured: false, paymentMethodId: 3));
        context.ReceivablePayments.Add(CreateReceivablePayment(1,
            receivedAtUtc: InWindow.AddMinutes(30), amountUsd: 80m, amountBsS: 4000m, rate: 50m));
        context.CreditPaymentAllocations.Add(CreateAllocation(1,
            receivablePaymentId: 1, receivableId: 1, amountUsd: 80m, amountBsS: 4000m));

        await context.SaveChangesAsync();

        var realized = (await CreateService(context, MonetaryMode.Dual)
            .GetSummaryAsync(CreateWindowFilter())).Realized;

        Assert.Equal(100m, realized.CollectedRevenue);
        Assert.Equal(100m, realized.KnownCostCollectedRevenue);
        Assert.Equal(0m, realized.UnknownCostCollectedRevenue);
        Assert.Equal(60m, realized.RealizedCogs);
        Assert.Equal(40m, realized.RealizedGrossProfit);
        Assert.Equal(40m, realized.RealizedGrossMarginPercent);
        Assert.Equal(40m, realized.NetProfit);
    }

    [Fact]
    public async Task GetSummaryAsync_UnknownCostSale_KeepsCollectedOutsideMargin()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.Add(CreateSale(1, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                unitCostUsd: null, unitCostLocal: null)));
        context.SalePayments.Add(CreatePayment(1, saleId: 1, createdAt: InWindow,
            amountUsd: 100m, amountBsS: 5000m, rate: 50m, feeUsd: 0m, feeLocal: 0m,
            feeConfigured: false, paymentMethodId: 3));

        await context.SaveChangesAsync();

        var realized = (await CreateService(context, MonetaryMode.Dual)
            .GetSummaryAsync(CreateWindowFilter())).Realized;

        Assert.Equal(100m, realized.CollectedRevenue);
        Assert.Equal(0m, realized.KnownCostCollectedRevenue);
        Assert.Equal(100m, realized.UnknownCostCollectedRevenue);
        Assert.Equal(0m, realized.RealizedCogs);
        Assert.Equal(0m, realized.RealizedGrossProfit);
        Assert.Equal(0m, realized.RealizedGrossMarginPercent);
        Assert.Equal(0m, realized.NetProfit);
        Assert.Equal(0m, realized.NetMarginPercent);
    }

    [Fact]
    public async Task GetSummaryAsync_OverpaymentRefund_ReducesCollectedButNotCogs()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.Add(CreateSale(1, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                unitCostUsd: 60m, unitCostLocal: 3000m)));
        context.SalePayments.Add(CreatePayment(1, saleId: 1, createdAt: InWindow,
            amountUsd: 100m, amountBsS: 5000m, rate: 50m, feeUsd: 0m, feeLocal: 0m,
            feeConfigured: false, paymentMethodId: 3));
        // Pure overpayment refund: no returned item revenue, so NetTotal stays 100 and the
        // refund is a cash outflow that does not reduce RealizedCogs.
        context.CreditReturns.Add(CreateReturn(1, saleId: 1, createdAt: InWindow.AddHours(2),
            returnedUsd: 0m, refundRate: 50m, refundedUsd: 10m, refundedBsS: 500m));

        await context.SaveChangesAsync();

        var realized = (await CreateService(context, MonetaryMode.Dual)
            .GetSummaryAsync(CreateWindowFilter())).Realized;

        Assert.Equal(90m, realized.CollectedRevenue);
        Assert.Equal(90m, realized.KnownCostCollectedRevenue);
        Assert.Equal(0m, realized.UnknownCostCollectedRevenue);
        Assert.Equal(60m, realized.RealizedCogs);
        Assert.Equal(30m, realized.RealizedGrossProfit);
        Assert.Equal(30m / 90m * 100m, realized.RealizedGrossMarginPercent);
        Assert.Equal(30m, realized.NetProfit);
    }

    [Fact]
    public async Task GetSummaryAsync_FxDifference_IsRealizedOutsideGrossProfit()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.ReceivablePayments.Add(CreateReceivablePayment(1,
            receivedAtUtc: InWindow, amountUsd: 80m, amountBsS: 4000m, rate: 50m,
            fxDifferenceBsS: 400m));

        await context.SaveChangesAsync();

        var realized = (await CreateService(context, MonetaryMode.Dual)
            .GetSummaryAsync(CreateWindowFilter())).Realized;

        Assert.Equal(0m, realized.CollectedRevenue);
        Assert.Equal(0m, realized.RealizedGrossProfit);
        Assert.Equal(8m, realized.FxDifference);
        Assert.Equal(8m, realized.NetProfit);
        Assert.Equal(0m, realized.NetMarginPercent);
    }

    [Fact]
    public async Task GetSummaryAsync_CommissionsPettyCashAndFeeReversals_NetProfit()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.AddRange(
            CreateSale(1, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
                items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                    unitCostUsd: 60m, unitCostLocal: 3000m)),
            CreateSale(2, InWindow, totalUsd: 0m, totalBsS: 0m, rate: 50m,
                saleType: SaleType.CashAdvance, commissionUsd: 1.4m, commissionLocal: 70m),
            // A sale collected before the window and cancelled inside it: its original fee must
            // reverse in this period (S10), but its payment is not a current-period collection.
            CreateSale(3, new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc),
                totalUsd: 50m, totalBsS: 2500m, rate: 50m, cancelledAtUtc: InWindow.AddHours(3)));

        context.SalePayments.AddRange(
            CreatePayment(1, saleId: 1, createdAt: InWindow, amountUsd: 100m, amountBsS: 5000m,
                rate: 50m, feeUsd: 2m, feeLocal: 100m, feeConfigured: true, paymentMethodId: 3),
            CreatePayment(2, saleId: 3, createdAt: new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc),
                amountUsd: 50m, amountBsS: 2500m, rate: 50m, feeUsd: 1.5m, feeLocal: 75m,
                feeConfigured: true, paymentMethodId: 3));

        context.PettyCashExpenses.Add(CreateExpense(1, createdAtUtc: InWindow.AddHours(4),
            status: PettyCashStatus.Active, amountUsd: 10m, amountBsS: 500m));

        await context.SaveChangesAsync();

        var realized = (await CreateService(context, MonetaryMode.Dual)
            .GetSummaryAsync(CreateWindowFilter())).Realized;

        Assert.Equal(100m, realized.CollectedRevenue);
        Assert.Equal(100m, realized.KnownCostCollectedRevenue);
        Assert.Equal(60m, realized.RealizedCogs);
        Assert.Equal(40m, realized.RealizedGrossProfit);
        Assert.Equal(1.4m, realized.ServiceCommissions);
        Assert.Equal(0m, realized.FxDifference);
        Assert.Equal(10m, realized.PettyCashExpenses);
        // 2.00 current fee - 1.50 reversal for the sale cancelled inside the window.
        Assert.Equal(0.5m, realized.PaymentMethodFees);
        Assert.Equal(30.9m, realized.NetProfit);
        Assert.Equal(30.9m, realized.NetMarginPercent);
    }

    [Fact]
    public async Task GetSummaryAsync_SinglePrimary_UsesLocalSnapshots()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.AddRange(
            CreateSale(1, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
                items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                    unitCostUsd: 60m, unitCostLocal: 3000m)),
            CreateSale(2, InWindow, totalUsd: 0m, totalBsS: 0m, rate: 50m,
                saleType: SaleType.CashAdvance, commissionUsd: 1.4m, commissionLocal: 70m));

        context.SalePayments.Add(CreatePayment(1, saleId: 1, createdAt: InWindow,
            amountUsd: 100m, amountBsS: 5000m, rate: 50m, feeUsd: 2m, feeLocal: 100m,
            feeConfigured: true, paymentMethodId: 3));
        context.PettyCashExpenses.Add(CreateExpense(1, createdAtUtc: InWindow.AddHours(4),
            status: PettyCashStatus.Active, amountUsd: 10m, amountBsS: 500m));
        context.ReceivablePayments.Add(CreateReceivablePayment(1,
            receivedAtUtc: InWindow.AddMinutes(30), amountUsd: 80m, amountBsS: 4000m, rate: 50m,
            fxDifferenceBsS: 400m));

        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Single).GetSummaryAsync(CreateWindowFilter());
        var realized = summary.Realized;

        Assert.Equal("COP", summary.ReportCurrencyCode);
        Assert.True(summary.IsSingleCurrency);
        Assert.Equal(5000m, realized.CollectedRevenue);
        Assert.Equal(5000m, realized.KnownCostCollectedRevenue);
        Assert.Equal(3000m, realized.RealizedCogs);
        Assert.Equal(2000m, realized.RealizedGrossProfit);
        Assert.Equal(40m, realized.RealizedGrossMarginPercent);
        Assert.Equal(70m, realized.ServiceCommissions);
        Assert.Equal(400m, realized.FxDifference);
        Assert.Equal(500m, realized.PettyCashExpenses);
        Assert.Equal(100m, realized.PaymentMethodFees);
        Assert.Equal(1870m, realized.NetProfit);
        Assert.Equal(37.4m, realized.NetMarginPercent);
    }

    [Fact]
    public async Task GetSummaryAsync_StrictUtcWindow_IncludesFromAndExcludesTo()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.AddRange(
            CreateSale(1, InWindow, totalUsd: 200m, totalBsS: 10000m, rate: 50m,
                items: CreateItem(1, productId: 10, quantity: 2m, subtotalUsd: 200m, subtotalBsS: 10000m,
                    unitCostUsd: 30m, unitCostLocal: 1500m)),
            CreateSale(2, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
                items: CreateItem(2, productId: 20, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                    unitCostUsd: 60m, unitCostLocal: 3000m)));

        context.Receivables.Add(CreateReceivable(1, saleId: 2, issuedAtUtc: InWindow));

        context.SalePayments.AddRange(
            CreatePayment(1, saleId: 1, createdAt: FromUtc, amountUsd: 100m, amountBsS: 5000m,
                rate: 50m, feeUsd: 0m, feeLocal: 0m, feeConfigured: false, paymentMethodId: 3),
            CreatePayment(2, saleId: 1, createdAt: ToUtc, amountUsd: 100m, amountBsS: 5000m,
                rate: 50m, feeUsd: 0m, feeLocal: 0m, feeConfigured: false, paymentMethodId: 3));

        context.ReceivablePayments.AddRange(
            CreateReceivablePayment(1, receivedAtUtc: FromUtc, amountUsd: 80m, amountBsS: 4000m, rate: 50m),
            CreateReceivablePayment(2, receivedAtUtc: ToUtc, amountUsd: 80m, amountBsS: 4000m, rate: 50m));
        context.CreditPaymentAllocations.AddRange(
            CreateAllocation(1, receivablePaymentId: 1, receivableId: 1, amountUsd: 80m, amountBsS: 4000m),
            CreateAllocation(2, receivablePaymentId: 2, receivableId: 1, amountUsd: 80m, amountBsS: 4000m));

        context.CreditReturns.Add(CreateReturn(1, saleId: 1, createdAt: ToUtc,
            returnedUsd: 0m, refundRate: 50m, refundedUsd: 5m, refundedBsS: 250m));

        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(CreateWindowFilter());
        var realized = summary.Realized;

        Assert.Equal(FromUtc, summary.FromUtc);
        Assert.Equal(ToUtc, summary.ToUtc);
        // Sale 1: 100 received at fromUtc; the 100 at toUtc and the refund at toUtc stay outside.
        // Sale 2: 80 allocated at fromUtc; the 80 at toUtc stays outside.
        Assert.Equal(180m, realized.CollectedRevenue);
        Assert.Equal(180m, realized.KnownCostCollectedRevenue);
        Assert.Equal(78m, realized.RealizedCogs);
        Assert.Equal(102m, realized.RealizedGrossProfit);
    }

    [Fact]
    public async Task GetSummaryAsync_PaymentMethodFilter_SelectsPaymentsAllocationsAndFees()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.AddRange(
            CreateSale(1, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
                items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                    unitCostUsd: 60m, unitCostLocal: 3000m)),
            CreateSale(2, InWindow, totalUsd: 50m, totalBsS: 2500m, rate: 50m,
                items: CreateItem(2, productId: 20, quantity: 1m, subtotalUsd: 50m, subtotalBsS: 2500m,
                    unitCostUsd: 30m, unitCostLocal: 1500m)),
            CreateSale(3, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
                items: CreateItem(3, productId: 30, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                    unitCostUsd: 60m, unitCostLocal: 3000m)));

        context.Receivables.Add(CreateReceivable(1, saleId: 3, issuedAtUtc: InWindow));

        context.SalePayments.AddRange(
            CreatePayment(1, saleId: 1, createdAt: InWindow, amountUsd: 100m, amountBsS: 5000m,
                rate: 50m, feeUsd: 2m, feeLocal: 100m, feeConfigured: true, paymentMethodId: 3),
            CreatePayment(2, saleId: 2, createdAt: InWindow, amountUsd: 50m, amountBsS: 2500m,
                rate: 50m, feeUsd: 0m, feeLocal: 0m, feeConfigured: false, paymentMethodId: 2));

        context.ReceivablePayments.Add(CreateReceivablePayment(1,
            receivedAtUtc: InWindow.AddMinutes(30), amountUsd: 80m, amountBsS: 4000m, rate: 50m,
            feeUsd: 0.9m, feeLocal: 45m, feeConfigured: true, paymentMethodId: 2));
        context.CreditPaymentAllocations.Add(CreateAllocation(1,
            receivablePaymentId: 1, receivableId: 1, amountUsd: 80m, amountBsS: 4000m));

        await context.SaveChangesAsync();

        var filter = CreateWindowFilter();
        filter.PaymentMethodIds = new[] { 3 };

        var realized = (await CreateService(context, MonetaryMode.Dual)
            .GetSummaryAsync(filter)).Realized;

        Assert.Equal(100m, realized.CollectedRevenue);
        Assert.Equal(100m, realized.KnownCostCollectedRevenue);
        Assert.Equal(60m, realized.RealizedCogs);
        Assert.Equal(40m, realized.RealizedGrossProfit);
        Assert.Equal(2m, realized.PaymentMethodFees);
        Assert.Equal(38m, realized.NetProfit);
    }

    [Fact]
    public async Task GetSummaryAsync_ProductFilter_ProratesRevenueAndCogsAndKeepsGlobalLinesWhole()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        // Mixed sale: product 10 contributes 100 of 160 revenue and 30 of 50 COGS.
        context.Sales.Add(CreateSale(1, InWindow, totalUsd: 160m, totalBsS: 8000m, rate: 50m,
            items: new[]
            {
                CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                    unitCostUsd: 30m, unitCostLocal: 1500m),
                CreateItem(2, productId: 20, quantity: 1m, subtotalUsd: 60m, subtotalBsS: 3000m,
                    unitCostUsd: 20m, unitCostLocal: 1000m)
            }));
        context.SalePayments.Add(CreatePayment(1, saleId: 1, createdAt: InWindow,
            amountUsd: 160m, amountBsS: 8000m, rate: 50m, feeUsd: 2m, feeLocal: 100m,
            feeConfigured: true, paymentMethodId: 3));
        context.Sales.Add(CreateSale(2, InWindow, totalUsd: 0m, totalBsS: 0m, rate: 50m,
            saleType: SaleType.CashAdvance, commissionUsd: 1.4m, commissionLocal: 70m));
        context.PettyCashExpenses.Add(CreateExpense(1, createdAtUtc: InWindow.AddHours(4),
            status: PettyCashStatus.Active, amountUsd: 10m, amountBsS: 500m));

        await context.SaveChangesAsync();

        var filter = CreateWindowFilter();
        filter.ProductIds = new[] { 10 };

        var realized = (await CreateService(context, MonetaryMode.Dual)
            .GetSummaryAsync(filter)).Realized;

        // Payment 160 * (selected revenue 100 / net total 160) = 100 collected.
        Assert.Equal(100m, realized.CollectedRevenue);
        Assert.Equal(100m, realized.KnownCostCollectedRevenue);
        Assert.Equal(0m, realized.UnknownCostCollectedRevenue);
        // Selected COGS 30 / net total 160 -> 160 * 0.1875 = 30.
        Assert.Equal(30m, realized.RealizedCogs);
        Assert.Equal(70m, realized.RealizedGrossProfit);
        Assert.Equal(70m, realized.RealizedGrossMarginPercent);
        // Global operation lines are not prorated by product.
        Assert.Equal(1.4m, realized.ServiceCommissions);
        Assert.Equal(10m, realized.PettyCashExpenses);
        Assert.Equal(2m, realized.PaymentMethodFees);
        Assert.Equal(59.4m, realized.NetProfit);
        Assert.Equal(59.4m, realized.NetMarginPercent);
    }

    [Fact]
    public async Task GetSummaryAsync_ProductFilter_AllocatesRefundBySelectedReturnedItemShare()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.Add(CreateSale(1, InWindow, totalUsd: 160m, totalBsS: 8000m, rate: 50m,
            items: new[]
            {
                CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                    unitCostUsd: 30m, unitCostLocal: 1500m),
                CreateItem(2, productId: 20, quantity: 1m, subtotalUsd: 60m, subtotalBsS: 3000m,
                    unitCostUsd: 20m, unitCostLocal: 1000m)
            }));
        context.SalePayments.Add(CreatePayment(1, saleId: 1, createdAt: InWindow,
            amountUsd: 160m, amountBsS: 8000m, rate: 50m, feeUsd: 0m, feeLocal: 0m,
            feeConfigured: false, paymentMethodId: 3));
        context.CreditReturns.Add(CreateReturn(1, saleId: 1, createdAt: InWindow.AddHours(2),
            returnedUsd: 40m, refundRate: 50m, refundedUsd: 40m, refundedBsS: 2000m,
            CreateReturnItem(1, saleItemId: 1, quantity: 0.1m, amountUsd: 10m),
            CreateReturnItem(2, saleItemId: 2, quantity: 0.5m, amountUsd: 30m)));

        await context.SaveChangesAsync();

        var filter = CreateWindowFilter();
        filter.ProductIds = new[] { 10 };

        var realized = (await CreateService(context, MonetaryMode.Dual)
            .GetSummaryAsync(filter)).Realized;

        // NetTotal = 160 - 40 = 120. The payment is prorated by the original selected revenue
        // (100 / 160 = 0.625) and the refund by the selected returned revenue (10 / 40 = 0.25):
        // 160 * 0.625 - 40 * 0.25 = 100 - 10 = 90.
        Assert.Equal(90m, realized.CollectedRevenue);
        Assert.Equal(90m, realized.KnownCostCollectedRevenue);
        Assert.Equal(0m, realized.UnknownCostCollectedRevenue);
        // Selected net COGS = 30 * (1 - 0.1) = 27 -> 160 * (27 / 120) = 36.
        Assert.Equal(36m, realized.RealizedCogs);
        Assert.Equal(54m, realized.RealizedGrossProfit);
    }

    [Fact]
    public async Task GetSummaryAsync_PaymentMethodFilter_SelectsOnlyMatchingFxDifference()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.Add(CreateSale(1, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                unitCostUsd: 60m, unitCostLocal: 3000m)));
        context.SalePayments.Add(CreatePayment(1, saleId: 1, createdAt: InWindow,
            amountUsd: 100m, amountBsS: 5000m, rate: 50m, feeUsd: 0m, feeLocal: 0m,
            feeConfigured: false, paymentMethodId: 3));
        context.ReceivablePayments.AddRange(
            CreateReceivablePayment(1, receivedAtUtc: InWindow.AddMinutes(10),
                amountUsd: 80m, amountBsS: 4000m, rate: 50m, fxDifferenceBsS: 400m,
                paymentMethodId: 2),
            CreateReceivablePayment(2, receivedAtUtc: InWindow.AddMinutes(20),
                amountUsd: 80m, amountBsS: 4000m, rate: 50m, fxDifferenceBsS: 200m,
                paymentMethodId: 3));

        await context.SaveChangesAsync();

        var filter = CreateWindowFilter();
        filter.PaymentMethodIds = new[] { 3 };

        var realized = (await CreateService(context, MonetaryMode.Dual)
            .GetSummaryAsync(filter)).Realized;

        // Only the method-3 payment contributes FX: 200 / 50 = 4. The method-2 FX (8) is excluded.
        Assert.Equal(4m, realized.FxDifference);
        Assert.Equal(100m, realized.CollectedRevenue);
        Assert.Equal(40m, realized.RealizedGrossProfit);
        Assert.Equal(44m, realized.NetProfit);
    }

    [Fact]
    public async Task GetSummaryAsync_ProductFilter_FullyReturnedSelectedProductNetsToZero()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.Add(CreateSale(1, InWindow, totalUsd: 160m, totalBsS: 8000m, rate: 50m,
            items: new[]
            {
                CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                    unitCostUsd: 30m, unitCostLocal: 1500m),
                CreateItem(2, productId: 20, quantity: 1m, subtotalUsd: 60m, subtotalBsS: 3000m,
                    unitCostUsd: 20m, unitCostLocal: 1000m)
            }));
        context.SalePayments.Add(CreatePayment(1, saleId: 1, createdAt: InWindow,
            amountUsd: 160m, amountBsS: 8000m, rate: 50m, feeUsd: 2m, feeLocal: 100m,
            feeConfigured: true, paymentMethodId: 3));
        context.CreditReturns.Add(CreateReturn(1, saleId: 1, createdAt: InWindow.AddHours(2),
            returnedUsd: 100m, refundRate: 50m, refundedUsd: 100m, refundedBsS: 5000m,
            CreateReturnItem(1, saleItemId: 1, quantity: 1m, amountUsd: 100m)));
        context.Sales.Add(CreateSale(2, InWindow, totalUsd: 0m, totalBsS: 0m, rate: 50m,
            saleType: SaleType.CashAdvance, commissionUsd: 1.4m, commissionLocal: 70m));
        context.PettyCashExpenses.Add(CreateExpense(1, createdAtUtc: InWindow.AddHours(4),
            status: PettyCashStatus.Active, amountUsd: 10m, amountBsS: 500m));

        await context.SaveChangesAsync();

        var filter = CreateWindowFilter();
        filter.ProductIds = new[] { 10 };

        var realized = (await CreateService(context, MonetaryMode.Dual)
            .GetSummaryAsync(filter)).Realized;

        // The selected product is fully returned: payment share 100/160 * 160 = 100 and refund
        // share 100/100 * 100 = 100, so the selected collected revenue nets to zero instead of
        // turning negative. The unselected product remains outside the filtered basis.
        Assert.Equal(0m, realized.CollectedRevenue);
        Assert.Equal(0m, realized.KnownCostCollectedRevenue);
        Assert.Equal(0m, realized.UnknownCostCollectedRevenue);
        Assert.Equal(0m, realized.RealizedCogs);
        Assert.Equal(0m, realized.RealizedGrossProfit);
        // Global operation lines are not affected by the selected product return.
        Assert.Equal(1.4m, realized.ServiceCommissions);
        Assert.Equal(10m, realized.PettyCashExpenses);
        Assert.Equal(2m, realized.PaymentMethodFees);
        Assert.Equal(-10.6m, realized.NetProfit);
    }

    [Fact]
    public async Task GetSummaryAsync_SingleCreditAllocationAndReturn_UsesSaleAppliedRate()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.Add(CreateSale(1, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                unitCostUsd: 60m, unitCostLocal: 3000m)));
        context.Receivables.Add(CreateReceivable(1, saleId: 1, issuedAtUtc: InWindow));
        // The payment rate (60) and the return's RefundRate (60) differ from the sale snapshot
        // rate (50). In Single the returned revenue must use Sale.AppliedRate, while the
        // refunded Bs.S remains the cash amount computed at the payment rate.
        context.ReceivablePayments.Add(CreateReceivablePayment(1,
            receivedAtUtc: InWindow.AddMinutes(30), amountUsd: 100m, amountBsS: 5000m, rate: 60m));
        context.CreditPaymentAllocations.Add(CreateAllocation(1,
            receivablePaymentId: 1, receivableId: 1, amountUsd: 100m, amountBsS: 5000m));
        context.CreditReturns.Add(CreateReturn(1, saleId: 1, createdAt: InWindow.AddHours(2),
            returnedUsd: 20m, refundRate: 60m, refundedUsd: 20m, refundedBsS: 1200m,
            CreateReturnItem(1, saleItemId: 1, quantity: 0.2m, amountUsd: 20m)));

        await context.SaveChangesAsync();

        var realized = (await CreateService(context, MonetaryMode.Single)
            .GetSummaryAsync(CreateWindowFilter())).Realized;

        // Returned revenue = 20 * Sale.AppliedRate (50) = 1000 -> NetTotal 4000.
        Assert.Equal(3800m, realized.CollectedRevenue);
        Assert.Equal(3800m, realized.KnownCostCollectedRevenue);
        // NetCogs = 3000 * 0.8 = 2400 -> 5000 * (2400 / 4000) = 3000.
        Assert.Equal(3000m, realized.RealizedCogs);
        Assert.Equal(800m, realized.RealizedGrossProfit);
    }

    [Fact]
    public async Task GetSummaryAsync_FullyReturnedSale_NetTotalZeroProducesNoCogs()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.Add(CreateSale(1, InWindow, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
                unitCostUsd: 60m, unitCostLocal: 3000m)));
        context.SalePayments.Add(CreatePayment(1, saleId: 1, createdAt: InWindow,
            amountUsd: 100m, amountBsS: 5000m, rate: 50m, feeUsd: 0m, feeLocal: 0m,
            feeConfigured: false, paymentMethodId: 3));
        context.CreditReturns.Add(CreateReturn(1, saleId: 1, createdAt: InWindow.AddHours(2),
            returnedUsd: 100m, refundRate: 50m, refundedUsd: 100m, refundedBsS: 5000m,
            CreateReturnItem(1, saleItemId: 1, quantity: 1m, amountUsd: 100m)));

        await context.SaveChangesAsync();

        var realized = (await CreateService(context, MonetaryMode.Dual)
            .GetSummaryAsync(CreateWindowFilter())).Realized;

        // NetTotal = 100 - 100 = 0: the payment and the refund cancel in the known bucket and
        // RealizedCogs is zero instead of dividing by a non-positive net invoice.
        Assert.Equal(0m, realized.CollectedRevenue);
        Assert.Equal(0m, realized.KnownCostCollectedRevenue);
        Assert.Equal(0m, realized.UnknownCostCollectedRevenue);
        Assert.Equal(0m, realized.RealizedCogs);
        Assert.Equal(0m, realized.RealizedGrossProfit);
        Assert.Equal(0m, realized.RealizedGrossMarginPercent);
        Assert.Equal(0m, realized.NetProfit);
        Assert.Equal(0m, realized.NetMarginPercent);
    }

    private static ProfitabilityFilterDto CreateWindowFilter() => new()
    {
        Preset = ProfitabilityPeriodPreset.Custom,
        FromDate = new DateOnly(2026, 11, 2),
        ToDate = new DateOnly(2026, 11, 2)
    };

    private static ProfitabilityService CreateService(SalesDbContext context, MonetaryMode mode)
    {
        var profile = mode == MonetaryMode.Single
            ? new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "COP",
                PrimaryCurrencySymbol = "COL$",
                SecondaryCurrencyCode = null,
                SecondaryCurrencySymbol = null
            }
            : new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = "USD",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = "VES",
                SecondaryCurrencySymbol = "Bs.S"
            };

        var profileService = new Mock<IMonetaryProfileService>();
        profileService
            .Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        return new ProfitabilityService(context, new StubTimeZoneProvider(), profileService.Object);
    }

    private static Sale CreateSale(
        int id,
        DateTime date,
        decimal totalUsd,
        decimal totalBsS,
        decimal rate,
        DateTime? cancelledAtUtc = null,
        int? cashierId = null,
        string priceListType = "Retail",
        decimal commissionUsd = 0m,
        decimal commissionLocal = 0m,
        SaleType saleType = SaleType.Merchandise,
        bool hasInvoice = true,
        params SaleItem[] items)
    {
        var sale = new Sale
        {
            Id = id,
            Date = date,
            Status = cancelledAtUtc.HasValue ? SaleStatus.Cancelled : SaleStatus.Completed,
            SaleType = saleType,
            InvoiceNumber = hasInvoice ? id : null,
            TotalUSD = totalUsd,
            TotalBsS = totalBsS,
            Subtotal = totalUsd,
            SubtotalBsS = totalBsS,
            AppliedRate = rate,
            CancelledAtUtc = cancelledAtUtc,
            CashierId = cashierId,
            PriceListType = priceListType,
            CommissionAmountUSD = commissionUsd,
            CommissionAmountLocal = commissionLocal
        };

        foreach (var item in items)
        {
            item.SaleId = id;
            sale.Items.Add(item);
        }

        return sale;
    }

    private static SaleItem CreateItem(
        int id,
        int productId,
        decimal quantity,
        decimal subtotalUsd,
        decimal subtotalBsS,
        decimal? unitCostUsd,
        decimal? unitCostLocal) => new()
    {
        Id = id,
        ProductId = productId,
        ProductName = $"Product {productId}",
        Quantity = quantity,
        UnitPrice = quantity == 0m ? 0m : subtotalUsd / quantity,
        UnitPriceBsS = quantity == 0m ? 0m : subtotalBsS / quantity,
        Subtotal = subtotalUsd,
        SubtotalBsS = subtotalBsS,
        UnitCostUSD = unitCostUsd,
        UnitCostLocal = unitCostLocal
    };

    private static SalePayment CreatePayment(
        int id,
        int saleId,
        DateTime createdAt,
        decimal amountUsd,
        decimal amountBsS,
        decimal rate,
        decimal feeUsd,
        decimal feeLocal,
        bool feeConfigured,
        int paymentMethodId = 3) => new()
    {
        Id = id,
        SaleId = saleId,
        PaymentMethodId = paymentMethodId,
        Amount = amountUsd,
        AmountBsS = amountBsS,
        ExchangeRate = rate,
        CreatedAt = createdAt,
        FeeAmountUsd = feeUsd,
        FeeAmountLocal = feeLocal,
        FeeRate = feeConfigured ? rate : 0m,
        FeeCurrencyCode = feeConfigured ? "VES" : null,
        FeeConfigured = feeConfigured
    };

    private static Receivable CreateReceivable(int id, int saleId, DateTime issuedAtUtc) => new()
    {
        Id = id,
        SaleId = saleId,
        CustomerId = 1,
        OriginalUSD = 100m,
        BalanceUSD = 0m,
        OriginalBsS = 5000m,
        HistoricalRate = 50m,
        IssuedAtUtc = issuedAtUtc,
        DueDateUtc = issuedAtUtc.AddDays(30),
        Status = ReceivableStatus.Pagada
    };

    private static ReceivablePayment CreateReceivablePayment(
        int id,
        DateTime receivedAtUtc,
        decimal amountUsd,
        decimal amountBsS,
        decimal rate,
        decimal feeUsd = 0m,
        decimal feeLocal = 0m,
        bool feeConfigured = false,
        decimal fxDifferenceBsS = 0m,
        int? paymentMethodId = 3) => new()
    {
        Id = id,
        CustomerId = 1,
        PaymentMethodId = paymentMethodId,
        AmountUSD = amountUsd,
        AmountBsS = amountBsS,
        PaymentRate = rate,
        FxDifferenceBsS = fxDifferenceBsS,
        ReceivedAtUtc = receivedAtUtc,
        FeeAmountUsd = feeUsd,
        FeeAmountLocal = feeLocal,
        FeeRate = feeConfigured ? rate : 0m,
        FeeCurrencyCode = feeConfigured ? "VES" : null,
        FeeConfigured = feeConfigured
    };

    private static CreditPaymentAllocation CreateAllocation(
        int id,
        int receivablePaymentId,
        int receivableId,
        decimal amountUsd,
        decimal amountBsS) => new()
    {
        Id = id,
        ReceivablePaymentId = receivablePaymentId,
        ReceivableId = receivableId,
        AmountUSD = amountUsd,
        AmountBsS = amountBsS
    };

    private static CreditReturn CreateReturn(
        int id,
        int saleId,
        DateTime createdAt,
        decimal returnedUsd,
        decimal? refundRate,
        decimal refundedUsd = 0m,
        decimal refundedBsS = 0m,
        params CreditReturnItem[] items)
    {
        var creditReturn = new CreditReturn
        {
            Id = id,
            SaleId = saleId,
            ReceivableId = 1,
            CustomerId = 1,
            CreatedAtUtc = createdAt,
            ReturnedUSD = returnedUsd,
            RefundedUSD = refundedUsd,
            RefundedBsS = refundedBsS,
            RefundRate = refundRate
        };

        foreach (var item in items)
        {
            item.CreditReturnId = id;
            creditReturn.Items.Add(item);
        }

        return creditReturn;
    }

    private static CreditReturnItem CreateReturnItem(int id, int saleItemId, decimal quantity, decimal amountUsd) => new()
    {
        Id = id,
        SaleItemId = saleItemId,
        Quantity = quantity,
        AmountUSD = amountUsd
    };

    private static PettyCashExpense CreateExpense(
        int id,
        DateTime createdAtUtc,
        PettyCashStatus status,
        decimal amountUsd,
        decimal amountBsS,
        DateTime? cancelledAtUtc = null) => new()
    {
        Id = id,
        VoucherNumber = id,
        SessionId = 1,
        Category = PettyCashCategory.Varios,
        Currency = PettyCashCurrency.Usd,
        AmountUsd = amountUsd,
        AmountBsS = amountBsS,
        HistoricalRate = 50m,
        Concept = "Test expense",
        Beneficiary = "Test",
        ReferenceNumber = $"REF-{id}",
        CreatedByUserId = 1,
        CreatedAtUtc = createdAtUtc,
        Status = status,
        CancelledAtUtc = cancelledAtUtc
    };

    private sealed class StubTimeZoneProvider : ITimeZoneProvider
    {
        public Task<TimeZoneInfo> GetTimeZoneInfoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(FixedTimeZone);

        public Task<string> GetTimeZoneIdAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(TimeZoneId);

        public void Invalidate()
        {
        }
    }
}
