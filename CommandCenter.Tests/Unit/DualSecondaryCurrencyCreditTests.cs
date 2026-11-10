using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;
using SalesService = Sales.Module.Services.SalesService;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T3c3c2 (S7): credit payments (CxC) in Dual USD/{COP,MXN,EUR}. The abono rate comes from
/// the exact manual daily pair (server-side, no client input), the physical income snapshots the
/// method currency, and the profile remains the authority over which methods may be collected.
/// Missing quotes fail closed with no payment and no cash row.
/// </summary>
public class DualSecondaryCurrencyCreditTests
{
    private const decimal CopRate = 4000m;
    private const string SupportedCheckoutProfileMessage =
        "El perfil monetario actual no está habilitado para ventas: esta fase admite Dual USD/VES, USD/COP, USD/MXN o USD/EUR, o Single con una moneda de 2 decimales.";

    private static (SalesService Service, Mock<IInventoryService> Inventory, Mock<ICashDrawerService> CashDrawer) CreateHarness(
        SalesDbContext context,
        IMonetaryProfileService? monetaryProfileService = null,
        ICurrencyRateService? currencyRateService = null)
    {
        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(60m);

        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer
            .Setup(d => d.GetOrCreateActiveSessionAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var service = new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            cashDrawer.Object,
            new Mock<ISystemSettingsService>().Object,
            creditService: new CreditService(context),
            monetaryProfileService: monetaryProfileService,
            currencyRateService: currencyRateService);

        return (service, inventory, cashDrawer);
    }

    private static Mock<IMonetaryProfileService> CreateDualProfileMock(string secondaryCode)
    {
        var mock = new Mock<IMonetaryProfileService>();
        mock
            .Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = "USD",
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
        mock
            .Setup(r => r.GetRateAsync("USD", secondaryCode, It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrencyRateQuote(rate, CurrencyRateSource.Manual, Core.Helpers.TimeZoneHelper.GetVenezuelaDate(), "USD", secondaryCode));
        return mock;
    }

    private static async Task<Customer> SeedCustomerAsync(SalesDbContext context)
    {
        var customer = new Customer
        {
            CedulaOrRif = "V-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Cliente Abonos Dual",
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

    private static async Task<Receivable> SeedReceivableAsync(
        SalesDbContext context,
        int customerId,
        decimal balanceUsd = 100m,
        decimal historicalRate = 50m,
        int invoiceNumber = 9701)
    {
        var sale = new Sale
        {
            CustomerId = customerId,
            Status = SaleStatus.Completed,
            InvoiceNumber = invoiceNumber,
            Date = DateTime.UtcNow,
            AppliedRate = historicalRate,
            TotalUSD = balanceUsd,
            TotalBsS = Math.Round(balanceUsd * historicalRate, 2, MidpointRounding.AwayFromZero)
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var receivable = new Receivable
        {
            SaleId = sale.Id,
            CustomerId = customerId,
            OriginalUSD = balanceUsd,
            BalanceUSD = balanceUsd,
            OriginalBsS = Math.Round(balanceUsd * historicalRate, 2, MidpointRounding.AwayFromZero),
            HistoricalRate = historicalRate,
            IssuedAtUtc = DateTime.UtcNow,
            DueDateUtc = DateTime.UtcNow.AddDays(10),
            Status = ReceivableStatus.Vigente
        };
        context.Receivables.Add(receivable);
        await context.SaveChangesAsync();
        return receivable;
    }

    private static async Task SeedPaymentMethodsAsync(SalesDbContext context)
    {
        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true, IsActive = true, DisplayOrder = 1 },
            new PaymentMethod { Id = 2, Name = "Punto de Venta", IsCash = false, IsActive = true, DisplayOrder = 2 },
            new PaymentMethod { Id = 3, Name = "Efectivo COP", IsCash = true, IsActive = true, DisplayOrder = 3, Currency = "COP" },
            new PaymentMethod { Id = 4, Name = "Punto de Venta Bs.S", IsCash = false, IsActive = true, DisplayOrder = 4, Currency = "Bs.S" });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task RegisterCreditPayment_DualUsdCop_CashPayment_UsesManualRateAndCopDrawerCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var receivable = await SeedReceivableAsync(context, customer.Id, balanceUsd: 100m);
        await SeedPaymentMethodsAsync(context);

        var profile = CreateDualProfileMock("COP");
        var (service, inventory, cashDrawer) = CreateHarness(
            context,
            profile.Object,
            CreateManualRateMock("COP", CopRate).Object);

        var result = await service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 100m,
            PaymentMethodId = 3,
            ReferenceNumber = "COP-REF-1"
        });

        Assert.Equal(CopRate, result.PaymentRate);
        Assert.Equal(400000m, result.AmountBsS);
        Assert.Equal(100m, result.AmountUSD);

        var payment = await context.ReceivablePayments.AsNoTracking().SingleAsync();
        Assert.Equal(CopRate, payment.PaymentRate);
        Assert.Equal(400000m, payment.AmountBsS);
        Assert.NotNull(payment.CashTransactionId);

        var cash = await context.CashTransactions.AsNoTracking().SingleAsync();
        Assert.Equal(CashTransactionType.Income, cash.Type);
        Assert.Equal(CashTransactionSource.CreditPayment, cash.Source);
        Assert.Equal("COP", cash.CurrencyCode);
        Assert.Equal(400000m, cash.AmountLocal);

        Assert.Equal(0m, (await context.Receivables.AsNoTracking().SingleAsync(r => r.Id == receivable.Id)).BalanceUSD);
        cashDrawer.Verify(d => d.GetOrCreateActiveSessionAsync(CopRate, It.IsAny<CancellationToken>()), Times.Once);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterCreditPayment_DualUsdCop_RejectsLocalMethodOutsideTheProfile()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var receivable = await SeedReceivableAsync(context, customer.Id, balanceUsd: 100m);
        await SeedPaymentMethodsAsync(context);

        var (service, _, _) = CreateHarness(
            context,
            CreateDualProfileMock("COP").Object,
            CreateManualRateMock("COP", CopRate).Object);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RegisterCreditPaymentAsync(new CreditPaymentRequest
            {
                CustomerId = customer.Id,
                AmountUSD = 100m,
                PaymentMethodId = 4
            }));

        Assert.Equal(
            "El método de pago 'Punto de Venta Bs.S' usa la moneda VES, pero el perfil actual solo admite USD y COP.",
            exception.Message);

        Assert.False(await context.ReceivablePayments.AsNoTracking().AnyAsync());
        Assert.False(await context.CashTransactions.AsNoTracking().AnyAsync());
        Assert.Equal(100m, (await context.Receivables.AsNoTracking().SingleAsync(r => r.Id == receivable.Id)).BalanceUSD);
    }

    [Fact]
    public async Task RegisterCreditPayment_DualUsdCop_WithoutManualQuote_FailsClosedWithoutPaymentOrCash()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var receivable = await SeedReceivableAsync(context, customer.Id, balanceUsd: 100m);
        await SeedPaymentMethodsAsync(context);

        var rateService = new Mock<ICurrencyRateService>();
        rateService
            .Setup(r => r.GetRateAsync("USD", "COP", It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No hay tasa manual configurada para USD->COP en la fecha 2026-10-28."));

        var (service, inventory, _) = CreateHarness(
            context,
            CreateDualProfileMock("COP").Object,
            rateService.Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RegisterCreditPaymentAsync(new CreditPaymentRequest
            {
                CustomerId = customer.Id,
                AmountUSD = 100m,
                PaymentMethodId = 3
            }));

        Assert.Contains("No hay tasa manual configurada", exception.Message);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);

        Assert.False(await context.ReceivablePayments.AsNoTracking().AnyAsync());
        Assert.False(await context.CashTransactions.AsNoTracking().AnyAsync());
        Assert.Equal(100m, (await context.Receivables.AsNoTracking().SingleAsync(r => r.Id == receivable.Id)).BalanceUSD);
    }

    [Fact]
    public async Task RegisterCreditPayment_DualUsdCop_ProfileWithoutManualRateService_FailsClosed()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        await SeedReceivableAsync(context, customer.Id, balanceUsd: 100m);
        await SeedPaymentMethodsAsync(context);

        var (service, inventory, _) = CreateHarness(context, CreateDualProfileMock("COP").Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RegisterCreditPaymentAsync(new CreditPaymentRequest
            {
                CustomerId = customer.Id,
                AmountUSD = 100m,
                PaymentMethodId = 3
            }));

        Assert.Contains("No hay servicio de tasas manuales configurado para el par USD/COP", exception.Message);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(await context.ReceivablePayments.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task RegisterCreditPayment_UnsupportedDualProfile_RejectsWithUpdatedMessage()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        await SeedReceivableAsync(context, customer.Id, balanceUsd: 100m);
        await SeedPaymentMethodsAsync(context);

        var (service, _, _) = CreateHarness(
            context,
            CreateDualProfileMock("GBP").Object,
            CreateManualRateMock("GBP", 0.8m).Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RegisterCreditPaymentAsync(new CreditPaymentRequest
            {
                CustomerId = customer.Id,
                AmountUSD = 100m,
                PaymentMethodId = 3
            }));

        Assert.Equal(SupportedCheckoutProfileMessage, exception.Message);
        Assert.False(await context.ReceivablePayments.AsNoTracking().AnyAsync());
    }
}
