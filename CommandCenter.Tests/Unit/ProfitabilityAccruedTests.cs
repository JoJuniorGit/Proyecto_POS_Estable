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
/// Paso 14 (F2-WU2, F2-S2): motor devengado y cobertura de costos/comisiones. Ventana UTC
/// estricta [fromUtc, toUtc), moneda de reporte Dual USD / Single primaria, reversas de comision
/// por anulacion en el periodo y notas de lineas con costo/fee desconocidos.
/// </summary>
public class ProfitabilityAccruedTests
{
    private const string TimeZoneId = "Profitability/UTC-4";

    private static readonly TimeZoneInfo FixedTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        TimeZoneId,
        TimeSpan.FromHours(-4),
        "Profitability UTC-4",
        "Profitability UTC-4");

    private static readonly DateTime FromUtc = new(2026, 11, 2, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ToUtc = new(2026, 11, 3, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetSummaryAsync_DualUsd_FillsAccruedAndCoverage()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var knownItem = CreateItem(1, productId: 10, quantity: 2m, subtotalUsd: 100m, subtotalBsS: 5000m,
            unitCostUsd: 30m, unitCostLocal: 1500m);
        var unknownItem = CreateItem(2, productId: 20, quantity: 1m, subtotalUsd: 60m, subtotalBsS: 3000m,
            unitCostUsd: null, unitCostLocal: null);
        var cancelledPriorItem = CreateItem(3, productId: 30, quantity: 3m, subtotalUsd: 30m, subtotalBsS: 1500m,
            unitCostUsd: 5m, unitCostLocal: 250m);

        var knownSale = CreateSale(1, new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
            SaleStatus.Completed, SaleType.Merchandise, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: knownItem);
        var unknownSale = CreateSale(2, new DateTime(2026, 11, 2, 13, 0, 0, DateTimeKind.Utc),
            SaleStatus.Completed, SaleType.Merchandise, totalUsd: 60m, totalBsS: 3000m, rate: 50m,
            items: unknownItem);
        // A sale issued before the window and cancelled inside it: both lines of the report reverse
        // in the event period, including its item revenue/COGS.
        var cancelledPriorSale = CreateSale(3, new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc),
            SaleStatus.Cancelled, SaleType.Merchandise, totalUsd: 30m, totalBsS: 1500m, rate: 50m,
            cancelledAtUtc: new DateTime(2026, 11, 2, 15, 30, 0, DateTimeKind.Utc),
            items: cancelledPriorItem);
        var cashAdvance = CreateSale(4, new DateTime(2026, 11, 2, 16, 0, 0, DateTimeKind.Utc),
            SaleStatus.Completed, SaleType.CashAdvance, totalUsd: 0m, totalBsS: 0m, rate: 50m,
            commissionUsd: 1.4m, commissionLocal: 70m);

        context.Sales.AddRange(knownSale, unknownSale, cancelledPriorSale, cashAdvance);

        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 2, Name = "Efectivo", IsCash = true, IsActive = true, DisplayOrder = 1 },
            new PaymentMethod { Id = 3, Name = "Punto de Venta", IsCash = false, IsActive = true, DisplayOrder = 2 });

        context.SalePayments.AddRange(
            CreatePayment(1, saleId: 1, createdAt: new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
                amountUsd: 100m, amountBsS: 5000m, rate: 50m, feeUsd: 2m, feeLocal: 100m, feeConfigured: true),
            CreatePayment(2, saleId: 2, createdAt: new DateTime(2026, 11, 2, 13, 0, 0, DateTimeKind.Utc),
                amountUsd: 60m, amountBsS: 3000m, rate: 50m, feeUsd: 0m, feeLocal: 0m, feeConfigured: false),
            // Fee frozen when the (later cancelled) sale was collected in the previous period.
            CreatePayment(3, saleId: 3, createdAt: new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc),
                amountUsd: 30m, amountBsS: 1500m, rate: 50m, feeUsd: 1.5m, feeLocal: 75m, feeConfigured: true),
            // Cash has no acquirer fee by definition: it must not dilute FeeCoveragePercent.
            CreatePayment(4, saleId: 1, createdAt: new DateTime(2026, 11, 2, 12, 30, 0, DateTimeKind.Utc),
                amountUsd: 30m, amountBsS: 1500m, rate: 50m, feeUsd: 0m, feeLocal: 0m,
                feeConfigured: false, paymentMethodId: 2));

        context.CreditReturns.Add(CreateReturn(1, saleId: 1,
            createdAt: new DateTime(2026, 11, 2, 14, 0, 0, DateTimeKind.Utc), returnedUsd: 50m, refundRate: 50m,
            CreateReturnItem(1, saleItemId: 1, quantity: 1m, amountUsd: 50m)));

        context.PettyCashExpenses.Add(CreateExpense(1,
            createdAtUtc: new DateTime(2026, 11, 2, 17, 0, 0, DateTimeKind.Utc),
            status: PettyCashStatus.Active, amountUsd: 10m, amountBsS: 500m));

        context.ReceivablePayments.Add(CreateReceivablePayment(1,
            receivedAtUtc: new DateTime(2026, 11, 2, 19, 0, 0, DateTimeKind.Utc),
            amountUsd: 40m, amountBsS: 2000m, rate: 50m, feeUsd: 0.9m, feeLocal: 45m,
            feeConfigured: true, fxDifferenceBsS: 100m));

        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(CreateWindowFilter());
        var accrued = summary.Accrued;
        var coverage = summary.Coverage;

        Assert.Equal(160m, accrued.GrossSales);
        Assert.Equal(30m, accrued.Cancellations);
        Assert.Equal(50m, accrued.Returns);
        Assert.Equal(80m, accrued.NetSales);
        Assert.Equal(20m, accrued.KnownCostRevenue);
        Assert.Equal(60m, accrued.UnknownCostRevenue);
        Assert.Equal(15m, accrued.Cogs);
        Assert.Equal(5m, accrued.GrossProfit);
        Assert.Equal(25m, accrued.GrossMarginPercent);
        Assert.Equal(1.4m, accrued.ServiceCommissions);
        Assert.Equal(2m, accrued.FxDifferenceInformational);
        Assert.Equal(10m, accrued.PettyCashExpenses);
        Assert.Equal(1.4m, accrued.PaymentMethodFees);
        Assert.Equal(-5m, accrued.NetProfit);
        Assert.Equal(-25m, accrued.NetMarginPercent);

        Assert.Equal(25m, coverage.CostCoveragePercent);
        Assert.Equal(20m, coverage.KnownCostRevenue);
        Assert.Equal(15m, coverage.KnownCogs);
        Assert.Equal(60m, coverage.UnknownCostRevenue);
        Assert.Equal(1, coverage.UnknownCostLinesCount);
        Assert.Equal(70m, coverage.FeeCoveragePercent);
        Assert.Equal(140m, coverage.KnownFeePaymentAmount);
        Assert.Equal(60m, coverage.UnknownFeePaymentAmount);
        Assert.Equal(1, coverage.UnknownFeePaymentsCount);
    }

    [Fact]
    public async Task GetSummaryAsync_SinglePrimary_UsesLocalSnapshots()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var knownItem = CreateItem(1, productId: 10, quantity: 2m, subtotalUsd: 100m, subtotalBsS: 5000m,
            unitCostUsd: 30m, unitCostLocal: 1500m);
        var unknownItem = CreateItem(2, productId: 20, quantity: 1m, subtotalUsd: 60m, subtotalBsS: 3000m,
            unitCostUsd: null, unitCostLocal: null);
        var cancelledPriorItem = CreateItem(3, productId: 30, quantity: 3m, subtotalUsd: 30m, subtotalBsS: 1500m,
            unitCostUsd: 5m, unitCostLocal: 250m);

        var knownSale = CreateSale(1, new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
            SaleStatus.Completed, SaleType.Merchandise, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: knownItem);
        var unknownSale = CreateSale(2, new DateTime(2026, 11, 2, 13, 0, 0, DateTimeKind.Utc),
            SaleStatus.Completed, SaleType.Merchandise, totalUsd: 60m, totalBsS: 3000m, rate: 50m,
            items: unknownItem);
        var cancelledPriorSale = CreateSale(3, new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc),
            SaleStatus.Cancelled, SaleType.Merchandise, totalUsd: 30m, totalBsS: 1500m, rate: 50m,
            cancelledAtUtc: new DateTime(2026, 11, 2, 15, 30, 0, DateTimeKind.Utc),
            items: cancelledPriorItem);
        var cashAdvance = CreateSale(4, new DateTime(2026, 11, 2, 16, 0, 0, DateTimeKind.Utc),
            SaleStatus.Completed, SaleType.CashAdvance, totalUsd: 0m, totalBsS: 0m, rate: 50m,
            commissionUsd: 1.4m, commissionLocal: 70m);

        context.Sales.AddRange(knownSale, unknownSale, cancelledPriorSale, cashAdvance);

        context.SalePayments.AddRange(
            CreatePayment(1, saleId: 1, createdAt: new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
                amountUsd: 100m, amountBsS: 5000m, rate: 50m, feeUsd: 2m, feeLocal: 100m, feeConfigured: true),
            CreatePayment(2, saleId: 2, createdAt: new DateTime(2026, 11, 2, 13, 0, 0, DateTimeKind.Utc),
                amountUsd: 60m, amountBsS: 3000m, rate: 50m, feeUsd: 0m, feeLocal: 0m, feeConfigured: false),
            CreatePayment(3, saleId: 3, createdAt: new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc),
                amountUsd: 30m, amountBsS: 1500m, rate: 50m, feeUsd: 1.5m, feeLocal: 75m, feeConfigured: true));

        context.CreditReturns.Add(CreateReturn(1, saleId: 1,
            createdAt: new DateTime(2026, 11, 2, 14, 0, 0, DateTimeKind.Utc), returnedUsd: 50m, refundRate: 50m,
            CreateReturnItem(1, saleItemId: 1, quantity: 1m, amountUsd: 50m)));

        context.PettyCashExpenses.Add(CreateExpense(1,
            createdAtUtc: new DateTime(2026, 11, 2, 17, 0, 0, DateTimeKind.Utc),
            status: PettyCashStatus.Active, amountUsd: 10m, amountBsS: 500m));

        context.ReceivablePayments.Add(CreateReceivablePayment(1,
            receivedAtUtc: new DateTime(2026, 11, 2, 19, 0, 0, DateTimeKind.Utc),
            amountUsd: 40m, amountBsS: 2000m, rate: 50m, feeUsd: 0.9m, feeLocal: 45m,
            feeConfigured: true, fxDifferenceBsS: 100m));

        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Single).GetSummaryAsync(CreateWindowFilter());
        var accrued = summary.Accrued;
        var coverage = summary.Coverage;

        Assert.Equal("COP", summary.ReportCurrencyCode);
        Assert.True(summary.IsSingleCurrency);

        Assert.Equal(8000m, accrued.GrossSales);
        Assert.Equal(1500m, accrued.Cancellations);
        Assert.Equal(2500m, accrued.Returns);
        Assert.Equal(4000m, accrued.NetSales);
        Assert.Equal(1000m, accrued.KnownCostRevenue);
        Assert.Equal(3000m, accrued.UnknownCostRevenue);
        Assert.Equal(750m, accrued.Cogs);
        Assert.Equal(250m, accrued.GrossProfit);
        Assert.Equal(25m, accrued.GrossMarginPercent);
        Assert.Equal(70m, accrued.ServiceCommissions);
        Assert.Equal(100m, accrued.FxDifferenceInformational);
        Assert.Equal(500m, accrued.PettyCashExpenses);
        Assert.Equal(70m, accrued.PaymentMethodFees);
        Assert.Equal(-250m, accrued.NetProfit);
        Assert.Equal(-25m, accrued.NetMarginPercent);

        Assert.Equal(25m, coverage.CostCoveragePercent);
        Assert.Equal(1000m, coverage.KnownCostRevenue);
        Assert.Equal(750m, coverage.KnownCogs);
        Assert.Equal(3000m, coverage.UnknownCostRevenue);
        Assert.Equal(1, coverage.UnknownCostLinesCount);
        Assert.Equal(70m, coverage.FeeCoveragePercent);
        Assert.Equal(7000m, coverage.KnownFeePaymentAmount);
        Assert.Equal(3000m, coverage.UnknownFeePaymentAmount);
        Assert.Equal(1, coverage.UnknownFeePaymentsCount);
    }

    [Fact]
    public async Task GetSummaryAsync_StrictUtcWindow_IncludesFromAndExcludesTo()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var atFromItem = CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
            unitCostUsd: 30m, unitCostLocal: 1500m);
        var atToItem = CreateItem(2, productId: 20, quantity: 1m, subtotalUsd: 200m, subtotalBsS: 10000m,
            unitCostUsd: 50m, unitCostLocal: 2500m);
        var beforeFromItem = CreateItem(3, productId: 30, quantity: 1m, subtotalUsd: 300m, subtotalBsS: 15000m,
            unitCostUsd: 60m, unitCostLocal: 3000m);

        var atFromSale = CreateSale(1, FromUtc, SaleStatus.Completed, SaleType.Merchandise,
            totalUsd: 100m, totalBsS: 5000m, rate: 50m, items: atFromItem);
        var atToSale = CreateSale(2, ToUtc, SaleStatus.Completed, SaleType.Merchandise,
            totalUsd: 200m, totalBsS: 10000m, rate: 50m, items: atToItem);
        var beforeFromSale = CreateSale(3, FromUtc.AddTicks(-1), SaleStatus.Completed, SaleType.Merchandise,
            totalUsd: 300m, totalBsS: 15000m, rate: 50m, items: beforeFromItem);
        var atFromAdvance = CreateSale(4, FromUtc, SaleStatus.Completed, SaleType.CashAdvance,
            totalUsd: 0m, totalBsS: 0m, rate: 50m, commissionUsd: 1.4m, commissionLocal: 70m);
        var atToAdvance = CreateSale(5, ToUtc, SaleStatus.Completed, SaleType.CashAdvance,
            totalUsd: 0m, totalBsS: 0m, rate: 50m, commissionUsd: 2.8m, commissionLocal: 140m);

        context.Sales.AddRange(atFromSale, atToSale, beforeFromSale, atFromAdvance, atToAdvance);

        context.SalePayments.AddRange(
            CreatePayment(1, saleId: 1, createdAt: FromUtc, amountUsd: 100m, amountBsS: 5000m,
                rate: 50m, feeUsd: 1m, feeLocal: 50m, feeConfigured: true),
            CreatePayment(2, saleId: 2, createdAt: ToUtc, amountUsd: 200m, amountBsS: 10000m,
                rate: 50m, feeUsd: 2m, feeLocal: 100m, feeConfigured: true));

        // The return is created exactly at toUtc, so it must stay outside the period.
        context.CreditReturns.Add(CreateReturn(1, saleId: 1, createdAt: ToUtc, returnedUsd: 50m, refundRate: 50m,
            CreateReturnItem(1, saleItemId: 1, quantity: 1m, amountUsd: 50m)));

        context.PettyCashExpenses.AddRange(
            CreateExpense(1, createdAtUtc: FromUtc, status: PettyCashStatus.Active, amountUsd: 5m, amountBsS: 250m),
            CreateExpense(2, createdAtUtc: ToUtc, status: PettyCashStatus.Active, amountUsd: 10m, amountBsS: 500m));

        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(CreateWindowFilter());
        var accrued = summary.Accrued;
        var coverage = summary.Coverage;

        Assert.Equal(FromUtc, summary.FromUtc);
        Assert.Equal(ToUtc, summary.ToUtc);
        Assert.Equal(100m, accrued.GrossSales);
        Assert.Equal(0m, accrued.Cancellations);
        Assert.Equal(0m, accrued.Returns);
        Assert.Equal(100m, accrued.NetSales);
        Assert.Equal(100m, accrued.KnownCostRevenue);
        Assert.Equal(0m, accrued.UnknownCostRevenue);
        Assert.Equal(30m, accrued.Cogs);
        Assert.Equal(70m, accrued.GrossProfit);
        Assert.Equal(70m, accrued.GrossMarginPercent);
        Assert.Equal(1.4m, accrued.ServiceCommissions);
        Assert.Equal(5m, accrued.PettyCashExpenses);
        Assert.Equal(1m, accrued.PaymentMethodFees);
        Assert.Equal(65.4m, accrued.NetProfit);
        Assert.Equal(65.4m, accrued.NetMarginPercent);

        Assert.Equal(100m, coverage.CostCoveragePercent);
        Assert.Equal(0, coverage.UnknownCostLinesCount);
        Assert.Equal(100m, coverage.FeeCoveragePercent);
        Assert.Equal(100m, coverage.KnownFeePaymentAmount);
        Assert.Equal(0m, coverage.UnknownFeePaymentAmount);
        Assert.Equal(0, coverage.UnknownFeePaymentsCount);
    }

    [Fact]
    public async Task GetSummaryAsync_SameWindowCancellation_NetsGrossSalesToZero()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var item = CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
            unitCostUsd: 30m, unitCostLocal: 1500m);
        var sale = CreateSale(1, new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
            SaleStatus.Cancelled, SaleType.Merchandise, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            cancelledAtUtc: new DateTime(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc),
            items: item);

        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(CreateWindowFilter());
        var accrued = summary.Accrued;
        var coverage = summary.Coverage;

        // The sale was completed (invoice assigned) and cancelled inside the same window: it must
        // be present in both gross sales and cancellations so the period nets to zero.
        Assert.Equal(100m, accrued.GrossSales);
        Assert.Equal(100m, accrued.Cancellations);
        Assert.Equal(0m, accrued.Returns);
        Assert.Equal(0m, accrued.NetSales);
        Assert.Equal(0m, accrued.KnownCostRevenue);
        Assert.Equal(0m, accrued.UnknownCostRevenue);
        Assert.Equal(0m, accrued.Cogs);
        Assert.Equal(0m, accrued.GrossProfit);
        Assert.Equal(0m, accrued.GrossMarginPercent);
        Assert.Equal(0m, coverage.CostCoveragePercent);
        Assert.Equal(0, coverage.UnknownCostLinesCount);
    }

    [Fact]
    public async Task GetSummaryAsync_FullCreditReturnWithCancellation_IsRepresentedOnlyByReturns()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var item = CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
            unitCostUsd: 30m, unitCostLocal: 1500m);
        // A full return of a credit sale with no money received also sets CancelledAtUtc.
        var sale = CreateSale(1, new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
            SaleStatus.Cancelled, SaleType.Merchandise, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            cancelledAtUtc: new DateTime(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc),
            items: item);

        context.Sales.Add(sale);
        context.CreditReturns.Add(CreateReturn(1, saleId: 1,
            createdAt: new DateTime(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc),
            returnedUsd: 100m, refundRate: 50m,
            CreateReturnItem(1, saleItemId: 1, quantity: 1m, amountUsd: 100m)));
        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(CreateWindowFilter());
        var accrued = summary.Accrued;

        Assert.Equal(100m, accrued.GrossSales);
        Assert.Equal(0m, accrued.Cancellations);
        Assert.Equal(100m, accrued.Returns);
        Assert.Equal(0m, accrued.NetSales);
        Assert.Equal(0m, accrued.KnownCostRevenue);
        Assert.Equal(0m, accrued.Cogs);
        Assert.Equal(0m, accrued.GrossProfit);
        Assert.Equal(0m, accrued.GrossMarginPercent);
    }

    [Fact]
    public async Task GetSummaryAsync_CancelledOnHoldWithoutInvoice_IsIgnoredByGrossAndCancellations()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var item = CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 50m, subtotalBsS: 2500m,
            unitCostUsd: 20m, unitCostLocal: 1000m);
        var onHoldSale = CreateSale(1, new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
            SaleStatus.Cancelled, SaleType.Merchandise, totalUsd: 50m, totalBsS: 2500m, rate: 50m,
            cancelledAtUtc: new DateTime(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc),
            hasInvoice: false, items: item);

        context.Sales.Add(onHoldSale);
        // An initial payment collected while the order was on hold: only the invoice-less
        // cancellation guard prevents its fee from being reversed.
        context.SalePayments.Add(CreatePayment(1, saleId: 1,
            createdAt: new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc),
            amountUsd: 50m, amountBsS: 2500m, rate: 50m, feeUsd: 1.5m, feeLocal: 75m,
            feeConfigured: true));

        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(CreateWindowFilter());
        var accrued = summary.Accrued;

        Assert.Equal(0m, accrued.GrossSales);
        Assert.Equal(0m, accrued.Cancellations);
        Assert.Equal(0m, accrued.NetSales);
        Assert.Equal(0m, accrued.KnownCostRevenue);
        Assert.Equal(0m, accrued.Cogs);
        Assert.Equal(0m, accrued.PaymentMethodFees);
    }

    [Fact]
    public async Task GetSummaryAsync_PartialReturnOnUnknownCostLine_KeepsGrossLineInCoverageCount()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var unknownItem = CreateItem(1, productId: 10, quantity: 2m, subtotalUsd: 100m, subtotalBsS: 5000m,
            unitCostUsd: null, unitCostLocal: null);
        var sale = CreateSale(1, new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
            SaleStatus.Completed, SaleType.Merchandise, totalUsd: 100m, totalBsS: 5000m, rate: 50m,
            items: unknownItem);

        context.Sales.Add(sale);
        context.CreditReturns.Add(CreateReturn(1, saleId: 1,
            createdAt: new DateTime(2026, 11, 2, 14, 0, 0, DateTimeKind.Utc), returnedUsd: 50m, refundRate: 50m,
            CreateReturnItem(1, saleItemId: 1, quantity: 1m, amountUsd: 50m)));
        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(CreateWindowFilter());
        var accrued = summary.Accrued;
        var coverage = summary.Coverage;

        Assert.Equal(100m, accrued.GrossSales);
        Assert.Equal(50m, accrued.Returns);
        Assert.Equal(50m, accrued.NetSales);
        Assert.Equal(0m, accrued.KnownCostRevenue);
        // Revenue stays proportional to the returned quantity...
        Assert.Equal(50m, accrued.UnknownCostRevenue);
        Assert.Equal(0m, accrued.Cogs);
        // ...but coverage counts the unknown-cost line present in gross qualifying sales.
        Assert.Equal(1, coverage.UnknownCostLinesCount);
        Assert.Equal(0m, coverage.CostCoveragePercent);
    }

    [Fact]
    public async Task GetSummaryAsync_SameWindowCancelledCashAdvance_NetsServiceCommissionsToZero()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var advance = CreateSale(1, new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
            SaleStatus.Cancelled, SaleType.CashAdvance, totalUsd: 0m, totalBsS: 0m, rate: 50m,
            cancelledAtUtc: new DateTime(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc),
            commissionUsd: 1.4m, commissionLocal: 70m);

        context.Sales.Add(advance);
        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(CreateWindowFilter());

        // The advance is earned in the window and cancelled in the same window: the positive
        // commission and its reversal must net to zero.
        Assert.Equal(0m, summary.Accrued.ServiceCommissions);
        Assert.Equal(0m, summary.Accrued.GrossSales);
        Assert.Equal(0m, summary.Accrued.NetProfit);
    }

    [Fact]
    public async Task GetSummaryAsync_PettyCash_ReversesOnlyPriorPeriodCancellations()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        context.PettyCashExpenses.AddRange(
            CreateExpense(1, createdAtUtc: new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc),
                status: PettyCashStatus.Active, amountUsd: 100m, amountBsS: 5000m),
            // Recognized in a previous period: the cancellation is a reversal of the current one.
            CreateExpense(2, createdAtUtc: new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc),
                status: PettyCashStatus.Cancelled, amountUsd: 40m, amountBsS: 2000m,
                cancelledAtUtc: new DateTime(2026, 11, 2, 11, 0, 0, DateTimeKind.Utc)),
            // Created and cancelled inside the window: it was never counted as active, so the
            // cancellation must not produce a second negative adjustment.
            CreateExpense(3, createdAtUtc: new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
                status: PettyCashStatus.Cancelled, amountUsd: 25m, amountBsS: 1250m,
                cancelledAtUtc: new DateTime(2026, 11, 2, 13, 0, 0, DateTimeKind.Utc)));

        await context.SaveChangesAsync();

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(CreateWindowFilter());

        Assert.Equal(60m, summary.Accrued.PettyCashExpenses);
        Assert.Equal(-60m, summary.Accrued.NetProfit);
    }

    [Fact]
    public async Task GetSummaryAsync_ProductFilter_CountsOnlySelectedItemsAndKeepsGlobalLinesWhole()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var selectedItem = CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m, subtotalBsS: 5000m,
            unitCostUsd: 30m, unitCostLocal: 1500m);
        var otherItem = CreateItem(2, productId: 20, quantity: 1m, subtotalUsd: 60m, subtotalBsS: 3000m,
            unitCostUsd: null, unitCostLocal: null);

        var mixedSale = CreateSale(1, new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
            SaleStatus.Completed, SaleType.Merchandise, totalUsd: 160m, totalBsS: 8000m, rate: 50m,
            items: new[] { selectedItem, otherItem });

        var cashAdvance = CreateSale(2, new DateTime(2026, 11, 2, 13, 0, 0, DateTimeKind.Utc),
            SaleStatus.Completed, SaleType.CashAdvance, totalUsd: 0m, totalBsS: 0m, rate: 50m,
            commissionUsd: 1.4m, commissionLocal: 70m);

        context.Sales.AddRange(mixedSale, cashAdvance);
        context.SalePayments.Add(CreatePayment(1, saleId: 1,
            createdAt: new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
            amountUsd: 160m, amountBsS: 8000m, rate: 50m, feeUsd: 2m, feeLocal: 100m, feeConfigured: true));
        context.PettyCashExpenses.Add(CreateExpense(1,
            createdAtUtc: new DateTime(2026, 11, 2, 14, 0, 0, DateTimeKind.Utc),
            status: PettyCashStatus.Active, amountUsd: 10m, amountBsS: 500m));
        await context.SaveChangesAsync();

        var filter = CreateWindowFilter();
        filter.ProductIds = new[] { 10 };

        var summary = await CreateService(context, MonetaryMode.Dual).GetSummaryAsync(filter);
        var accrued = summary.Accrued;
        var coverage = summary.Coverage;

        Assert.Equal(100m, accrued.GrossSales);
        Assert.Equal(0m, accrued.Cancellations);
        Assert.Equal(0m, accrued.Returns);
        Assert.Equal(100m, accrued.NetSales);
        Assert.Equal(100m, accrued.KnownCostRevenue);
        Assert.Equal(0m, accrued.UnknownCostRevenue);
        Assert.Equal(30m, accrued.Cogs);
        Assert.Equal(70m, accrued.GrossProfit);
        Assert.Equal(70m, accrued.GrossMarginPercent);

        // Global operation lines are not prorated by product.
        Assert.Equal(1.4m, accrued.ServiceCommissions);
        Assert.Equal(10m, accrued.PettyCashExpenses);
        Assert.Equal(2m, accrued.PaymentMethodFees);
        Assert.Equal(59.4m, accrued.NetProfit);
        Assert.Equal(59.4m, accrued.NetMarginPercent);

        Assert.Equal(100m, coverage.CostCoveragePercent);
        Assert.Equal(30m, coverage.KnownCogs);
        Assert.Equal(0m, coverage.UnknownCostRevenue);
        Assert.Equal(0, coverage.UnknownCostLinesCount);
        Assert.Equal(100m, coverage.FeeCoveragePercent);
        Assert.Equal(160m, coverage.KnownFeePaymentAmount);
        Assert.Equal(0m, coverage.UnknownFeePaymentAmount);
    }

    [Fact]
    public async Task GetSummaryAsync_AppliesCashierPriceListAndSaleTypeFilters()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var retailCashierOneItem = CreateItem(1, productId: 10, quantity: 1m, subtotalUsd: 100m,
            subtotalBsS: 5000m, unitCostUsd: 30m, unitCostLocal: 1500m);
        var retailCashierTwoItem = CreateItem(2, productId: 20, quantity: 1m, subtotalUsd: 200m,
            subtotalBsS: 10000m, unitCostUsd: 50m, unitCostLocal: 2500m);
        var wholesaleCashierOneItem = CreateItem(3, productId: 30, quantity: 1m, subtotalUsd: 300m,
            subtotalBsS: 15000m, unitCostUsd: 60m, unitCostLocal: 3000m);

        context.Sales.AddRange(
            CreateSale(1, new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc), SaleStatus.Completed,
                SaleType.Merchandise, totalUsd: 100m, totalBsS: 5000m, rate: 50m, cashierId: 1,
                priceListType: "Retail", items: retailCashierOneItem),
            CreateSale(2, new DateTime(2026, 11, 2, 11, 0, 0, DateTimeKind.Utc), SaleStatus.Completed,
                SaleType.Merchandise, totalUsd: 200m, totalBsS: 10000m, rate: 50m, cashierId: 2,
                priceListType: "Retail", items: retailCashierTwoItem),
            CreateSale(3, new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc), SaleStatus.Completed,
                SaleType.Merchandise, totalUsd: 300m, totalBsS: 15000m, rate: 50m, cashierId: 1,
                priceListType: "Wholesale", items: wholesaleCashierOneItem),
            CreateSale(4, new DateTime(2026, 11, 2, 13, 0, 0, DateTimeKind.Utc), SaleStatus.Completed,
                SaleType.CashAdvance, totalUsd: 0m, totalBsS: 0m, rate: 50m, cashierId: 1,
                priceListType: "Retail", commissionUsd: 1.4m, commissionLocal: 70m),
            CreateSale(5, new DateTime(2026, 11, 2, 14, 0, 0, DateTimeKind.Utc), SaleStatus.Completed,
                SaleType.CashAdvance, totalUsd: 0m, totalBsS: 0m, rate: 50m, cashierId: 2,
                priceListType: "Retail", commissionUsd: 2.8m, commissionLocal: 140m));

        await context.SaveChangesAsync();

        var service = CreateService(context, MonetaryMode.Dual);

        var merchandiseFilter = CreateWindowFilter();
        merchandiseFilter.CashierId = 1;
        merchandiseFilter.PriceListType = "Retail";
        merchandiseFilter.SaleType = SaleType.Merchandise;
        var merchandiseSummary = await service.GetSummaryAsync(merchandiseFilter);

        Assert.Equal(100m, merchandiseSummary.Accrued.GrossSales);
        Assert.Equal(0m, merchandiseSummary.Accrued.ServiceCommissions);

        var advanceFilter = CreateWindowFilter();
        advanceFilter.CashierId = 1;
        advanceFilter.PriceListType = "Retail";
        advanceFilter.SaleType = SaleType.CashAdvance;
        var advanceSummary = await service.GetSummaryAsync(advanceFilter);

        Assert.Equal(0m, advanceSummary.Accrued.GrossSales);
        Assert.Equal(1.4m, advanceSummary.Accrued.ServiceCommissions);
    }

    [Fact]
    public async Task GetSummaryAsync_CategoryFilter_ThrowsUntilCatalogLands()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var filter = CreateWindowFilter();
        filter.CategoryIds = new[] { 7 };

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => CreateService(context, MonetaryMode.Dual).GetSummaryAsync(filter));

        Assert.Equal("El filtro por categoría se habilita en F2-WU4.", exception.Message);
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
        SaleStatus status,
        SaleType saleType,
        decimal totalUsd,
        decimal totalBsS,
        decimal rate,
        DateTime? cancelledAtUtc = null,
        int? cashierId = null,
        string priceListType = "Retail",
        decimal commissionUsd = 0m,
        decimal commissionLocal = 0m,
        bool hasInvoice = true,
        params SaleItem[] items)
    {
        var sale = new Sale
        {
            Id = id,
            Date = date,
            Status = status,
            SaleType = saleType,
            // F2-S2: the invoice discriminator replaces the mutable Status check. A completed
            // (even if later cancelled) sale has an invoice; Pending/OnHold and cancelled OnHold
            // orders do not.
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

    private static CreditReturn CreateReturn(
        int id,
        int saleId,
        DateTime createdAt,
        decimal returnedUsd,
        decimal? refundRate,
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

    private static ReceivablePayment CreateReceivablePayment(
        int id,
        DateTime receivedAtUtc,
        decimal amountUsd,
        decimal amountBsS,
        decimal rate,
        decimal feeUsd,
        decimal feeLocal,
        bool feeConfigured,
        decimal fxDifferenceBsS) => new()
    {
        Id = id,
        CustomerId = 1,
        PaymentMethodId = 3,
        AmountUSD = amountUsd,
        AmountBsS = amountBsS,
        PaymentRate = rate,
        FeeAmountUsd = feeUsd,
        FeeAmountLocal = feeLocal,
        FeeRate = feeConfigured ? rate : 0m,
        FeeCurrencyCode = feeConfigured ? "VES" : null,
        FeeConfigured = feeConfigured,
        FxDifferenceBsS = fxDifferenceBsS,
        ReceivedAtUtc = receivedAtUtc
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
