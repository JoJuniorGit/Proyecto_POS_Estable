using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using CommandCenter.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T2b (Paso 13 / 8.188, S2): integración del gasto de caja chica con el cierre Z. El esperado
/// de cada método de efectivo resta SOLO los gastos Active de su moneda (jamás la heurística del
/// primer efectivo); el bloque OPEX del cierre es informativo (no vuelve a restar) y solo suma
/// Active; y el cierre de sesión persiste el arqueo físico en USD cuando se informa.
/// </summary>
public class PettyCashClosureTests
{
    private const decimal SeedRate = 36.50m;
    private static readonly TimeZoneInfo VetTz = Core.Helpers.TimeZoneHelper.GetVenezuelaTimeZone();

    private static SalesDbContext CreateInMemoryContext()
    {
        return TestDatabaseFactory.CreateSalesDbContext();
    }

    private static DateTime ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), VetTz);
    }

    private static DateTime TodayMidnightUtc()
    {
        var venDate = Core.Helpers.TimeZoneHelper.GetVenezuelaDate(DateTime.UtcNow);
        return ToUtc(venDate, TimeOnly.MinValue);
    }

    private static DailyClosureService CreateService(SalesDbContext context)
    {
        return DailyClosureTestHelper.CreateService(context);
    }

    private static async Task SeedPaymentMethodsAsync(
        SalesDbContext context,
        bool includeBsSCash = true,
        bool includeUsdCash = true)
    {
        if (includeBsSCash)
        {
            context.PaymentMethods.Add(new PaymentMethod
            {
                Id = 1,
                Name = "Efectivo Bs.S",
                IsCash = true,
                IsActive = true,
                DisplayOrder = 1
            });
        }

        if (includeUsdCash)
        {
            context.PaymentMethods.Add(new PaymentMethod
            {
                Id = 2,
                Name = "Efectivo USD",
                IsCash = true,
                IsActive = true,
                DisplayOrder = 2
            });
        }

        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 3,
            Name = "Punto de Venta",
            IsCash = false,
            IsActive = true,
            DisplayOrder = 3
        });

        await context.SaveChangesAsync();
    }

    private static async Task<CashDrawerSession> SeedOpenSessionAsync(SalesDbContext context, DateTime openedAtUtc)
    {
        var session = new CashDrawerSession
        {
            OpenedAt = openedAtUtc,
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = 1000m,
            OpeningExchangeRate = SeedRate
        };
        context.CashDrawerSessions.Add(session);
        await context.SaveChangesAsync();
        return session;
    }

    private static async Task SeedSalePaymentAsync(
        SalesDbContext context,
        int saleId,
        int paymentMethodId,
        decimal amountBsS,
        DateTime dateUtc)
    {
        context.Sales.Add(new Sale
        {
            Id = saleId,
            Status = SaleStatus.Completed,
            Date = dateUtc,
            TotalUSD = Math.Round(amountBsS / SeedRate, 2, MidpointRounding.AwayFromZero),
            TotalBsS = amountBsS,
            AppliedRate = SeedRate
        });
        context.SalePayments.Add(new SalePayment
        {
            Id = saleId,
            SaleId = saleId,
            PaymentMethodId = paymentMethodId,
            AmountBsS = amountBsS,
            ExchangeRate = SeedRate
        });
        await context.SaveChangesAsync();
    }

    private static async Task<PettyCashExpense> SeedExpenseAsync(
        SalesDbContext context,
        int sessionId,
        PettyCashCurrency currency,
        decimal amountBsS,
        decimal amountUsd,
        DateTime createdAtUtc,
        PettyCashCategory category = PettyCashCategory.Varios,
        PettyCashStatus status = PettyCashStatus.Active,
        long voucherNumber = 1)
    {
        var expense = new PettyCashExpense
        {
            VoucherNumber = voucherNumber,
            SessionId = sessionId,
            Category = category,
            Currency = currency,
            AmountBsS = amountBsS,
            AmountUsd = amountUsd,
            HistoricalRate = SeedRate,
            Concept = "Compra operativa",
            Beneficiary = "Proveedor de prueba",
            ReferenceNumber = "REF-PC-001",
            CreatedByUserId = 1,
            CreatedAtUtc = createdAtUtc,
            Status = status
        };
        context.PettyCashExpenses.Add(expense);
        await context.SaveChangesAsync();
        return expense;
    }

    private static async Task<PettyCashExpense> SeedUsdExpenseWithoutCashMethodAsync(
        SalesDbContext context,
        int sessionId,
        DateTime createdAtUtc)
    {
        return await SeedExpenseAsync(
            context,
            sessionId,
            PettyCashCurrency.Usd,
            amountBsS: 182.50m,
            amountUsd: 5m,
            createdAtUtc: createdAtUtc,
            category: PettyCashCategory.FletesAcarreo);
    }

    private static decimal ExpectedFor(List<ExpectedTotalDto> totals, int paymentMethodId)
    {
        return totals.Single(total => total.PaymentMethodId == paymentMethodId).ExpectedAmountBsS;
    }

    // ------------------------------------------------------------------ deducción por moneda

    [Fact]
    public async Task GetExpectedTotals_BsSExpense_IsDeductedOnlyFromTheBsSCashMethod()
    {
        using var context = CreateInMemoryContext();
        await SeedPaymentMethodsAsync(context);
        var session = await SeedOpenSessionAsync(context, TodayMidnightUtc());
        var now = DateTime.UtcNow;

        await SeedSalePaymentAsync(context, 1, paymentMethodId: 1, amountBsS: 1000m, dateUtc: now);
        await SeedSalePaymentAsync(context, 2, paymentMethodId: 2, amountBsS: 500m, dateUtc: now);
        await SeedExpenseAsync(context, session.Id, PettyCashCurrency.BsS, 120m, 3.2877m, now);

        var totals = await CreateService(context).GetExpectedTotalsByPaymentMethodAsync(now);

        Assert.Equal(880m, ExpectedFor(totals, 1));
        Assert.Equal(500m, ExpectedFor(totals, 2));
    }

    [Fact]
    public async Task GetExpectedTotals_UsdExpense_IsDeductedOnlyFromTheUsdCashMethod()
    {
        using var context = CreateInMemoryContext();
        await SeedPaymentMethodsAsync(context);
        var session = await SeedOpenSessionAsync(context, TodayMidnightUtc());
        var now = DateTime.UtcNow;

        await SeedSalePaymentAsync(context, 1, paymentMethodId: 1, amountBsS: 1000m, dateUtc: now);
        await SeedSalePaymentAsync(context, 2, paymentMethodId: 2, amountBsS: 500m, dateUtc: now);
        await SeedExpenseAsync(context, session.Id, PettyCashCurrency.Usd, 182.50m, 5m, now);

        var totals = await CreateService(context).GetExpectedTotalsByPaymentMethodAsync(now);

        Assert.Equal(1000m, ExpectedFor(totals, 1));
        Assert.Equal(317.50m, ExpectedFor(totals, 2));
    }

    [Fact]
    public async Task GetExpectedTotals_ExpenseWithoutCashMethodOfItsCurrency_IsIgnoredWithoutAffectingOthers()
    {
        using var context = CreateInMemoryContext();
        await SeedPaymentMethodsAsync(context, includeUsdCash: false);
        var session = await SeedOpenSessionAsync(context, TodayMidnightUtc());
        var now = DateTime.UtcNow;

        await SeedSalePaymentAsync(context, 1, paymentMethodId: 1, amountBsS: 1000m, dateUtc: now);
        await SeedUsdExpenseWithoutCashMethodAsync(context, session.Id, now);

        var totals = await CreateService(context).GetExpectedTotalsByPaymentMethodAsync(now);

        Assert.Equal(1000m, ExpectedFor(totals, 1));
        Assert.DoesNotContain(totals, total => total.PaymentMethodName.Contains("USD", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetExpectedTotals_CancelledExpense_IsNotDeducted()
    {
        using var context = CreateInMemoryContext();
        await SeedPaymentMethodsAsync(context);
        var session = await SeedOpenSessionAsync(context, TodayMidnightUtc());
        var now = DateTime.UtcNow;

        await SeedSalePaymentAsync(context, 1, paymentMethodId: 1, amountBsS: 1000m, dateUtc: now);
        await SeedExpenseAsync(
            context, session.Id, PettyCashCurrency.BsS, 120m, 3.2877m, now,
            status: PettyCashStatus.Cancelled);

        var totals = await CreateService(context).GetExpectedTotalsByPaymentMethodAsync(now);

        Assert.Equal(1000m, ExpectedFor(totals, 1));
    }

    [Fact]
    public async Task GetExpectedTotals_KeepsChangeAndCreditRefundSemantics_WithPettyCashExpense()
    {
        using var context = CreateInMemoryContext();
        await SeedPaymentMethodsAsync(context);
        var session = await SeedOpenSessionAsync(context, TodayMidnightUtc());
        var now = DateTime.UtcNow;

        await SeedSalePaymentAsync(context, 1, paymentMethodId: 1, amountBsS: 1000m, dateUtc: now);

        context.CashTransactions.AddRange(
            new CashTransaction
            {
                SessionId = session.Id,
                TransactionTime = now,
                Type = CashTransactionType.Expense,
                Source = CashTransactionSource.SalePayment,
                IsPhysicalCash = true,
                PaymentMethodId = 1,
                AmountLocal = 150m
            },
            new CashTransaction
            {
                SessionId = session.Id,
                TransactionTime = now,
                Type = CashTransactionType.Expense,
                Source = CashTransactionSource.CreditRefund,
                IsPhysicalCash = true,
                PaymentMethodId = 1,
                AmountLocal = 50m
            },
            // La reversa de un gasto anulado NUNCA se suma al esperado (el par netea 0 en gaveta).
            new CashTransaction
            {
                SessionId = session.Id,
                TransactionTime = now,
                Type = CashTransactionType.Income,
                Source = CashTransactionSource.PettyCashReversal,
                IsPhysicalCash = true,
                PaymentMethodId = 1,
                AmountLocal = 100m
            });
        await context.SaveChangesAsync();

        await SeedExpenseAsync(context, session.Id, PettyCashCurrency.BsS, 100m, 2.7397m, now);

        var totals = await CreateService(context).GetExpectedTotalsByPaymentMethodAsync(now);

        // 1000 ventas − 150 vuelto − 50 reembolso − 100 caja chica; la reversa no suma.
        Assert.Equal(700m, ExpectedFor(totals, 1));
    }

    // ------------------------------------------------------------------ renglón OPEX del cierre

    [Fact]
    public async Task GetClosure_PettyCashSummary_OnlyActiveExpensesAndNoDoubleDeduction()
    {
        using var context = CreateInMemoryContext();
        await SeedPaymentMethodsAsync(context);
        var session = await SeedOpenSessionAsync(context, TodayMidnightUtc());
        var now = DateTime.UtcNow;

        await SeedSalePaymentAsync(context, 1, paymentMethodId: 1, amountBsS: 1000m, dateUtc: now);
        await SeedSalePaymentAsync(context, 2, paymentMethodId: 2, amountBsS: 500m, dateUtc: now);

        await SeedExpenseAsync(
            context, session.Id, PettyCashCurrency.BsS, 100m, 2.7397m, now,
            category: PettyCashCategory.InsumosLimpieza, voucherNumber: 1);
        await SeedExpenseAsync(
            context, session.Id, PettyCashCurrency.Usd, 182.50m, 5m, now,
            category: PettyCashCategory.FletesAcarreo, voucherNumber: 2);
        await SeedExpenseAsync(
            context, session.Id, PettyCashCurrency.BsS, 50m, 1.3699m, now,
            category: PettyCashCategory.Varios, status: PettyCashStatus.Cancelled, voucherNumber: 3);

        var service = CreateService(context);
        var command = new CreateClosureCommand(
            now,
            "Admin",
            null,
            new List<DeclaredPaymentAmount>
            {
                new(1, 900m),
                new(2, 6.35m)
            });

        var result = await service.CreateClosureFromCommandAsync(command, CancellationToken.None);

        var saved = await context.DailyClosures.AsNoTracking()
            .Include(closure => closure.Details)
            .SingleAsync(closure => closure.Id == result.ClosureId);
        // La resta al esperado ocurre UNA sola vez: si el OPEX también restara, serían 800/135.
        Assert.Equal(900m, saved.Details.Single(detail => detail.PaymentMethodId == 1).ExpectedAmountBsS);
        Assert.Equal(317.50m, saved.Details.Single(detail => detail.PaymentMethodId == 2).ExpectedAmountBsS);

        var closure = await service.GetClosureAsync(result.ClosureId);
        Assert.NotNull(closure);
        Assert.NotNull(closure!.PettyCashSummary);

        var summary = closure.PettyCashSummary!;
        Assert.Equal(2, summary.ActiveCount);
        Assert.Equal(282.50m, summary.TotalBsS);
        Assert.Equal(7.7397m, summary.TotalUsd);
        Assert.Equal(2, summary.ByCategory.Count);

        var insumos = summary.ByCategory.Single(category => category.Category == PettyCashCategory.InsumosLimpieza);
        Assert.Equal(1, insumos.Count);
        Assert.Equal(100m, insumos.TotalBsS);
        Assert.Equal(2.7397m, insumos.TotalUsd);

        var fletes = summary.ByCategory.Single(category => category.Category == PettyCashCategory.FletesAcarreo);
        Assert.Equal(1, fletes.Count);
        Assert.Equal(182.50m, fletes.TotalBsS);
        Assert.Equal(5m, fletes.TotalUsd);
    }

    [Fact]
    public async Task GetClosure_WithOnlyCancelledExpenses_ReportsEmptyOpexSummary()
    {
        using var context = CreateInMemoryContext();
        await SeedPaymentMethodsAsync(context);
        var session = await SeedOpenSessionAsync(context, TodayMidnightUtc());
        var now = DateTime.UtcNow;

        await SeedSalePaymentAsync(context, 1, paymentMethodId: 1, amountBsS: 1000m, dateUtc: now);
        await SeedExpenseAsync(
            context, session.Id, PettyCashCurrency.BsS, 120m, 3.2877m, now,
            status: PettyCashStatus.Cancelled);

        var service = CreateService(context);
        var result = await service.CreateClosureFromCommandAsync(
            new CreateClosureCommand(now, "Admin", null, new List<DeclaredPaymentAmount> { new(1, 1000m) }),
            CancellationToken.None);

        var closure = await service.GetClosureAsync(result.ClosureId);

        Assert.NotNull(closure);
        Assert.NotNull(closure!.PettyCashSummary);
        var summary = closure.PettyCashSummary!;
        Assert.Equal(0, summary.ActiveCount);
        Assert.Equal(0m, summary.TotalBsS);
        Assert.Equal(0m, summary.TotalUsd);
        Assert.Empty(summary.ByCategory);
        Assert.Equal(1000m, closure.Details.Single(detail => detail.PaymentMethodId == 1).ExpectedAmountBsS);
    }

    [Fact]
    public async Task GetLatestClosure_IncludesPettyCashSummary()
    {
        using var context = CreateInMemoryContext();
        await SeedPaymentMethodsAsync(context);
        var session = await SeedOpenSessionAsync(context, TodayMidnightUtc());
        var now = DateTime.UtcNow;

        await SeedSalePaymentAsync(context, 1, paymentMethodId: 1, amountBsS: 500m, dateUtc: now);
        await SeedExpenseAsync(
            context, session.Id, PettyCashCurrency.BsS, 40m, 1.0959m, now,
            category: PettyCashCategory.Papeleria);

        var service = CreateService(context);
        await service.CreateClosureFromCommandAsync(
            new CreateClosureCommand(now, "Admin", null, new List<DeclaredPaymentAmount> { new(1, 460m) }),
            CancellationToken.None);

        var latest = await service.GetLatestClosureAsync();

        Assert.NotNull(latest);
        Assert.NotNull(latest!.PettyCashSummary);
        Assert.Equal(1, latest.PettyCashSummary!.ActiveCount);
        Assert.Equal(40m, latest.PettyCashSummary.TotalBsS);
        Assert.Equal(PettyCashCategory.Papeleria, latest.PettyCashSummary.ByCategory.Single().Category);
    }

    // ------------------------------------------------------------------ arqueo físico USD del cierre

    [Fact]
    public async Task CloseSessionAsync_PersistsClosingBalanceUsd_WhenProvided()
    {
        using var context = CreateInMemoryContext();
        var drawer = new CashDrawerService(context);
        var session = await drawer.OpenSessionAsync(500m, 50m);

        var closed = await drawer.CloseSessionAsync(700m, 50m, actualClosingBalanceUsd: 25.50m);

        Assert.Equal(CashDrawerStatus.Closed, closed.Status);
        Assert.Equal(700m, closed.ClosingBalanceLocal);
        Assert.Equal(25.50m, await context.CashDrawerSessions
            .AsNoTracking()
            .Where(candidate => candidate.Id == session.Id)
            .Select(candidate => candidate.ClosingBalanceUsd)
            .SingleAsync());
    }

    [Fact]
    public async Task CloseSessionAsync_WithoutUsdClosingBalance_KeepsPreviousBehavior()
    {
        using var context = CreateInMemoryContext();
        var drawer = new CashDrawerService(context);
        var session = await drawer.OpenSessionAsync(500m, 50m);

        var closed = await drawer.CloseSessionAsync(500m, 50m);

        Assert.Equal(CashDrawerStatus.Closed, closed.Status);
        Assert.Equal(500m, closed.ClosingBalanceLocal);
        Assert.Null(await context.CashDrawerSessions
            .AsNoTracking()
            .Where(candidate => candidate.Id == session.Id)
            .Select(candidate => candidate.ClosingBalanceUsd)
            .SingleAsync());
    }

    [Fact]
    public async Task CloseSessionAsync_WithNegativeUsdClosingBalance_IsRejected()
    {
        using var context = CreateInMemoryContext();
        var drawer = new CashDrawerService(context);
        var session = await drawer.OpenSessionAsync(500m, 50m);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            drawer.CloseSessionAsync(500m, 50m, actualClosingBalanceUsd: -0.01m));

        var persisted = await context.CashDrawerSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == session.Id);
        Assert.Equal(CashDrawerStatus.Open, persisted.Status);
        Assert.Null(persisted.ClosingBalanceUsd);
    }
}
