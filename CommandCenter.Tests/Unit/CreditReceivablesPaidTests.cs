using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// CxC (Deudas Pagadas): aislamiento de la cartera operativa, historial realizado con tasas
/// congeladas, cronología por obligación y bitácora plana sin doble conteo FIFO.
/// </summary>
public class CreditReceivablesPaidTests
{
    private static readonly DateTime NowUtc = DateTime.UtcNow;

    [Fact]
    public async Task GetCreditReceivablesAsync_ExcludesPaidObligationsFromPendingPortfolio()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, "Cliente Cartera");
        await SeedReceivableAsync(context, customer, 1001, 100m, ReceivableStatus.Vigente, balance: 100m);
        await SeedReceivableAsync(context, customer, 1002, 50m, ReceivableStatus.Pagada, balance: 0m);

        var pending = await CreateService(context).GetCreditReceivablesAsync(new CreditReceivablesFilterDto());

        var item = Assert.Single(pending.Items);
        Assert.Equal(1001, item.InvoiceNumber);
        Assert.Equal(100m, pending.TotalOpenUSD);
        Assert.Equal(1, pending.OpenCount);
        Assert.DoesNotContain(pending.Items, row => row.Status == "Pagada");
    }

    [Fact]
    public async Task GetPaidReceivablesAsync_ReturnsHistoricalRealizedAmountsAndSummary()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, "Ana Pérez", cedula: "V-12345678");
        var method = await SeedMethodAsync(context);
        var paid = await SeedReceivableAsync(context, customer, 287, 100m, ReceivableStatus.Pagada, balance: 0m);
        await SeedPaymentAsync(context, customer, paid, method, amountUsd: 40m, rate: 40m, reference: "REF-1", receivedAt: NowUtc.AddDays(-5));
        await SeedPaymentAsync(context, customer, paid, method, amountUsd: 60m, rate: 50m, reference: "REF-2", receivedAt: NowUtc.AddDays(-2));

        var result = await CreateService(context).GetPaidReceivablesAsync(new PaidReceivablesFilterDto());

        var item = Assert.Single(result.Items);
        Assert.Equal("Ana Pérez", item.CustomerName);
        Assert.Equal("V-12345678", item.CustomerDocument);
        Assert.Equal(100m, item.TotalPaidUsd);
        Assert.Equal(4600m, item.TotalPaidLocalHistorical);
        Assert.Equal(2, item.PaymentsCount);
        Assert.Equal("Pago Móvil", item.PaymentMethodsSummary);
        Assert.Equal(NowUtc.AddDays(-2).Date, item.PaidAt.Date);

        Assert.Equal(1, result.Summary.PaidDebtsCount);
        Assert.Equal(100m, result.Summary.TotalCollectedUsd);
        Assert.Equal(4600m, result.Summary.TotalCollectedLocalHistorical);
        Assert.Equal(2, result.Summary.TotalPaymentsCount);
    }

    [Fact]
    public async Task GetReceivablePaymentTimeline_KeepsFrozenRatesAndRunningBalance()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, "Cliente Parcial");
        var method = await SeedMethodAsync(context);
        var receivable = await SeedReceivableAsync(context, customer, 300, 100m, ReceivableStatus.Pagada, balance: 0m);
        await SeedPaymentAsync(context, customer, receivable, method, 40m, 40m, "REF-A", NowUtc.AddDays(-6));
        await SeedPaymentAsync(context, customer, receivable, method, 60m, 50m, "REF-B", NowUtc.AddDays(-1));

        var timeline = await CreateService(context).GetReceivablePaymentTimelineAsync(receivable.Id);

        Assert.Equal(2, timeline.Payments.Count);
        Assert.Equal(40m, timeline.Payments[0].ExchangeRateApplied);
        Assert.Equal(50m, timeline.Payments[1].ExchangeRateApplied);
        Assert.Equal(60m, timeline.Payments[0].RunningBalanceAfterUsd);
        Assert.Equal(0m, timeline.Payments[1].RunningBalanceAfterUsd);
        Assert.Equal(1, timeline.Payments[0].SequenceNumber);
        Assert.Equal(2, timeline.Payments[1].SequenceNumber);
        Assert.Equal("Aplicado", timeline.Payments[0].Status);
        Assert.Equal("Pago Móvil", timeline.Payments[0].PaymentMethodName);
        Assert.Equal("Ana", timeline.Payments[0].CashierName);
    }

    [Fact]
    public async Task GetPaidReceivablePayments_DoesNotDoubleCountFifoAllocations()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, "Cliente FIFO");
        var method = await SeedMethodAsync(context);
        var first = await SeedReceivableAsync(context, customer, 401, 40m, ReceivableStatus.Pagada, balance: 0m);
        var second = await SeedReceivableAsync(context, customer, 402, 60m, ReceivableStatus.Pagada, balance: 0m);

        var payment = new ReceivablePayment
        {
            CustomerId = customer.Id,
            AmountUSD = 100m,
            AmountBsS = 4000m,
            PaymentRate = 40m,
            PaymentMethodId = method.Id,
            ReferenceNumber = "FIFO-1",
            ReceivedAtUtc = NowUtc.AddDays(-1),
            ReceivedByUserId = customer.Id
        };
        context.ReceivablePayments.Add(payment);
        await context.SaveChangesAsync();
        context.CreditPaymentAllocations.AddRange(
            new CreditPaymentAllocation { ReceivablePaymentId = payment.Id, ReceivableId = first.Id, AmountUSD = 40m, AmountBsS = 1600m },
            new CreditPaymentAllocation { ReceivablePaymentId = payment.Id, ReceivableId = second.Id, AmountUSD = 60m, AmountBsS = 2400m });
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetPaidReceivablePaymentsAsync(new PaidReceivablesFilterDto());

        var item = Assert.Single(result.Items);
        Assert.Equal(2, item.Allocations.Count);
        Assert.Equal(100m, item.AmountCreditedUsd);
        Assert.Equal(1, result.Summary.TotalPaymentsCount);
        Assert.Equal(100m, result.Summary.TotalCollectedUsd);
        Assert.Equal(2, result.Summary.PaidDebtsCount);
    }

    [Fact]
    public async Task GetPaidReceivablesAsync_SearchMatchesPaymentReference()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, "Cliente Referencia");
        var method = await SeedMethodAsync(context);
        var receivable = await SeedReceivableAsync(context, customer, 500, 100m, ReceivableStatus.Pagada, balance: 0m);
        await SeedPaymentAsync(context, customer, receivable, method, 100m, 40m, "REF-UNICA", NowUtc.AddDays(-1));

        var result = await CreateService(context).GetPaidReceivablesAsync(new PaidReceivablesFilterDto
        {
            Search = "REF-UNICA"
        });

        Assert.Single(result.Items);
        Assert.Equal(receivable.Id, result.Items[0].ObligationId);
    }
    [Fact]
    public async Task VoidedPayments_AreExcludedFromTotalsAndKeptInTimelineTraceability()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, "Cliente Anulado");
        var method = await SeedMethodAsync(context);
        var receivable = await SeedReceivableAsync(context, customer, 600, 100m, ReceivableStatus.Pagada, balance: 0m);
        await SeedPaymentAsync(context, customer, receivable, method, 100m, 40m, "REF-VALIDA", NowUtc.AddDays(-2));

        var voided = new ReceivablePayment
        {
            CustomerId = customer.Id,
            AmountUSD = 20m,
            AmountBsS = 800m,
            PaymentRate = 40m,
            PaymentMethodId = method.Id,
            ReferenceNumber = "REF-ANULADA",
            ReceivedAtUtc = NowUtc.AddDays(-1),
            IsVoided = true,
            VoidedAtUtc = NowUtc.AddHours(-12),
            VoidReason = "Pago duplicado"
        };
        context.ReceivablePayments.Add(voided);
        await context.SaveChangesAsync();
        context.CreditPaymentAllocations.Add(new CreditPaymentAllocation
        {
            ReceivablePaymentId = voided.Id,
            ReceivableId = receivable.Id,
            AmountUSD = 20m,
            AmountBsS = 800m
        });
        await context.SaveChangesAsync();

        var paid = await CreateService(context).GetPaidReceivablesAsync(new PaidReceivablesFilterDto());
        var item = Assert.Single(paid.Items);
        Assert.Equal(100m, item.TotalPaidUsd);
        Assert.Equal(1, item.PaymentsCount);
        Assert.Equal(100m, paid.Summary.TotalCollectedUsd);

        var timeline = await CreateService(context).GetReceivablePaymentTimelineAsync(receivable.Id);
        Assert.Equal(2, timeline.Payments.Count);
        Assert.Equal("Anulado", timeline.Payments[1].Status);
        Assert.Equal("Pago duplicado", timeline.Payments[1].Notes);
        Assert.Equal(0m, timeline.Payments[1].RunningBalanceAfterUsd);
        Assert.Equal(0m, timeline.Payments[0].RunningBalanceAfterUsd - 0m);
    }


    private static CreditService CreateService(SalesDbContext context) =>
        new(context, new FixedRateProvider(50m));

    private static async Task<Customer> SeedCustomerAsync(
        SalesDbContext context,
        string name,
        string? cedula = null)
    {
        var customer = new Customer
        {
            CedulaOrRif = cedula ?? "V-" + Guid.NewGuid().ToString("N")[..8],
            Name = name,
            IsCreditEnabled = true,
            CreditLimitUSD = 1000m,
            PaymentTermDays = 30,
            CreditGraceDays = 0,
            CreditStatus = CreditStatus.Active,
            IsActive = true
        };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        return customer;
    }

    private static async Task<PaymentMethod> SeedMethodAsync(SalesDbContext context)
    {
        var method = new PaymentMethod
        {
            Name = "Pago Móvil",
            IsActive = true,
            IsCash = false,
            Currency = "Bs.S"
        };
        context.PaymentMethods.Add(method);
        await context.SaveChangesAsync();
        return method;
    }

    private static async Task<Receivable> SeedReceivableAsync(
        SalesDbContext context,
        Customer customer,
        int invoiceNumber,
        decimal originalUsd,
        ReceivableStatus status,
        decimal balance)
    {
        var sale = new Sale
        {
            CustomerId = customer.Id,
            CustomerName = customer.Name,
            CustomerCedula = customer.CedulaOrRif,
            Status = SaleStatus.Completed,
            DeliveryStatus = SaleDeliveryStatus.Delivered,
            PriceListType = "Retail",
            InvoiceNumber = invoiceNumber,
            Date = NowUtc.AddDays(-30),
            AppliedRate = 40m,
            Subtotal = originalUsd,
            TotalUSD = originalUsd,
            TotalBsS = Math.Round(originalUsd * 40m, 2, MidpointRounding.AwayFromZero)
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var receivable = new Receivable
        {
            SaleId = sale.Id,
            CustomerId = customer.Id,
            OriginalUSD = originalUsd,
            BalanceUSD = balance,
            OriginalBsS = Math.Round(originalUsd * 40m, 2, MidpointRounding.AwayFromZero),
            HistoricalRate = 40m,
            IssuedAtUtc = NowUtc.AddDays(-30),
            DueDateUtc = NowUtc.AddDays(-1),
            Status = status
        };
        context.Receivables.Add(receivable);
        await context.SaveChangesAsync();
        return receivable;
    }

    private static async Task SeedPaymentAsync(
        SalesDbContext context,
        Customer customer,
        Receivable receivable,
        PaymentMethod method,
        decimal amountUsd,
        decimal rate,
        string reference,
        DateTime receivedAt)
    {
        var cashier = new User
        {
            Cedula = "V-CASHIER",
            Name = "Ana",
            Username = "ana-" + Guid.NewGuid().ToString("N")[..6],
            FullName = "Ana",
            Role = UserRole.Admin,
            IsActive = true
        };
        context.Users.Add(cashier);
        await context.SaveChangesAsync();

        var payment = new ReceivablePayment
        {
            CustomerId = customer.Id,
            AmountUSD = amountUsd,
            AmountBsS = Math.Round(amountUsd * rate, 2, MidpointRounding.AwayFromZero),
            PaymentRate = rate,
            PaymentMethodId = method.Id,
            ReferenceNumber = reference,
            ReceivedAtUtc = receivedAt,
            ReceivedByUserId = cashier.Id
        };
        context.ReceivablePayments.Add(payment);
        await context.SaveChangesAsync();
        context.CreditPaymentAllocations.Add(new CreditPaymentAllocation
        {
            ReceivablePaymentId = payment.Id,
            ReceivableId = receivable.Id,
            AmountUSD = amountUsd,
            AmountBsS = Math.Round(amountUsd * rate, 2, MidpointRounding.AwayFromZero)
        });
        await context.SaveChangesAsync();
    }

    private sealed class FixedRateProvider : Core.Interfaces.ITodayExchangeRateProvider
    {
        private readonly decimal _rate;

        public FixedRateProvider(decimal rate) => _rate = rate;

        public Task<decimal> GetEffectiveTodayRateAsync(System.Threading.CancellationToken cancellationToken) =>
            Task.FromResult(_rate);
    }
}

