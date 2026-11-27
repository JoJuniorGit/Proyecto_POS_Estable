using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.202-H4 (S7): certifica la rama PostgreSQL de la cartera CxC — búsqueda, mora dinámica,
/// totales y paginación se resuelven en SQL antes de materializar— contra una base aislada real.
/// </summary>
[Collection(PostgresRealCollection.Name)]
public class PostgresCreditReceivablesQueryTests
{
    private static readonly DateTime NowUtc = DateTime.UtcNow;

    [Fact]
    public async Task GetCreditReceivablesAsync_PostgresPath_FiltersSearchOverdueAndPaginates()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(seedStandardSalesData: true);
        await using var context = database.CreateSalesDbContext();

        var alpha = await SeedCustomerAsync(context, "Cliente Alfa", "V-ALFA-0001", graceDays: 0);
        var beta = await SeedCustomerAsync(context, "Cliente Beta", "V-BETA-0001", graceDays: 5);

        await SeedReceivableAsync(context, alpha, 1001, 100m, ReceivableStatus.Vigente, dueInDays: 10);
        await SeedReceivableAsync(context, alpha, 1002, 40m, ReceivableStatus.Parcial, dueInDays: -3);
        await SeedReceivableAsync(context, alpha, 1003, 0.004m, ReceivableStatus.Vigente, dueInDays: 5);
        await SeedReceivableAsync(context, beta, 2001, 70m, ReceivableStatus.Vencida, dueInDays: -2);
        await SeedReceivableAsync(context, beta, 2002, 0m, ReceivableStatus.Anulada, dueInDays: -30);

        var service = new CreditService(context, new FixedRateProvider(50m));

        var all = await service.GetCreditReceivablesAsync(new CreditReceivablesFilterDto());
        Assert.Equal(5, all.Items.Count);
        Assert.Equal(5, all.TotalCount);
        Assert.Equal(210m, all.TotalOpenUSD);
        Assert.Equal(40m, all.TotalOverdueUSD);
        Assert.Equal(3, all.OpenCount);

        var overdueOnly = await service.GetCreditReceivablesAsync(new CreditReceivablesFilterDto
        {
            OverdueOnly = true
        });
        var overdueItem = Assert.Single(overdueOnly.Items);
        Assert.Equal(1002, overdueItem.InvoiceNumber);
        Assert.Equal(40m, overdueOnly.TotalOpenUSD);
        Assert.Equal(40m, overdueOnly.TotalOverdueUSD);
        Assert.Equal(1, overdueOnly.OpenCount);

        var byName = await service.GetCreditReceivablesAsync(new CreditReceivablesFilterDto { Search = "Alfa" });
        Assert.Equal(3, byName.Items.Count);
        Assert.All(byName.Items, item => Assert.Equal(alpha.Id, item.CustomerId));

        var byInvoice = await service.GetCreditReceivablesAsync(new CreditReceivablesFilterDto { Search = "1002" });
        var invoiceItem = Assert.Single(byInvoice.Items);
        Assert.Equal(1002, invoiceItem.InvoiceNumber);

        var byCustomerAndStatus = await service.GetCreditReceivablesAsync(new CreditReceivablesFilterDto
        {
            CustomerId = alpha.Id,
            Status = "pArCiAl"
        });
        var partial = Assert.Single(byCustomerAndStatus.Items);
        Assert.Equal(1002, partial.InvoiceNumber);

        var firstPage = await service.GetCreditReceivablesAsync(new CreditReceivablesFilterDto
        {
            Page = 1,
            PageSize = 2
        });
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(5, firstPage.TotalCount);
        Assert.Equal(1, firstPage.Page);
        Assert.Equal(2, firstPage.PageSize);
        Assert.True(firstPage.HasMore);
        Assert.Equal(210m, firstPage.TotalOpenUSD);

        var lastPage = await service.GetCreditReceivablesAsync(new CreditReceivablesFilterDto
        {
            Page = 3,
            PageSize = 2
        });
        var lastItem = Assert.Single(lastPage.Items);
        Assert.False(lastPage.HasMore);
        Assert.Equal(210m, lastPage.TotalOpenUSD);
        Assert.Equal(3, lastPage.OpenCount);
        Assert.NotNull(lastItem);
    }

    private static async Task<Customer> SeedCustomerAsync(
        SalesDbContext context,
        string name,
        string cedula,
        int graceDays)
    {
        var customer = new Customer
        {
            CedulaOrRif = cedula,
            Name = name,
            IsCreditEnabled = true,
            CreditLimitUSD = 1000m,
            PaymentTermDays = 30,
            CreditGraceDays = graceDays,
            CreditStatus = CreditStatus.Active,
            IsActive = true
        };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        return customer;
    }

    private static async Task SeedReceivableAsync(
        SalesDbContext context,
        Customer customer,
        int invoiceNumber,
        decimal balanceUsd,
        ReceivableStatus status,
        int dueInDays)
    {
        var rate = 50m;
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
            AppliedRate = rate,
            Subtotal = balanceUsd,
            TotalUSD = balanceUsd,
            TotalBsS = Math.Round(balanceUsd * rate, 2, MidpointRounding.AwayFromZero)
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        context.Receivables.Add(new Receivable
        {
            SaleId = sale.Id,
            CustomerId = customer.Id,
            OriginalUSD = balanceUsd,
            BalanceUSD = balanceUsd,
            OriginalBsS = Math.Round(balanceUsd * rate, 2, MidpointRounding.AwayFromZero),
            HistoricalRate = rate,
            IssuedAtUtc = NowUtc.AddDays(-30),
            DueDateUtc = NowUtc.AddDays(dueInDays),
            Status = status
        });
        await context.SaveChangesAsync();
    }

    private sealed class FixedRateProvider : ITodayExchangeRateProvider
    {
        private readonly decimal _rate;

        public FixedRateProvider(decimal rate)
        {
            _rate = rate;
        }

        public Task<decimal> GetEffectiveTodayRateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_rate);
    }
}
