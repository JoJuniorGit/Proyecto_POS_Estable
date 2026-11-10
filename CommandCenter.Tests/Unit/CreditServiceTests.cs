using System;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// F2a (Paso 11, S1/S2): contrato del perfil crediticio del cliente — cupo disponible,
/// utilización, mora dinámica (fecha de vencimiento + gracia vs. día Venezuela) y matriz de
/// elegibilidad con bloqueos en español. Sin bypass de sobregiro (T3 agrega la autorización).
/// </summary>
public class CreditServiceTests
{
    private static readonly DateTime FixedUtc = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetCreditProfileAsync_ComputesDebtAvailableUtilizationAndOverdue()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(creditLimitUsd: 1000m, paymentTermDays: 30, graceDays: 3);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        // Abiertas y con saldo: 300 (vigente) + 200 (parcial) + 100 (vencida, fuera de gracia).
        await AddReceivableAsync(context, customer.Id, 300m, ReceivableStatus.Vigente, DateTime.UtcNow.AddDays(10));
        await AddReceivableAsync(context, customer.Id, 200m, ReceivableStatus.Parcial, DateTime.UtcNow.AddDays(-10));
        await AddReceivableAsync(context, customer.Id, 100m, ReceivableStatus.Vencida, DateTime.UtcNow.AddDays(-20));
        // Excluidas del saldo deudor: pagada, polvo decimal (≤ 0.005) y anulada.
        await AddReceivableAsync(context, customer.Id, 50m, ReceivableStatus.Pagada, DateTime.UtcNow.AddDays(-30));
        await AddReceivableAsync(context, customer.Id, 0.0040m, ReceivableStatus.Vigente, DateTime.UtcNow.AddDays(-30));
        await AddReceivableAsync(context, customer.Id, 25m, ReceivableStatus.Anulada, DateTime.UtcNow.AddDays(-30));

        var service = new CreditService(context);
        var profile = await service.GetCreditProfileAsync(customer.Id);

        Assert.Equal(customer.Id, profile.CustomerId);
        Assert.True(profile.IsActive);
        Assert.True(profile.IsCreditEnabled);
        Assert.Equal(CreditStatus.Active, profile.CreditStatus);
        Assert.Equal(1000m, profile.CreditLimitUSD);
        Assert.Equal(600.0000m, profile.CurrentDebtUSD);
        Assert.Equal(400.0000m, profile.AvailableCreditUSD);
        Assert.Equal(60.00m, profile.UtilizationPercent);
        Assert.Equal(30, profile.PaymentTermDays);
        Assert.Equal(3, profile.CreditGraceDays);
        Assert.Equal(300.0000m, profile.OverdueUSD);
        Assert.Equal(2, profile.OverdueCount);
    }

    [Fact]
    public async Task GetCreditProfileAsync_WithoutCreditLimit_ClampsAvailabilityToZeroAndZeroUtilization()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(creditLimitUsd: 0m);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        await AddReceivableAsync(context, customer.Id, 50m, ReceivableStatus.Vigente, DateTime.UtcNow.AddDays(10));

        var profile = await new CreditService(context).GetCreditProfileAsync(customer.Id);

        Assert.Equal(50.0000m, profile.CurrentDebtUSD);
        Assert.Equal(0m, profile.AvailableCreditUSD); // clamp: nunca negativo
        Assert.Equal(0m, profile.UtilizationPercent);
        Assert.Equal(0, profile.OverdueCount);
    }

    [Fact]
    public async Task GetCreditProfileAsync_DebtAboveLimit_ClampsAvailabilityToZeroAndUtilizationAbove100()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(creditLimitUsd: 100m);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        // La deuda supera el cupo: disponible 0.00 y utilización por encima de 100%.
        await AddReceivableAsync(context, customer.Id, 150m, ReceivableStatus.Parcial, DateTime.UtcNow.AddDays(5));

        var profile = await new CreditService(context).GetCreditProfileAsync(customer.Id);

        Assert.Equal(150.0000m, profile.CurrentDebtUSD);
        Assert.Equal(0m, profile.AvailableCreditUSD);
        Assert.Equal(150.00m, profile.UtilizationPercent);
    }

    [Fact]
    public async Task GetCreditProfileAsync_ThrowsWhenCustomerDoesNotExist()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new CreditService(context);

        await Assert.ThrowsAsync<System.Collections.Generic.KeyNotFoundException>(() => service.GetCreditProfileAsync(9999));
    }

    [Fact]
    public async Task EvaluateEligibilityAsync_EligibleCustomer_ReturnsAvailableCreditWithoutBlockers()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(creditLimitUsd: 500m);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        await AddReceivableAsync(context, customer.Id, 100m, ReceivableStatus.Vigente, DateTime.UtcNow.AddDays(10));

        var result = await new CreditService(context).EvaluateEligibilityAsync(customer.Id, 300m);

        Assert.True(result.IsEligible);
        Assert.Empty(result.Blockers);
        Assert.Equal(500m, result.CreditLimitUSD);
        Assert.Equal(100.0000m, result.CurrentDebtUSD);
        Assert.Equal(400.0000m, result.AvailableCreditUSD);
        Assert.Equal(300m, result.RequestedAmountUSD);
    }

    [Fact]
    public async Task EvaluateEligibilityAsync_CreditDisabled_BlocksWithSpanishMessage()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(creditLimitUsd: 500m, isCreditEnabled: false);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var result = await new CreditService(context).EvaluateEligibilityAsync(customer.Id, 100m);

        Assert.False(result.IsEligible);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("línea de crédito"));
    }

    [Fact]
    public async Task EvaluateEligibilityAsync_ManuallyBlocked_BlocksWithSpanishMessage()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(creditLimitUsd: 500m, creditStatus: CreditStatus.ManuallyBlocked);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var result = await new CreditService(context).EvaluateEligibilityAsync(customer.Id, 100m);

        Assert.False(result.IsEligible);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("bloqueado manualmente"));
    }

    [Fact]
    public async Task EvaluateEligibilityAsync_SuspendedByMora_BlocksWithSpanishMessage()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(creditLimitUsd: 500m, creditStatus: CreditStatus.SuspendedByMora);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var result = await new CreditService(context).EvaluateEligibilityAsync(customer.Id, 100m);

        Assert.False(result.IsEligible);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("morosidad"));
    }

    [Fact]
    public async Task EvaluateEligibilityAsync_OverdueBeyondGrace_BlocksWithSpanishMessage()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(creditLimitUsd: 500m, graceDays: 3);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        // Vencimiento hace 10 días con 3 de gracia: mora dinámica (sin job materializador).
        await AddReceivableAsync(context, customer.Id, 100m, ReceivableStatus.Vigente, DateTime.UtcNow.AddDays(-10));

        var result = await new CreditService(context).EvaluateEligibilityAsync(customer.Id, 100m);

        Assert.False(result.IsEligible);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("vencidas"));
    }

    [Fact]
    public async Task EvaluateEligibilityAsync_OverdueWithinGrace_DoesNotBlock()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(creditLimitUsd: 500m, graceDays: 3);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        // Vencimiento hace 2 días dentro de 3 de gracia: aún no es mora.
        await AddReceivableAsync(context, customer.Id, 100m, ReceivableStatus.Vigente, DateTime.UtcNow.AddDays(-2));

        var result = await new CreditService(context).EvaluateEligibilityAsync(customer.Id, 100m);

        Assert.True(result.IsEligible);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public async Task EvaluateEligibilityAsync_InsufficientAvailable_BlocksWithAmounts()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(creditLimitUsd: 100m);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var result = await new CreditService(context).EvaluateEligibilityAsync(customer.Id, 150m);

        Assert.False(result.IsEligible);
        Assert.Equal(100.0000m, result.AvailableCreditUSD);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("Crédito insuficiente"));
    }

    [Fact]
    public async Task EvaluateEligibilityAsync_OverIndebtedCustomer_ClampsAvailabilityToZeroAndBlocksEvenSmallAmounts()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(creditLimitUsd: 100m);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        await AddReceivableAsync(context, customer.Id, 150m, ReceivableStatus.Parcial, DateTime.UtcNow.AddDays(5));

        var result = await new CreditService(context).EvaluateEligibilityAsync(customer.Id, 10m);

        Assert.False(result.IsEligible);
        Assert.Equal(0m, result.AvailableCreditUSD); // clamp: disponible 0.00, no negativo
        Assert.Contains(result.Blockers, blocker => blocker.Contains("Crédito insuficiente"));
    }

    [Fact]
    public async Task EvaluateEligibilityAsync_InactiveCustomer_BlocksWithSpanishMessage()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(creditLimitUsd: 500m, isActive: false);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var result = await new CreditService(context).EvaluateEligibilityAsync(customer.Id, 100m);

        Assert.False(result.IsEligible);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("inactivo"));
    }

    [Fact]
    public async Task EvaluateEligibilityAsync_InvalidPaymentTerm_BlocksWithSpanishMessage()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(creditLimitUsd: 500m, paymentTermDays: 0);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var result = await new CreditService(context).EvaluateEligibilityAsync(customer.Id, 100m);

        Assert.False(result.IsEligible);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("plazo"));
    }

    [Fact]
    public async Task EvaluateEligibilityAsync_MissingCustomer_BlocksInsteadOfThrowing()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var result = await new CreditService(context).EvaluateEligibilityAsync(9999, 100m);

        Assert.False(result.IsEligible);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("no existe"));
    }

    private static Customer BuildCustomer(
        decimal creditLimitUsd,
        int paymentTermDays = 30,
        int graceDays = 0,
        bool isCreditEnabled = true,
        bool isActive = true,
        CreditStatus creditStatus = CreditStatus.Active)
    {
        return new Customer
        {
            CedulaOrRif = "V-" + Guid.NewGuid().ToString("N").Substring(0, 8),
            Name = "Cliente Credito Test",
            IsCreditEnabled = isCreditEnabled,
            CreditLimitUSD = creditLimitUsd,
            PaymentTermDays = paymentTermDays,
            CreditGraceDays = graceDays,
            CreditStatus = creditStatus,
            IsActive = isActive
        };
    }

    private static async Task AddReceivableAsync(
        SalesDbContext context,
        int customerId,
        decimal balanceUsd,
        ReceivableStatus status,
        DateTime dueDateUtc)
    {
        var sale = new Sale
        {
            Status = SaleStatus.Completed,
            Date = FixedUtc,
            AppliedRate = 50m,
            TotalUSD = balanceUsd,
            TotalBsS = Math.Round(balanceUsd * 50m, 2, MidpointRounding.AwayFromZero)
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        context.Receivables.Add(new Receivable
        {
            SaleId = sale.Id,
            CustomerId = customerId,
            OriginalUSD = balanceUsd,
            BalanceUSD = balanceUsd,
            OriginalBsS = Math.Round(balanceUsd * 50m, 2, MidpointRounding.AwayFromZero),
            HistoricalRate = 50m,
            IssuedAtUtc = FixedUtc,
            DueDateUtc = dueDateUtc,
            Status = status
        });
        await context.SaveChangesAsync();
    }
}
