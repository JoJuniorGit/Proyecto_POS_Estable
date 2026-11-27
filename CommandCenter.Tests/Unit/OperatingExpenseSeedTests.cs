using System;
using System.Linq;
using Backend.API.Startup;
using CommandCenter.Tests.Builders;
using Sales.Module.Entities;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// P0 (T3/S2/S4): semilla idempotente de los metodos digitales reales, sus reglas de comision y
/// los dos gastos operativos mensuales. No renombra, borra ni desactiva metodos historicos y no
/// sobrescribe una regla activa existente.
/// </summary>
public class OperatingExpenseSeedTests
{
    private const string DebitCardName = "Tarjeta de Débito (TDD)";
    private const string CreditCardName = "Tarjeta de Crédito (TDC)";
    private const string BioPagoName = "BIOpago";
    private const string CreditCardExpenseDescription = "Mantenimiento de cuenta TDC";
    private const string PosExpenseDescription = "Gastos mensuales del POS";

    [Fact]
    public void EnsureP0Defaults_CreatesDigitalMethodsRulesAndMonthlyExpenses()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        DatabaseInitializer.EnsureP0CommissionAndOperatingExpenseDefaults(context);

        var methods = context.PaymentMethods.ToList();
        var debit = Assert.Single(methods, method => method.Name == DebitCardName);
        var credit = Assert.Single(methods, method => method.Name == CreditCardName);
        var bioPago = Assert.Single(methods, method => method.Name == BioPagoName);

        foreach (var method in new[] { debit, credit, bioPago })
        {
            Assert.False(method.IsCash);
            Assert.True(method.RequiresReference);
            Assert.True(method.IsActive);
            Assert.False(method.IsDeleted);
            Assert.Equal("Bs.S", method.Currency);
        }

        Assert.True(debit.DisplayOrder > 0);
        Assert.True(credit.DisplayOrder > debit.DisplayOrder);
        Assert.True(bioPago.DisplayOrder > credit.DisplayOrder);

        var rules = context.PaymentMethodFeeRules.ToList();
        Assert.Equal(3, rules.Count);
        AssertRule(rules, debit.Id, 0.75m);
        AssertRule(rules, credit.Id, 2.50m);
        AssertRule(rules, bioPago.Id, 2.70m);
        Assert.All(rules, rule =>
        {
            Assert.Equal(PaymentMethodFeeType.Percentage, rule.FeeType);
            Assert.Equal("USD", rule.CurrencyCode);
            Assert.True(rule.IsActive);
            Assert.Equal(TimeSpan.Zero, rule.EffectiveFromUtc.TimeOfDay);
            Assert.Equal(DateTime.UtcNow.Date, rule.EffectiveFromUtc.Date);
        });

        var expenses = context.OperatingExpenses.ToList();
        var venezuelaToday = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var expectedFrom = new DateOnly(venezuelaToday.Year, venezuelaToday.Month, 1);
        var maintenance = Assert.Single(expenses, expense => expense.Description == CreditCardExpenseDescription);
        Assert.Equal(1.60m, maintenance.MonthlyAmountUsd);
        Assert.Equal(credit.Id, maintenance.PaymentMethodId);
        Assert.Equal(expectedFrom, maintenance.EffectiveFromDate);
        Assert.Null(maintenance.EffectiveToDate);
        Assert.True(maintenance.IsActive);

        var posExpense = Assert.Single(expenses, expense => expense.Description == PosExpenseDescription);
        Assert.Equal(41.12m, posExpense.MonthlyAmountUsd);
        Assert.Null(posExpense.PaymentMethodId);
        Assert.Equal(expectedFrom, posExpense.EffectiveFromDate);
        Assert.Null(posExpense.EffectiveToDate);
        Assert.True(posExpense.IsActive);
    }

    [Fact]
    public void EnsureP0Defaults_IsIdempotentWhenRunTwice()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        DatabaseInitializer.EnsureP0CommissionAndOperatingExpenseDefaults(context);
        DatabaseInitializer.EnsureP0CommissionAndOperatingExpenseDefaults(context);

        Assert.Equal(3, context.PaymentMethods.Count());
        Assert.Equal(3, context.PaymentMethodFeeRules.Count());
        Assert.Equal(2, context.OperatingExpenses.Count());
    }

    [Fact]
    public void EnsureP0Defaults_KeepsAnExistingActiveRuleAndDoesNotTouchLegacyMethods()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var debit = new PaymentMethod
        {
            Name = DebitCardName,
            IsActive = true,
            IsCash = false,
            RequiresReference = true,
            Currency = "Bs.S"
        };
        var legacyCard = new PaymentMethod
        {
            Name = "Tarjeta (Punto de Venta)",
            IsActive = true,
            IsCash = false,
            RequiresReference = true,
            Currency = "Bs.S"
        };
        var cash = new PaymentMethod
        {
            Name = "Efectivo",
            IsActive = true,
            IsCash = true,
            Currency = "Bs.S"
        };
        context.PaymentMethods.AddRange(debit, legacyCard, cash);
        context.SaveChanges();

        context.PaymentMethodFeeRules.Add(new PaymentMethodFeeRule
        {
            PaymentMethodId = debit.Id,
            FeeType = PaymentMethodFeeType.Percentage,
            Percentage = 1.00m,
            CurrencyCode = "USD",
            EffectiveFromUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            IsActive = true
        });
        context.SaveChanges();

        DatabaseInitializer.EnsureP0CommissionAndOperatingExpenseDefaults(context);

        // The admin-configured active rule wins: the seed must not add a second TDD rule.
        var debitRules = context.PaymentMethodFeeRules
            .Where(rule => rule.PaymentMethodId == debit.Id)
            .ToList();
        var debitRule = Assert.Single(debitRules);
        Assert.Equal(1.00m, debitRule.Percentage);

        Assert.DoesNotContain(
            context.PaymentMethodFeeRules,
            rule => rule.PaymentMethodId == legacyCard.Id || rule.PaymentMethodId == cash.Id);

        Assert.Equal(3, context.PaymentMethodFeeRules.Count());

        // Pre-existing methods keep their identity: no rename, no deletion, no deactivation.
        var reloadedLegacyCard = context.PaymentMethods.Single(method => method.Id == legacyCard.Id);
        Assert.Equal("Tarjeta (Punto de Venta)", reloadedLegacyCard.Name);
        Assert.False(reloadedLegacyCard.IsDeleted);
        Assert.True(reloadedLegacyCard.IsActive);

        var reloadedCash = context.PaymentMethods.Single(method => method.Id == cash.Id);
        Assert.Equal("Efectivo", reloadedCash.Name);
        Assert.True(reloadedCash.IsActive);
    }

    [Fact]
    public void EnsureP0Defaults_DoesNotResurrectAnInactiveOperatingExpense()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.OperatingExpenses.Add(new OperatingExpense
        {
            Description = PosExpenseDescription,
            MonthlyAmountUsd = 41.12m,
            EffectiveFromDate = new DateOnly(2026, 1, 1),
            IsActive = false
        });
        context.SaveChanges();

        DatabaseInitializer.EnsureP0CommissionAndOperatingExpenseDefaults(context);

        var posExpenses = context.OperatingExpenses
            .Where(expense => expense.Description == PosExpenseDescription)
            .ToList();
        var existing = Assert.Single(posExpenses);
        Assert.False(existing.IsActive);
    }

    [Fact]
    public void EnsureP0Defaults_DoesNotDuplicateAnExistingOperatingExpense()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.OperatingExpenses.Add(new OperatingExpense
        {
            Description = PosExpenseDescription,
            MonthlyAmountUsd = 41.12m,
            EffectiveFromDate = new DateOnly(2026, 1, 1),
            IsActive = true
        });
        context.SaveChanges();

        DatabaseInitializer.EnsureP0CommissionAndOperatingExpenseDefaults(context);

        var posExpenses = context.OperatingExpenses
            .Where(expense => expense.Description == PosExpenseDescription)
            .ToList();
        Assert.Single(posExpenses);
        Assert.Single(context.OperatingExpenses, expense => expense.Description == CreditCardExpenseDescription);
    }

    private static void AssertRule(
        System.Collections.Generic.IEnumerable<PaymentMethodFeeRule> rules,
        int paymentMethodId,
        decimal percentage)
    {
        var rule = Assert.Single(rules, candidate => candidate.PaymentMethodId == paymentMethodId);
        Assert.Equal(percentage, rule.Percentage);
    }
}
