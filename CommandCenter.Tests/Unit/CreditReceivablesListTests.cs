using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T4-prep (Paso 11, S3): cartera global de cuentas por cobrar para el dashboard administrativo.
/// Cubre filtros por cliente/estado/mora/búsqueda, proyección Bs.S a la tasa del día y los
/// totales de cartera abierta/vencida. La mora se calcula con la gracia del cliente, no con el
/// estado persistido, y el polvo decimal ≤ 0.005 USD no computa deuda.
/// </summary>
public class CreditReceivablesListTests
{
    private static readonly DateTime NowUtc = DateTime.UtcNow;

    [Fact]
    public async Task GetCreditReceivablesAsync_ReturnsCustomerInvoiceBalancesAndTodayProjection()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, "Cliente Alfa", graceDays: 0);
        await SeedReceivableAsync(context, customer, invoiceNumber: 1001, balanceUsd: 100m, status: ReceivableStatus.Vigente, dueInDays: 10);

        var list = await CreateService(context, rate: 50m).GetCreditReceivablesAsync(new CreditReceivablesFilterDto());

        var item = Assert.Single(list.Items);
        Assert.Equal(customer.Id, item.CustomerId);
        Assert.Equal("Cliente Alfa", item.CustomerName);
        Assert.Equal(customer.CedulaOrRif, item.CustomerCedulaOrRif);
        Assert.Equal(1001, item.InvoiceNumber);
        Assert.Equal(100m, item.OriginalUSD);
        Assert.Equal(100m, item.BalanceUSD);
        Assert.Equal(5000m, item.OriginalBsS);
        Assert.Equal(50m, item.HistoricalRate);
        Assert.Equal(5000m, item.BalanceTodayBsS);
        Assert.Equal("Vigente", item.Status);
        Assert.False(item.IsOverdue);
        Assert.Equal(50m, list.TodayRate);
        Assert.Equal(100m, list.TotalOpenUSD);
        Assert.Equal(0m, list.TotalOverdueUSD);
        Assert.Equal(1, list.OpenCount);
    }

    [Fact]
    public async Task GetCreditReceivablesAsync_FiltersByCustomerAndStatusCaseInsensitively()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var first = await SeedCustomerAsync(context, "Cliente Uno");
        var second = await SeedCustomerAsync(context, "Cliente Dos");
        await SeedReceivableAsync(context, first, 1001, 100m, ReceivableStatus.Vigente, dueInDays: 10);
        await SeedReceivableAsync(context, first, 1002, 40m, ReceivableStatus.Parcial, dueInDays: 5);
        await SeedReceivableAsync(context, second, 2001, 70m, ReceivableStatus.Parcial, dueInDays: 8);

        var list = await CreateService(context).GetCreditReceivablesAsync(new CreditReceivablesFilterDto
        {
            CustomerId = first.Id,
            Status = "pArCiAl"
        });

        var item = Assert.Single(list.Items);
        Assert.Equal(first.Id, item.CustomerId);
        Assert.Equal(1002, item.InvoiceNumber);
        Assert.Equal(40m, list.TotalOpenUSD);
        Assert.Equal(1, list.OpenCount);
    }

    [Fact]
    public async Task GetCreditReceivablesAsync_OverdueOnly_UsesEachCustomerGraceDays()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var strict = await SeedCustomerAsync(context, "Cliente Estricto", graceDays: 0);
        var tolerant = await SeedCustomerAsync(context, "Cliente Tolerante", graceDays: 5);
        await SeedReceivableAsync(context, strict, 1001, 40m, ReceivableStatus.Parcial, dueInDays: -3);
        await SeedReceivableAsync(context, tolerant, 2001, 70m, ReceivableStatus.Vencida, dueInDays: -2);

        var all = await CreateService(context).GetCreditReceivablesAsync(new CreditReceivablesFilterDto());
        Assert.True(all.Items.Single(item => item.CustomerId == strict.Id).IsOverdue);
        Assert.False(all.Items.Single(item => item.CustomerId == tolerant.Id).IsOverdue);

        var overdueOnly = await CreateService(context).GetCreditReceivablesAsync(new CreditReceivablesFilterDto
        {
            OverdueOnly = true
        });

        var item = Assert.Single(overdueOnly.Items);
        Assert.Equal(strict.Id, item.CustomerId);
        Assert.Equal(40m, overdueOnly.TotalOpenUSD);
        Assert.Equal(40m, overdueOnly.TotalOverdueUSD);
        Assert.Equal(1, overdueOnly.OpenCount);
    }

    [Theory]
    [InlineData("Alfa", 2)]
    [InlineData("V-ALFA", 2)]
    [InlineData("1001", 1)]
    public async Task GetCreditReceivablesAsync_SearchMatchesNameCedulaAndInvoice(string search, int expectedCount)
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, "Cliente Alfa", cedula: "V-ALFA-0001");
        await SeedReceivableAsync(context, customer, 1001, 100m, ReceivableStatus.Vigente, dueInDays: 10);
        await SeedReceivableAsync(context, customer, 1002, 50m, ReceivableStatus.Vigente, dueInDays: 12);

        var list = await CreateService(context).GetCreditReceivablesAsync(new CreditReceivablesFilterDto
        {
            Search = search
        });

        Assert.Equal(expectedCount, list.Items.Count);
        if (expectedCount == 1)
        {
            Assert.Equal(1001, list.Items[0].InvoiceNumber);
        }
    }

    [Fact]
    public async Task GetCreditReceivablesAsync_ComputesTotalsOverFilteredOpenPortfolio()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var alpha = await SeedCustomerAsync(context, "Cliente Alfa", graceDays: 0);
        var beta = await SeedCustomerAsync(context, "Cliente Beta", graceDays: 5);

        await SeedReceivableAsync(context, alpha, 1001, 100m, ReceivableStatus.Vigente, dueInDays: 10);
        await SeedReceivableAsync(context, alpha, 1002, 40m, ReceivableStatus.Parcial, dueInDays: -3);
        await SeedReceivableAsync(context, alpha, 1003, 0m, ReceivableStatus.Pagada, dueInDays: -20);
        await SeedReceivableAsync(context, alpha, 1004, 0.004m, ReceivableStatus.Vigente, dueInDays: 5);
        await SeedReceivableAsync(context, beta, 2001, 70m, ReceivableStatus.Vencida, dueInDays: -2);
        await SeedReceivableAsync(context, beta, 2002, 0m, ReceivableStatus.Anulada, dueInDays: -30);

        var list = await CreateService(context).GetCreditReceivablesAsync(new CreditReceivablesFilterDto());

        Assert.Equal(6, list.Items.Count);
        Assert.Equal(210m, list.TotalOpenUSD);
        Assert.Equal(40m, list.TotalOverdueUSD);
        Assert.Equal(3, list.OpenCount);
    }

    [Fact]
    public async Task GetCreditReceivablesAsync_InvalidStatus_ThrowsArgumentException()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateService(context).GetCreditReceivablesAsync(new CreditReceivablesFilterDto { Status = "Bogus" }));

        Assert.Contains("Estado", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Vigente", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCreditReceivablesAsync_WithoutRateProvider_ReportsZeroTodayProjection()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, "Cliente Alfa");
        await SeedReceivableAsync(context, customer, 1001, 100m, ReceivableStatus.Vigente, dueInDays: 10);

        var list = await new CreditService(context).GetCreditReceivablesAsync(new CreditReceivablesFilterDto());

        var item = Assert.Single(list.Items);
        Assert.Equal(0m, list.TodayRate);
        Assert.Equal(0m, item.BalanceTodayBsS);
    }

    [Fact]
    public async Task GetCreditReceivablesAsync_DualUsdCop_ProjectsWithManualRateAndNeverReadsBcv()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, "Cliente Dual COP");
        await SeedReceivableAsync(context, customer, 1001, 100m, ReceivableStatus.Vigente, dueInDays: 10);

        var bcv = CreateRateProviderMock(50m);
        var service = new CreditService(
            context,
            bcv.Object,
            logger: null,
            CreateProfileMock(MonetaryMode.Dual, "USD", "COP").Object,
            CreateManualRateMock("COP", 4000m).Object);

        var list = await service.GetCreditReceivablesAsync(new CreditReceivablesFilterDto());

        var item = Assert.Single(list.Items);
        Assert.Equal(4000m, list.TodayRate);
        Assert.Equal(400000m, item.BalanceTodayBsS);
        bcv.Verify(provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCreditReceivablesAsync_SingleProfile_ProjectsIdentityWithoutRateServices()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, "Cliente Single");
        await SeedReceivableAsync(context, customer, 1001, 100m, ReceivableStatus.Vigente, dueInDays: 10);

        var bcv = CreateRateProviderMock(50m);
        var manual = CreateManualRateMock("COP", 4000m);
        var service = new CreditService(
            context,
            bcv.Object,
            logger: null,
            CreateProfileMock(MonetaryMode.Single, "USD", null).Object,
            manual.Object);

        var list = await service.GetCreditReceivablesAsync(new CreditReceivablesFilterDto());

        var item = Assert.Single(list.Items);
        Assert.Equal(1m, list.TodayRate);
        Assert.Equal(100m, item.BalanceTodayBsS);
        bcv.Verify(provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()), Times.Never);
        manual.Verify(
            rateService => rateService.GetRateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateOnly?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetCreditReceivablesAsync_DualUsdCopWithoutManualService_FailsClosedWithoutBcvFallback()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, "Cliente Dual COP");
        await SeedReceivableAsync(context, customer, 1001, 100m, ReceivableStatus.Vigente, dueInDays: 10);

        var bcv = CreateRateProviderMock(50m);
        var service = new CreditService(
            context,
            bcv.Object,
            logger: null,
            CreateProfileMock(MonetaryMode.Dual, "USD", "COP").Object,
            currencyRateService: null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetCreditReceivablesAsync(new CreditReceivablesFilterDto()));

        Assert.Contains("USD/COP", exception.Message);
        bcv.Verify(provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCreditReceivablesAsync_UnsupportedDualSecondary_FailsClosedWithoutBcvFallback()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, "Cliente Dual GBP");
        await SeedReceivableAsync(context, customer, 1001, 100m, ReceivableStatus.Vigente, dueInDays: 10);

        var bcv = CreateRateProviderMock(50m);
        var service = new CreditService(
            context,
            bcv.Object,
            logger: null,
            CreateProfileMock(MonetaryMode.Dual, "USD", "GBP").Object,
            CreateManualRateMock("GBP", 0.8m).Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetCreditReceivablesAsync(new CreditReceivablesFilterDto()));

        Assert.Contains("USD/GBP", exception.Message);
        bcv.Verify(provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCreditStatementAsync_DualUsdCop_ProjectsTodayRateFromManualPair()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, "Cliente Dual COP");
        await SeedReceivableAsync(context, customer, 1001, 100m, ReceivableStatus.Vigente, dueInDays: 10);

        var bcv = CreateRateProviderMock(50m);
        var service = new CreditService(
            context,
            bcv.Object,
            logger: null,
            CreateProfileMock(MonetaryMode.Dual, "USD", "COP").Object,
            CreateManualRateMock("COP", 4000m).Object);

        var statement = await service.GetCreditStatementAsync(customer.Id);

        Assert.Equal(4000m, statement.TodayRate);
        Assert.Equal(400000m, statement.CurrentDebtTodayBsS);
        Assert.Equal(400000m, Assert.Single(statement.Receivables).BalanceTodayBsS);
        bcv.Verify(provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static CreditService CreateService(SalesDbContext context, decimal rate = 50m) =>
        new(context, new FixedRateProvider(rate));

    private static Mock<ITodayExchangeRateProvider> CreateRateProviderMock(decimal rate)
    {
        var mock = new Mock<ITodayExchangeRateProvider>();
        mock.Setup(provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(rate);
        return mock;
    }

    private static Mock<IMonetaryProfileService> CreateProfileMock(
        MonetaryMode mode,
        string primaryCode = "USD",
        string? secondaryCode = "VES")
    {
        var mock = new Mock<IMonetaryProfileService>();
        mock.Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = mode,
                PrimaryCurrencyCode = primaryCode,
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = secondaryCode,
                SecondaryCurrencySymbol = secondaryCode,
                IsConfigured = true
            });
        return mock;
    }

    private static Mock<ICurrencyRateService> CreateManualRateMock(string secondaryCode, decimal rate)
    {
        var mock = new Mock<ICurrencyRateService>();
        mock.Setup(service => service.GetRateAsync("USD", secondaryCode, It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrencyRateQuote(
                rate,
                CurrencyRateSource.Manual,
                DateOnly.FromDateTime(DateTime.UtcNow),
                "USD",
                secondaryCode));
        return mock;
    }

    private static async Task<Customer> SeedCustomerAsync(
        SalesDbContext context,
        string name,
        string? cedula = null,
        int graceDays = 0)
    {
        var customer = new Customer
        {
            CedulaOrRif = cedula ?? "V-" + Guid.NewGuid().ToString("N")[..8],
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
