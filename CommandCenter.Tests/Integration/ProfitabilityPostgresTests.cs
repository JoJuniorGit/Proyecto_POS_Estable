using System;
using System.Collections.Generic;
using System.Linq;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;
using Sales.Module.Data;
using Sales.Module.DTOs.Profitability;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;
using Xunit.Abstractions;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// Paso 14 (F2-WU5, F2-S5): evidencia de integración del Estado de Resultados sobre PostgreSQL
/// real. Un test siembra la regla de comisión, cobros de contado/CxC, un avance de efectivo y una
/// anulación reconocida en un período anterior, y contrasta todos los bloques del resumen contra
/// valores calculados a mano (precisión 18,4/18,2, FKs e historial de migraciones incluidos). El
/// otro test ejecuta EXPLAIN (ANALYZE, BUFFERS) con enable_seqscan deshabilitado y certifica que
/// las consultas clave usan índices. Cada test corre en una base aislada del carril
/// <see cref="PostgresTestLane"/>; sin conexión utilizable el lane falla EXPLÍCITO.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class ProfitabilityPostgresTests
{
    private const string TimeZoneId = "Profitability/UTC-4";
    private const int CashierId = 101;
    private const int CustomerId = 1;
    private const int CardPaymentMethodId = 11;
    private const int CashPaymentMethodId = 12;
    private const int FeeRuleId = 100;
    private const int SaleAId = 1001;
    private const int SaleBId = 1002;
    private const int SaleCId = 1003;
    private const int SaleDId = 1004;
    private const int SaleEId = 1005;
    private const int SaleItemAId = 2001;
    private const int SaleItemBId = 2002;
    private const int SaleItemDId = 2004;
    private const int SaleItemEId = 2005;
    private const int SalePaymentAId = 3001;
    private const int SalePaymentBId = 3002;
    private const int SalePaymentCId = 3003;
    private const int SalePaymentDId = 3004;
    private const int ReceivableAId = 4001;
    private const int ReceivableEId = 4002;
    private const int ReceivablePaymentEId = 5001;
    private const int AllocationEId = 6001;
    private const int CreditReturnAId = 7001;
    private const int CreditReturnItemAId = 8001;
    private const int PettyCashSessionId = 9501;
    private const int PettyCashExpenseId = 9001;

    private static readonly TimeZoneInfo FixedTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        TimeZoneId,
        TimeSpan.FromHours(-4),
        "Profitability UTC-4",
        "Profitability UTC-4");

    // Local day 2026-11-02 in the fixed UTC-4 zone: strict [fromUtc, toUtc) window.
    private static readonly DateTime FromUtc = new(2026, 11, 2, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ToUtc = new(2026, 11, 3, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime InWindow = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    private readonly ITestOutputHelper _output;

    public ProfitabilityPostgresTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task GetSummaryAsync_RealPostgres_ReturnsHandCalculatedProfitabilityAndFeeSnapshots()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(applyMigrations: true);
        await using (var seedContext = database.CreateSalesDbContext())
        {
            await SeedScenarioAsync(seedContext);
        }

        await using var serviceContext = database.CreateSalesDbContext();
        var summary = await CreateService(serviceContext).GetSummaryAsync(CreateWindowFilter());

        Assert.Equal(FromUtc, summary.FromUtc);
        Assert.Equal(ToUtc, summary.ToUtc);
        Assert.Equal(TimeZoneId, summary.TimeZoneId);
        Assert.Equal(MonetaryMode.Dual, summary.MonetaryMode);
        Assert.Equal("USD", summary.ReportCurrencyCode);
        Assert.False(summary.IsSingleCurrency);

        AssertAccrued(summary.Accrued);
        AssertCoverage(summary.Coverage);
        AssertRealized(summary.Realized);
        AssertBreakdown(summary.Breakdown);
        Assert.Empty(summary.Alerts);

        await using var verifyContext = database.CreateSalesDbContext();
        await AssertFeeSnapshotsAsync(verifyContext);
        await AssertFeeReferencesAndMigrationHistoryAsync(verifyContext);
    }

    [Fact]
    public async Task ExplainAnalyze_KeyProfitabilityQueries_UseIndexScans()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(applyMigrations: true);
        await using (var seedContext = database.CreateSalesDbContext())
        {
            await SeedScenarioAsync(seedContext);
            await seedContext.Database.ExecuteSqlRawAsync(
                "ANALYZE \"Sales\", \"SalePayments\", \"ReceivablePayments\", \"CreditReturns\", \"PettyCashExpenses\"");
        }

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();

        // SET LOCAL only lives inside the transaction; the plans are read with the sequential
        // scan explicitly discouraged so the test asserts the index exists and is usable.
        await using (var setSeqScan = new NpgsqlCommand("SET LOCAL enable_seqscan = off", connection, transaction))
        {
            await setSeqScan.ExecuteNonQueryAsync();
        }

        var from = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

        var salesPlan = await ExplainAsync(
            connection,
            transaction,
            "SELECT \"Id\", \"TotalUSD\" FROM \"Sales\" " +
            "WHERE \"SaleType\" = @saleType AND \"Status\" = @status " +
            "AND \"Date\" >= @from AND \"Date\" < @to",
            ("saleType", 0),
            ("status", 1),
            ("from", from),
            ("to", to));
        AssertUsesIndexScan("Sales by SaleType/Status/Date", salesPlan);

        var salePaymentsPlan = await ExplainAsync(
            connection,
            transaction,
            "SELECT \"Id\", \"Amount\" FROM \"SalePayments\" " +
            "WHERE \"CreatedAt\" >= @from AND \"CreatedAt\" < @to",
            ("from", from),
            ("to", to));
        AssertUsesIndexScan("SalePayments by CreatedAt", salePaymentsPlan);

        var receivablePaymentsPlan = await ExplainAsync(
            connection,
            transaction,
            "SELECT \"Id\", \"AmountUSD\" FROM \"ReceivablePayments\" " +
            "WHERE \"ReceivedAtUtc\" >= @from AND \"ReceivedAtUtc\" < @to",
            ("from", from),
            ("to", to));
        AssertUsesIndexScan("ReceivablePayments by ReceivedAtUtc", receivablePaymentsPlan);

        var creditReturnsPlan = await ExplainAsync(
            connection,
            transaction,
            "SELECT \"Id\", \"ReturnedUSD\" FROM \"CreditReturns\" " +
            "WHERE \"CreatedAtUtc\" >= @from AND \"CreatedAtUtc\" < @to",
            ("from", from),
            ("to", to));
        AssertUsesIndexScan("CreditReturns by CreatedAtUtc", creditReturnsPlan);

        var pettyCashPlan = await ExplainAsync(
            connection,
            transaction,
            "SELECT \"Id\", \"AmountUsd\" FROM \"PettyCashExpenses\" " +
            "WHERE \"CreatedAtUtc\" >= @from AND \"CreatedAtUtc\" < @to",
            ("from", from),
            ("to", to));
        AssertUsesIndexScan("PettyCashExpenses by CreatedAtUtc", pettyCashPlan);

        await transaction.RollbackAsync();
    }

    private static void AssertAccrued(ProfitabilityAccruedDto accrued)
    {
        // A+B+E gross (100+50+100); D was issued before the period and is recognized in
        // Cancellations, while the partial return of A reduces both return lines.
        Assert.Equal(250m, accrued.GrossSales);
        Assert.Equal(50m, accrued.Cancellations);
        Assert.Equal(10m, accrued.Returns);
        Assert.Equal(190m, accrued.NetSales);
        Assert.Equal(140m, accrued.KnownCostRevenue);
        Assert.Equal(50m, accrued.UnknownCostRevenue);
        Assert.Equal(94m, accrued.Cogs);
        Assert.Equal(46m, accrued.GrossProfit);
        Assert.Equal(46m / 140m * 100m, accrued.GrossMarginPercent);
        Assert.Equal(10m, accrued.ServiceCommissions);
        Assert.Equal(8m, accrued.FxDifferenceInformational);
        Assert.Equal(5m, accrued.PettyCashExpenses);
        // A 1.0000 + C 5.1000 + E 0.4000 = 6.5000 in-period fees, minus D's 2.0000 reversal.
        Assert.Equal(4.5m, accrued.PaymentMethodFees);
        Assert.Equal(46.5m, accrued.NetProfit);
        Assert.Equal(46.5m / 140m * 100m, accrued.NetMarginPercent);
    }

    private static void AssertCoverage(ProfitabilityCoverageDto coverage)
    {
        Assert.Equal(140m / 190m * 100m, coverage.CostCoveragePercent);
        Assert.Equal(140m, coverage.KnownCostRevenue);
        Assert.Equal(94m, coverage.KnownCogs);
        Assert.Equal(50m, coverage.UnknownCostRevenue);
        Assert.Equal(1, coverage.UnknownCostLinesCount);
        // Cash payments have no acquirer fee by definition and do not dilute the coverage base.
        Assert.Equal(100m, coverage.FeeCoveragePercent);
        Assert.Equal(650m, coverage.KnownFeePaymentAmount);
        Assert.Equal(0m, coverage.UnknownFeePaymentAmount);
        Assert.Equal(0, coverage.UnknownFeePaymentsCount);
    }

    private static void AssertRealized(ProfitabilityRealizedDto realized)
    {
        // A 100 + B 50 + E's CxC allocation 40 = 190 collected; A's 10 refund reduces the cash.
        Assert.Equal(180m, realized.CollectedRevenue);
        Assert.Equal(130m, realized.KnownCostCollectedRevenue);
        Assert.Equal(50m, realized.UnknownCostCollectedRevenue);
        Assert.Equal(84m, realized.RealizedCogs);
        Assert.Equal(46m, realized.RealizedGrossProfit);
        Assert.Equal(46m / 130m * 100m, realized.RealizedGrossMarginPercent);
        Assert.Equal(10m, realized.ServiceCommissions);
        Assert.Equal(8m, realized.FxDifference);
        Assert.Equal(5m, realized.PettyCashExpenses);
        Assert.Equal(4.5m, realized.PaymentMethodFees);
        Assert.Equal(54.5m, realized.NetProfit);
        Assert.Equal(54.5m / 130m * 100m, realized.NetMarginPercent);
    }

    private static void AssertBreakdown(ProfitabilityBreakdownDto breakdown)
    {
        // Payment methods: card 650 collected / 4.50 net fee (including the S10 reversal),
        // cash 50 with no acquirer fee.
        Assert.Equal(2, breakdown.PaymentMethods.Count);
        var card = breakdown.PaymentMethods[0];
        Assert.Equal(CardPaymentMethodId, card.PaymentMethodId);
        Assert.Equal("Tarjeta VES", card.PaymentMethodName);
        Assert.Equal(650m, card.CollectedAmount);
        Assert.Equal(4.5m, card.FeeAmount);
        Assert.Equal(650m, card.KnownFeeAmount);
        Assert.Equal(0m, card.UnknownFeeAmount);
        Assert.Equal(100m, card.FeeCoveragePercent);

        var cash = breakdown.PaymentMethods[1];
        Assert.Equal(CashPaymentMethodId, cash.PaymentMethodId);
        Assert.Equal("Efectivo", cash.PaymentMethodName);
        Assert.Equal(50m, cash.CollectedAmount);
        Assert.Equal(0m, cash.FeeAmount);
        Assert.Equal(0m, cash.KnownFeeAmount);
        Assert.Equal(0m, cash.UnknownFeeAmount);
        Assert.Equal(100m, cash.FeeCoveragePercent);

        // Product ranking by gross profit: E (40), A (36 after the 0.1 return), B (unknown cost,
        // 0) and D (pre-period sale cancelled in the window, negative contribution).
        Assert.Equal(new[] { 30, 10, 20, 40 }, breakdown.Products.Select(product => product.ProductId));
        var productE = breakdown.Products[0];
        Assert.Equal("Producto E", productE.ProductName);
        Assert.Equal(1, productE.CategoryId);
        Assert.Equal("Categoría A", productE.CategoryName);
        Assert.Equal(1m, productE.QuantitySold);
        Assert.Equal(100m, productE.Revenue);
        Assert.Equal(60m, productE.Cogs);
        Assert.Equal(40m, productE.GrossProfit);
        Assert.Equal(40m, productE.GrossMarginPercent);

        var productA = breakdown.Products[1];
        Assert.Equal(0.9m, productA.QuantitySold);
        Assert.Equal(90m, productA.Revenue);
        Assert.Equal(54m, productA.Cogs);
        Assert.Equal(36m, productA.GrossProfit);
        Assert.Equal(40m, productA.GrossMarginPercent);

        var productB = breakdown.Products[2];
        Assert.Equal(1m, productB.QuantitySold);
        Assert.Equal(50m, productB.Revenue);
        Assert.Equal(0m, productB.Cogs);
        Assert.Equal(0m, productB.GrossProfit);
        Assert.Equal(2, productB.CategoryId);

        var productD = breakdown.Products[3];
        Assert.Equal(-1m, productD.QuantitySold);
        Assert.Equal(-50m, productD.Revenue);
        Assert.Equal(-20m, productD.Cogs);
        Assert.Equal(-30m, productD.GrossProfit);
        Assert.Equal(0m, productD.GrossMarginPercent);
        Assert.Equal(3, productD.CategoryId);

        Assert.Equal(3, breakdown.Categories.Count);
        var categoryA = breakdown.Categories[0];
        Assert.Equal(1, categoryA.CategoryId);
        Assert.Equal("Categoría A", categoryA.CategoryName);
        Assert.Equal(190m, categoryA.Revenue);
        Assert.Equal(114m, categoryA.Cogs);
        Assert.Equal(76m, categoryA.GrossProfit);
        Assert.Equal(40m, categoryA.GrossMarginPercent);
        Assert.Equal(76m / 46m * 100m, categoryA.ContributionPercent);

        var categoryB = breakdown.Categories[1];
        Assert.Equal(2, categoryB.CategoryId);
        Assert.Equal(50m, categoryB.Revenue);
        Assert.Equal(0m, categoryB.GrossProfit);
        Assert.Equal(0m, categoryB.ContributionPercent);

        var categoryC = breakdown.Categories[2];
        Assert.Equal(3, categoryC.CategoryId);
        Assert.Equal(-50m, categoryC.Revenue);
        Assert.Equal(-20m, categoryC.Cogs);
        Assert.Equal(-30m, categoryC.GrossProfit);
        Assert.Equal(-30m / 46m * 100m, categoryC.ContributionPercent);

        // A single seller owns every seeded sale (including the cancelled pre-period one).
        var seller = Assert.Single(breakdown.Sellers);
        Assert.Equal(CashierId, seller.CashierId);
        Assert.Equal("Ana", seller.CashierName);
        Assert.Equal(190m, seller.NetSales);
        Assert.Equal(46m, seller.GrossProfit);

        // One local day: the series point reconciles with both headline blocks.
        var point = Assert.Single(breakdown.Series);
        Assert.Equal(new DateTime(2026, 11, 2, 4, 0, 0, DateTimeKind.Utc), point.PeriodStartUtc);
        Assert.Equal(190m, point.AccruedNetSales);
        Assert.Equal(46.5m, point.AccruedNetProfit);
        Assert.Equal(180m, point.RealizedCollectedRevenue);
        Assert.Equal(54.5m, point.RealizedNetProfit);
    }

    private static async Task AssertFeeSnapshotsAsync(SalesDbContext context)
    {
        // The migration contract fixes FeeAmountUsd at numeric(18,4) and FeeAmountLocal at
        // numeric(18,2) in both payment tables.
        foreach (var tableName in new[] { "SalePayments", "ReceivablePayments" })
        {
            var feeUsdShape = await context.Database.SqlQueryRaw<string>(
                "SELECT numeric_precision::text || ':' || numeric_scale::text AS \"Value\" " +
                "FROM information_schema.columns WHERE table_schema = 'public' " +
                "AND table_name = {0} AND column_name = 'FeeAmountUsd'",
                tableName).SingleAsync();
            Assert.Equal("18:4", feeUsdShape);

            var feeLocalShape = await context.Database.SqlQueryRaw<string>(
                "SELECT numeric_precision::text || ':' || numeric_scale::text AS \"Value\" " +
                "FROM information_schema.columns WHERE table_schema = 'public' " +
                "AND table_name = {0} AND column_name = 'FeeAmountLocal'",
                tableName).SingleAsync();
            Assert.Equal("18:2", feeLocalShape);
        }

        // Round-trip through the persisted numeric scale: PostgreSQL prints the stored scale.
        var paymentAFees = await context.Database.SqlQueryRaw<string>(
            "SELECT \"FeeAmountUsd\"::text || '|' || \"FeeAmountLocal\"::text AS \"Value\" " +
            "FROM \"SalePayments\" WHERE \"Id\" = {0}",
            SalePaymentAId).SingleAsync();
        Assert.Equal("1.0000|50.00", paymentAFees);

        var paymentCFees = await context.Database.SqlQueryRaw<string>(
            "SELECT \"FeeAmountUsd\"::text || '|' || \"FeeAmountLocal\"::text AS \"Value\" " +
            "FROM \"SalePayments\" WHERE \"Id\" = {0}",
            SalePaymentCId).SingleAsync();
        Assert.Equal("5.1000|255.00", paymentCFees);

        var paymentDFees = await context.Database.SqlQueryRaw<string>(
            "SELECT \"FeeAmountUsd\"::text || '|' || \"FeeAmountLocal\"::text AS \"Value\" " +
            "FROM \"SalePayments\" WHERE \"Id\" = {0}",
            SalePaymentDId).SingleAsync();
        Assert.Equal("2.0000|100.00", paymentDFees);

        var receivableFees = await context.Database.SqlQueryRaw<string>(
            "SELECT \"FeeAmountUsd\"::text || '|' || \"FeeAmountLocal\"::text AS \"Value\" " +
            "FROM \"ReceivablePayments\" WHERE \"Id\" = {0}",
            ReceivablePaymentEId).SingleAsync();
        Assert.Equal("0.4000|20.00", receivableFees);

        var paymentA = await context.SalePayments.AsNoTracking().SingleAsync(payment => payment.Id == SalePaymentAId);
        Assert.Equal(1.0000m, paymentA.FeeAmountUsd);
        Assert.Equal(50.00m, paymentA.FeeAmountLocal);
        Assert.True(paymentA.FeeConfigured);

        var receivablePayment = await context.ReceivablePayments.AsNoTracking()
            .SingleAsync(payment => payment.Id == ReceivablePaymentEId);
        Assert.Equal(0.4000m, receivablePayment.FeeAmountUsd);
        Assert.Equal(20.00m, receivablePayment.FeeAmountLocal);
        Assert.Equal(400m, receivablePayment.FxDifferenceBsS);
    }

    private static async Task AssertFeeReferencesAndMigrationHistoryAsync(SalesDbContext context)
    {
        // Percentage keeps the configured decimal(9,4) precision.
        var percentageShape = await context.Database.SqlQueryRaw<string>(
            "SELECT numeric_precision::text || ':' || numeric_scale::text AS \"Value\" " +
            "FROM information_schema.columns WHERE table_schema = 'public' " +
            "AND table_name = 'PaymentMethodFeeRules' AND column_name = 'Percentage'")
            .SingleAsync();
        Assert.Equal("9:4", percentageShape);

        var rulePercentage = await context.PaymentMethodFeeRules.AsNoTracking()
            .Where(rule => rule.Id == FeeRuleId)
            .Select(rule => rule.Percentage)
            .SingleAsync();
        Assert.Equal(1.0000m, rulePercentage);

        // Every fee snapshot resolves to the configured rule through the real FK.
        var resolvedSaleFeeRules = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM \"SalePayments\" payment " +
            "INNER JOIN \"PaymentMethodFeeRules\" rule ON payment.\"PaymentMethodFeeRuleId\" = rule.\"Id\" " +
            "WHERE payment.\"Id\" IN ({0}, {1}, {2})",
            SalePaymentAId,
            SalePaymentCId,
            SalePaymentDId).SingleAsync();
        Assert.Equal(3, resolvedSaleFeeRules);

        var resolvedReceivableFeeRules = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM \"ReceivablePayments\" payment " +
            "INNER JOIN \"PaymentMethodFeeRules\" rule ON payment.\"PaymentMethodFeeRuleId\" = rule.\"Id\" " +
            "WHERE payment.\"Id\" = {0}",
            ReceivablePaymentEId).SingleAsync();
        Assert.Equal(1, resolvedReceivableFeeRules);

        var feeConstraintCount = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM pg_constraint WHERE contype = 'f' AND conname IN " +
            "('FK_PaymentMethodFeeRules_PaymentMethods', 'FK_SalePayments_PaymentMethodFeeRules', " +
            "'FK_ReceivablePayments_PaymentMethodFeeRules')").SingleAsync();
        Assert.Equal(3, feeConstraintCount);

        var f1MigrationCount = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" IN ({0}, {1})",
            "20261109231727_AddProfitabilityFoundationRev8213",
            "20261110000007_CorrectCashAdvanceCommissionBackfillRev8213").SingleAsync();
        Assert.Equal(2, f1MigrationCount);
    }

    private static async Task<string> ExplainAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var plan = new System.Text.StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plan.AppendLine(reader.GetString(0));
        }

        return plan.ToString();
    }

    private void AssertUsesIndexScan(string label, string plan)
    {
        _output.WriteLine($"--- {label} ---");
        _output.WriteLine(plan);

        Assert.False(
            plan.Contains("Seq Scan", StringComparison.Ordinal),
            $"{label} must not use a sequential scan. Observed plan:{Environment.NewLine}{plan}");
        Assert.True(
            plan.Contains("Index Scan", StringComparison.Ordinal),
            $"{label} must use an Index Scan or Bitmap Index Scan. Observed plan:{Environment.NewLine}{plan}");
    }

    private static ProfitabilityFilterDto CreateWindowFilter() => new()
    {
        Preset = ProfitabilityPeriodPreset.Custom,
        FromDate = new DateOnly(2026, 11, 2),
        ToDate = new DateOnly(2026, 11, 2),
        GroupBy = ProfitabilityGroupBy.Day
    };

    private static ProfitabilityService CreateService(SalesDbContext context)
    {
        var profile = new MonetaryProfileDto
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

        var inventoryService = new Mock<IInventoryService>();
        inventoryService
            .Setup(service => service.GetProductIdsByCategoryAsync(
                It.IsAny<IReadOnlyCollection<int>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<int>)Array.Empty<int>());
        inventoryService
            .Setup(service => service.GetProductCategoryInfoAsync(
                It.IsAny<IReadOnlyCollection<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, ProfitabilityProductInfoDto>)new Dictionary<int, ProfitabilityProductInfoDto>
            {
                [10] = new() { ProductId = 10, ProductName = "Producto A", CategoryId = 1, CategoryName = "Categoría A" },
                [20] = new() { ProductId = 20, ProductName = "Producto B", CategoryId = 2, CategoryName = "Categoría B" },
                [30] = new() { ProductId = 30, ProductName = "Producto E", CategoryId = 1, CategoryName = "Categoría A" },
                [40] = new() { ProductId = 40, ProductName = "Producto D", CategoryId = 3, CategoryName = "Categoría C" }
            });

        return new ProfitabilityService(
            context,
            new StubTimeZoneProvider(),
            profileService.Object,
            inventoryService.Object);
    }

    private static async Task SeedScenarioAsync(SalesDbContext context)
    {
        var ruleEffectiveFrom = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var prePeriod = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

        context.Users.Add(new User
        {
            Id = CashierId,
            Cedula = "V-101",
            Name = "Ana",
            Username = "ana",
            FullName = "Ana Vendedora",
            Role = UserRole.Cashier,
            IsActive = true
        });

        context.Customers.Add(new Customer
        {
            Id = CustomerId,
            CedulaOrRif = "V-99999999",
            Name = "Cliente Rentabilidad",
            Phone = "000-0000000",
            IsActive = true,
            CreditStatus = CreditStatus.Active
        });

        context.PaymentMethods.AddRange(
            new PaymentMethod
            {
                Id = CardPaymentMethodId,
                Name = "Tarjeta VES",
                IsCash = false,
                IsActive = true,
                RequiresReference = true,
                DisplayOrder = 1,
                IsDeleted = false,
                Currency = "VES"
            },
            new PaymentMethod
            {
                Id = CashPaymentMethodId,
                Name = "Efectivo",
                IsCash = true,
                IsActive = true,
                RequiresReference = false,
                DisplayOrder = 2,
                IsDeleted = false,
                Currency = "VES"
            });
        await context.SaveChangesAsync();

        context.PaymentMethodFeeRules.Add(new PaymentMethodFeeRule
        {
            Id = FeeRuleId,
            PaymentMethodId = CardPaymentMethodId,
            FeeType = PaymentMethodFeeType.Percentage,
            Percentage = 1.0000m,
            FixedAmount = null,
            CurrencyCode = "USD",
            EffectiveFromUtc = ruleEffectiveFrom,
            EffectiveToUtc = null,
            IsActive = true,
            CreatedAtUtc = ruleEffectiveFrom,
            UpdatedAtUtc = ruleEffectiveFrom
        });
        await context.SaveChangesAsync();

        context.Sales.AddRange(
            new Sale
            {
                Id = SaleAId,
                InvoiceNumber = 9001,
                Date = InWindow,
                Status = SaleStatus.Completed,
                SaleType = SaleType.Merchandise,
                PriceListType = "Retail",
                TotalUSD = 100m,
                TotalBsS = 5000m,
                Subtotal = 100m,
                SubtotalBsS = 5000m,
                AppliedRate = 50m,
                FinalPaidAmountBsS = 5000m,
                CashierId = CashierId,
                CustomerId = CustomerId
            },
            new Sale
            {
                Id = SaleBId,
                InvoiceNumber = 9002,
                Date = InWindow.AddHours(1),
                Status = SaleStatus.Completed,
                SaleType = SaleType.Merchandise,
                PriceListType = "Retail",
                TotalUSD = 50m,
                TotalBsS = 2500m,
                Subtotal = 50m,
                SubtotalBsS = 2500m,
                AppliedRate = 50m,
                FinalPaidAmountBsS = 2500m,
                CashierId = CashierId,
                CustomerId = CustomerId
            },
            new Sale
            {
                Id = SaleCId,
                InvoiceNumber = 9003,
                Date = InWindow.AddHours(2),
                Status = SaleStatus.Completed,
                SaleType = SaleType.CashAdvance,
                PriceListType = "Retail",
                TotalUSD = 510m,
                TotalBsS = 25500m,
                Subtotal = 510m,
                SubtotalBsS = 25500m,
                AppliedRate = 50m,
                FinalPaidAmountBsS = 25500m,
                CommissionAmountUSD = 10m,
                CommissionAmountLocal = 500m,
                CashierId = CashierId,
                CustomerId = CustomerId
            },
            new Sale
            {
                Id = SaleDId,
                InvoiceNumber = 9004,
                Date = prePeriod,
                Status = SaleStatus.Cancelled,
                SaleType = SaleType.Merchandise,
                PriceListType = "Retail",
                TotalUSD = 50m,
                TotalBsS = 2500m,
                Subtotal = 50m,
                SubtotalBsS = 2500m,
                AppliedRate = 50m,
                FinalPaidAmountBsS = 2500m,
                CancelledAtUtc = InWindow.AddHours(4),
                CashierId = CashierId,
                CustomerId = CustomerId
            },
            new Sale
            {
                Id = SaleEId,
                InvoiceNumber = 9005,
                Date = InWindow.AddHours(5),
                Status = SaleStatus.Completed,
                SaleType = SaleType.Merchandise,
                PriceListType = "Retail",
                TotalUSD = 100m,
                TotalBsS = 5000m,
                Subtotal = 100m,
                SubtotalBsS = 5000m,
                AppliedRate = 50m,
                FinalPaidAmountBsS = 5000m,
                CashierId = CashierId,
                CustomerId = CustomerId
            });
        await context.SaveChangesAsync();

        context.SaleItems.AddRange(
            new SaleItem
            {
                Id = SaleItemAId,
                SaleId = SaleAId,
                ProductId = 10,
                ProductName = "Producto A",
                Quantity = 1m,
                DeliveredQuantity = 1m,
                UnitPrice = 100m,
                Subtotal = 100m,
                UnitPriceBsS = 5000m,
                SubtotalBsS = 5000m,
                UnitCostUSD = 60m,
                UnitCostLocal = 3000m
            },
            new SaleItem
            {
                Id = SaleItemBId,
                SaleId = SaleBId,
                ProductId = 20,
                ProductName = "Producto B",
                Quantity = 1m,
                DeliveredQuantity = 1m,
                UnitPrice = 50m,
                Subtotal = 50m,
                UnitPriceBsS = 2500m,
                SubtotalBsS = 2500m,
                UnitCostUSD = null,
                UnitCostLocal = null
            },
            new SaleItem
            {
                Id = SaleItemDId,
                SaleId = SaleDId,
                ProductId = 40,
                ProductName = "Producto D",
                Quantity = 1m,
                DeliveredQuantity = 1m,
                UnitPrice = 50m,
                Subtotal = 50m,
                UnitPriceBsS = 2500m,
                SubtotalBsS = 2500m,
                UnitCostUSD = 20m,
                UnitCostLocal = 1000m
            },
            new SaleItem
            {
                Id = SaleItemEId,
                SaleId = SaleEId,
                ProductId = 30,
                ProductName = "Producto E",
                Quantity = 1m,
                DeliveredQuantity = 1m,
                UnitPrice = 100m,
                Subtotal = 100m,
                UnitPriceBsS = 5000m,
                SubtotalBsS = 5000m,
                UnitCostUSD = 60m,
                UnitCostLocal = 3000m
            });

        context.SalePayments.AddRange(
            new SalePayment
            {
                Id = SalePaymentAId,
                SaleId = SaleAId,
                PaymentMethodId = CardPaymentMethodId,
                Amount = 100m,
                AmountBsS = 5000m,
                ExchangeRate = 50m,
                CreatedAt = InWindow.AddMinutes(5),
                ReferenceNumber = "CARD-A",
                CurrencyCode = "VES",
                PaymentMethodFeeRuleId = FeeRuleId,
                FeeType = PaymentMethodFeeType.Percentage,
                FeePercentage = 1.0000m,
                FeeCurrencyCode = "USD",
                FeeAmountUsd = 1.0000m,
                FeeAmountLocal = 50.00m,
                FeeRate = 50m,
                FeeConfigured = true,
                FeeRuleEffectiveFromUtc = ruleEffectiveFrom
            },
            new SalePayment
            {
                Id = SalePaymentBId,
                SaleId = SaleBId,
                PaymentMethodId = CashPaymentMethodId,
                Amount = 50m,
                AmountBsS = 2500m,
                ExchangeRate = 50m,
                CreatedAt = InWindow.AddHours(1).AddMinutes(5),
                ReferenceNumber = "CASH-B",
                CurrencyCode = "VES",
                FeeConfigured = false
            },
            new SalePayment
            {
                Id = SalePaymentCId,
                SaleId = SaleCId,
                PaymentMethodId = CardPaymentMethodId,
                Amount = 510m,
                AmountBsS = 25500m,
                ExchangeRate = 50m,
                CreatedAt = InWindow.AddHours(2).AddMinutes(5),
                ReferenceNumber = "CARD-C",
                CurrencyCode = "VES",
                PaymentMethodFeeRuleId = FeeRuleId,
                FeeType = PaymentMethodFeeType.Percentage,
                FeePercentage = 1.0000m,
                FeeCurrencyCode = "USD",
                FeeAmountUsd = 5.1000m,
                FeeAmountLocal = 255.00m,
                FeeRate = 50m,
                FeeConfigured = true,
                FeeRuleEffectiveFromUtc = ruleEffectiveFrom
            },
            new SalePayment
            {
                Id = SalePaymentDId,
                SaleId = SaleDId,
                PaymentMethodId = CardPaymentMethodId,
                Amount = 50m,
                AmountBsS = 2500m,
                ExchangeRate = 50m,
                CreatedAt = prePeriod.AddMinutes(5),
                ReferenceNumber = "CARD-D",
                CurrencyCode = "VES",
                PaymentMethodFeeRuleId = FeeRuleId,
                FeeType = PaymentMethodFeeType.Percentage,
                FeePercentage = 4.0000m,
                FeeCurrencyCode = "USD",
                FeeAmountUsd = 2.0000m,
                FeeAmountLocal = 100.00m,
                FeeRate = 50m,
                FeeConfigured = true,
                FeeRuleEffectiveFromUtc = ruleEffectiveFrom
            });
        await context.SaveChangesAsync();

        context.Receivables.AddRange(
            // The return of the cash sale needs a receivable reference (the production flow
            // creates returns only for credit sales); it stays fully paid and does not add a
            // current-period collection.
            new Receivable
            {
                Id = ReceivableAId,
                SaleId = SaleAId,
                CustomerId = CustomerId,
                OriginalUSD = 100m,
                BalanceUSD = 0m,
                OriginalBsS = 5000m,
                HistoricalRate = 50m,
                IssuedAtUtc = InWindow,
                DueDateUtc = InWindow.AddDays(30),
                Status = ReceivableStatus.Pagada
            },
            new Receivable
            {
                Id = ReceivableEId,
                SaleId = SaleEId,
                CustomerId = CustomerId,
                OriginalUSD = 100m,
                BalanceUSD = 60m,
                OriginalBsS = 5000m,
                HistoricalRate = 50m,
                IssuedAtUtc = InWindow.AddHours(5),
                DueDateUtc = InWindow.AddDays(30),
                Status = ReceivableStatus.Parcial
            });
        await context.SaveChangesAsync();

        context.ReceivablePayments.Add(new ReceivablePayment
        {
            Id = ReceivablePaymentEId,
            CustomerId = CustomerId,
            PaymentMethodId = CardPaymentMethodId,
            AmountUSD = 40m,
            AmountBsS = 2000m,
            PaymentRate = 50m,
            FxDifferenceBsS = 400m,
            ReferenceNumber = "CXC-E",
            ReceivedAtUtc = InWindow.AddHours(6),
            PaymentMethodFeeRuleId = FeeRuleId,
            FeeType = PaymentMethodFeeType.Percentage,
            FeePercentage = 1.0000m,
            FeeCurrencyCode = "USD",
            FeeAmountUsd = 0.4000m,
            FeeAmountLocal = 20.00m,
            FeeRate = 50m,
            FeeConfigured = true,
            FeeRuleEffectiveFromUtc = ruleEffectiveFrom
        });
        await context.SaveChangesAsync();

        context.CreditPaymentAllocations.Add(new CreditPaymentAllocation
        {
            Id = AllocationEId,
            ReceivablePaymentId = ReceivablePaymentEId,
            ReceivableId = ReceivableEId,
            AmountUSD = 40m,
            AmountBsS = 2000m,
            FxDifferenceBsS = 400m
        });
        await context.SaveChangesAsync();

        context.CreditReturns.Add(new CreditReturn
        {
            Id = CreditReturnAId,
            SaleId = SaleAId,
            ReceivableId = ReceivableAId,
            CustomerId = CustomerId,
            ReturnedUSD = 10m,
            RefundedUSD = 10m,
            RefundedBsS = 500m,
            RefundRate = 50m,
            CreatedAtUtc = InWindow.AddHours(1).AddMinutes(30),
            Reason = "Partial return of sale A",
            CreatedByUserId = CashierId
        });
        await context.SaveChangesAsync();

        context.CreditReturnItems.Add(new CreditReturnItem
        {
            Id = CreditReturnItemAId,
            CreditReturnId = CreditReturnAId,
            SaleItemId = SaleItemAId,
            Quantity = 0.1m,
            AmountUSD = 10m
        });
        await context.SaveChangesAsync();

        context.CashDrawerSessions.Add(new CashDrawerSession
        {
            Id = PettyCashSessionId,
            OpenedAt = new DateTime(2026, 11, 2, 6, 0, 0, DateTimeKind.Utc),
            ClosedAt = new DateTime(2026, 11, 2, 20, 0, 0, DateTimeKind.Utc),
            Status = CashDrawerStatus.Closed,
            OpeningBalanceLocal = 1000m,
            OpeningExchangeRate = 50m,
            ClosingBalanceLocal = 750m,
            ClosingExchangeRate = 50m,
            OpeningBalanceUsd = 100m,
            ClosingBalanceUsd = 95m
        });
        await context.SaveChangesAsync();

        context.PettyCashExpenses.Add(new PettyCashExpense
        {
            Id = PettyCashExpenseId,
            VoucherNumber = 5001,
            SessionId = PettyCashSessionId,
            Category = PettyCashCategory.Varios,
            Currency = PettyCashCurrency.Usd,
            AmountBsS = 250.00m,
            AmountUsd = 5.0000m,
            HistoricalRate = 50m,
            Concept = "Gasto operativo del período",
            Beneficiary = "Proveedor",
            ReferenceNumber = "VCC-5001",
            CreatedByUserId = CashierId,
            CreatedAtUtc = InWindow.AddHours(7),
            Status = PettyCashStatus.Active
        });
        await context.SaveChangesAsync();
    }

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
