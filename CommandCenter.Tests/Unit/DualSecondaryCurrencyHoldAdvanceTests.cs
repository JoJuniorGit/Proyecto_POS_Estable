using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Helpers;
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
using ICashDrawerService = Sales.Module.Interfaces.ICashDrawerService;
using CashDrawerStatus = Sales.Module.Entities.CashDrawerStatus;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T3c3c2 (S7): hold orders, abonos and cash advances under Dual USD/{COP,MXN,EUR}. The
/// manual daily pair is honored (source "Manual"), fractional local cash is allowed for non-VES
/// secondaries, and Single stays explicitly rejected for these deferred operations.
/// </summary>
public class DualSecondaryCurrencyHoldAdvanceTests
{
    private const string SingleOperationPendingMessage =
        "La operación con moneda única se habilitará en la siguiente fase.";

    private const decimal CopRate = 4000m;

    private static SalesService CreateService(
        SalesDbContext context,
        IMonetaryProfileService? monetaryProfileService = null,
        ICashDrawerService? cashDrawerService = null,
        ICurrencyRateService? currencyRateService = null)
    {
        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(50m);
        inventory
            .Setup(i => i.GetCashAdvanceProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaleProductInfoDto { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        if (cashDrawerService is null)
        {
            var cashDrawer = new Mock<ICashDrawerService>();
            cashDrawer
                .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });
            cashDrawerService = cashDrawer.Object;
        }

        return new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            cashDrawerService,
            new Mock<ISystemSettingsService>().Object,
            monetaryProfileService: monetaryProfileService,
            currencyRateService: currencyRateService);
    }

    private static Mock<IMonetaryProfileService> CreateProfileMock(
        MonetaryMode mode,
        string primaryCode,
        string? secondaryCode)
    {
        var mock = new Mock<IMonetaryProfileService>();
        mock
            .Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = mode,
                PrimaryCurrencyCode = primaryCode,
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = secondaryCode,
                SecondaryCurrencySymbol = secondaryCode is null ? null : "S",
                IsConfigured = true
            });
        return mock;
    }

    private static Mock<ICurrencyRateService> CreateManualRateMock(decimal rate)
    {
        var mock = new Mock<ICurrencyRateService>();
        mock
            .Setup(r => r.GetRateAsync("USD", "COP", It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrencyRateQuote(rate, CurrencyRateSource.Manual, TimeZoneHelper.GetVenezuelaDate(), "USD", "COP"));
        return mock;
    }

    private static async Task<Sale> SeedPendingSaleAsync(SalesDbContext context, decimal totalUsd = 10m)
    {
        var sale = new Sale
        {
            TotalUSD = totalUsd,
            Subtotal = totalUsd,
            AppliedRate = CopRate,
            TotalBsS = totalUsd * CopRate,
            SubtotalBsS = totalUsd * CopRate,
            Status = SaleStatus.Pending
        };

        context.Sales.Add(sale);
        await context.SaveChangesAsync();
        return sale;
    }

    private static async Task<Customer> SeedRealCustomerAsync(SalesDbContext context)
    {
        var customer = new Customer
        {
            Id = 2,
            Name = "Cliente Real",
            CedulaOrRif = "V-12345678",
            IsDefault = false,
            IsActive = true
        };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        return customer;
    }

    private static async Task SeedDefaultCustomerAsync(SalesDbContext context)
    {
        context.Customers.Add(new Customer
        {
            Id = 1,
            Name = "Consumidor Final",
            CedulaOrRif = "V-00000000",
            IsDefault = true,
            IsActive = true
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task HoldSale_DualUsdCop_FullInitialPaymentWithFractionalCopCash_CompletesWithManualSource()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedRealCustomerAsync(context);
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 41,
            Name = "Efectivo COP",
            IsCash = true,
            IsActive = true,
            Currency = "COP"
        });
        await context.SaveChangesAsync();

        var service = CreateService(
            context,
            CreateProfileMock(MonetaryMode.Dual, "USD", "COP").Object,
            currencyRateService: CreateManualRateMock(CopRate).Object);

        var sale = await SeedPendingSaleAsync(context);

        await service.HoldSaleAsync(sale.Id, new HoldSaleRequestDto
        {
            CustomerId = customer.Id,
            ExchangeRate = CopRate,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = 41,
                AmountUSD = 10m,
                AmountBsS = 40000.50m,
                ExchangeRate = CopRate
            }
        });

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal("Manual", saved.AppliedRateSource);
        Assert.Equal("USD", saved.PrimaryCurrencyCode);
        Assert.Equal("COP", saved.SecondaryCurrencyCode);
        Assert.Equal("Dual", saved.MonetaryMode);
        Assert.Equal(10m, saved.PrimaryAmount);
        Assert.Equal(40000m, saved.SecondaryAmount);
        Assert.Equal(40000m, saved.TotalBsS);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);
        Assert.Equal("COP", payment.CurrencyCode);
        Assert.Equal(40000.50m, payment.AmountBsS);
        Assert.Equal(40000.50m, payment.OriginalAmount);

        var cash = await context.CashTransactions.AsNoTracking()
            .SingleAsync(t => t.SaleId == sale.Id
                && t.Type == CashTransactionType.Income
                && t.Source == CashTransactionSource.SalePayment);
        Assert.Equal("COP", cash.CurrencyCode);
        Assert.Equal(40000.50m, cash.AmountLocal);
    }

    [Fact]
    public async Task HoldSale_DualUsdCop_FractionalManualRate_CalculatesWithEightDecimalsAndSnapshotsFour()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedRealCustomerAsync(context);
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 46,
            Name = "Efectivo COP",
            IsCash = true,
            IsActive = true,
            Currency = "COP"
        });
        await context.SaveChangesAsync();

        const decimal fractionalRate = 4000.123456m;
        var service = CreateService(
            context,
            CreateProfileMock(MonetaryMode.Dual, "USD", "COP").Object,
            currencyRateService: CreateManualRateMock(fractionalRate).Object);

        var sale = await SeedPendingSaleAsync(context);
        await service.HoldSaleAsync(sale.Id, new HoldSaleRequestDto
        {
            CustomerId = customer.Id,
            ExchangeRate = fractionalRate,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = 46,
                AmountUSD = 10m,
                AmountBsS = 40001.23m,
                ExchangeRate = fractionalRate
            }
        });

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(4000.1235m, saved.AppliedRate);
        Assert.Equal(40001.23m, saved.TotalBsS);
        Assert.Equal(40001.23m, saved.SecondaryAmount);
    }

    [Fact]
    public async Task AddPaymentToHoldSale_ManualPairWithoutCalculationRate_FailsClosed()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedRealCustomerAsync(context);
        var profile = CreateProfileMock(MonetaryMode.Dual, "USD", "COP");
        var service = CreateService(
            context,
            profile.Object,
            currencyRateService: CreateManualRateMock(CopRate).Object);

        var sale = await SeedPendingSaleAsync(context);
        await service.HoldSaleAsync(sale.Id, new HoldSaleRequestDto
        {
            CustomerId = customer.Id,
            ExchangeRate = CopRate
        });

        sale.ClaimedByUserId = 7;
        sale.ClaimedByUserName = "Cajero COP";
        sale.ClaimAction = SaleClaimAction.Editing;
        sale.ClaimedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddPaymentToHoldSaleAsync(sale.Id, new AddPaymentRequestDto
            {
                PaymentMethodId = 1,
                AmountUSD = 5m,
                ExchangeRate = 0m
            }, actingUserId: 7));

        Assert.Contains("8 decimales", exception.Message, StringComparison.Ordinal);
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync());
    }
    [Fact]
    public async Task AddPaymentToHoldSale_DualUsdCop_FractionalCashAbono_UsesCopCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedRealCustomerAsync(context);
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 45,
            Name = "Efectivo COP",
            IsCash = true,
            IsActive = true,
            Currency = "COP"
        });
        await context.SaveChangesAsync();

        var profile = CreateProfileMock(MonetaryMode.Dual, "USD", "COP");
        var cashDrawer = new CashDrawerService(context, profile.Object);
        var session = await cashDrawer.OpenSessionAsync(0m, CopRate);
        var service = CreateService(
            context,
            profile.Object,
            cashDrawer,
            CreateManualRateMock(CopRate).Object);

        var sale = await SeedPendingSaleAsync(context);
        await service.HoldSaleAsync(sale.Id, new HoldSaleRequestDto
        {
            CustomerId = customer.Id,
            ExchangeRate = CopRate
        });

        // The hold must be claimed before an abono can be added.
        sale.ClaimedByUserId = 7;
        sale.ClaimedByUserName = "Cajero COP";
        sale.ClaimAction = SaleClaimAction.Editing;
        sale.ClaimedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync();

        var dto = await service.AddPaymentToHoldSaleAsync(sale.Id, new AddPaymentRequestDto
        {
            PaymentMethodId = 45,
            AmountUSD = 5m,
            AmountBsS = 20000.50m,
            ExchangeRate = CopRate
        }, actingUserId: 7);

        Assert.Equal(5m, dto.TotalPaidUSD);
        Assert.Equal(5m, dto.RemainingBalanceUSD);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);
        Assert.Equal("COP", payment.CurrencyCode);
        Assert.Equal(20000.50m, payment.AmountBsS);
        Assert.Equal(20000.50m, payment.OriginalAmount);

        var cash = await context.CashTransactions.AsNoTracking()
            .SingleAsync(t => t.SaleId == sale.Id
                && t.Type == CashTransactionType.Income
                && t.Source == CashTransactionSource.SalePayment);
        Assert.Equal("COP", cash.CurrencyCode);
        Assert.Equal(20000.50m, cash.AmountLocal);

        Assert.Equal(20000.50m, await cashDrawer.GetCurrentBalanceLocalAsync(session.Id));
    }

    [Fact]
    public async Task AddPaymentToHoldSale_SingleProfile_RejectsWithPendingOperationMessage()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 42,
            Name = "Punto de Venta COP",
            IsCash = false,
            IsActive = true,
            Currency = "COP"
        });
        var sale = await SeedPendingSaleAsync(context);
        sale.Status = SaleStatus.OnHold;
        await context.SaveChangesAsync();

        var service = CreateService(context, CreateProfileMock(MonetaryMode.Single, "COP", null).Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddPaymentToHoldSaleAsync(sale.Id, new AddPaymentRequestDto
            {
                PaymentMethodId = 42,
                AmountUSD = 1m,
                AmountBsS = 1m,
                ExchangeRate = 1m
            }));

        Assert.Equal(SingleOperationPendingMessage, exception.Message);
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync(p => p.SaleId == sale.Id));
        Assert.False(await context.CashTransactions.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task CreateCashAdvanceSale_DualUsdCop_WithCopNonCashMethod_CompletesWithManualSource()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedDefaultCustomerAsync(context);
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 43,
            Name = "Transferencia COP",
            IsCash = false,
            IsActive = true,
            Currency = "COP"
        });
        await context.SaveChangesAsync();

        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(50m);
        inventory
            .Setup(i => i.GetCashAdvanceProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaleProductInfoDto { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        var service = new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            new Mock<ICashDrawerService>().Object,
            new Mock<ISystemSettingsService>().Object,
            monetaryProfileService: CreateProfileMock(MonetaryMode.Dual, "USD", "COP").Object,
            currencyRateService: CreateManualRateMock(CopRate).Object);

        var dto = await service.CreateCashAdvanceSaleAsync(
            requestedAmountLocal: 40000m,
            commissionAmountLocal: 2000m,
            paymentMethodId: 43,
            paymentMethodName: "Transferencia COP",
            isTransfer: true,
            exchangeRate: CopRate,
            cashierId: 7);

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == dto.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal("Manual", saved.AppliedRateSource);
        Assert.Equal("COP", saved.SecondaryCurrencyCode);
        Assert.Equal("USD", saved.PrimaryCurrencyCode);
        Assert.Equal(42000m, saved.SecondaryAmount);
        Assert.Equal(42000m, saved.TotalBsS);
        Assert.Equal(10.5m, saved.PrimaryAmount);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == dto.Id);
        Assert.Equal("COP", payment.CurrencyCode);
        Assert.Equal(42000m, payment.OriginalAmount);
        Assert.Equal(CopRate, payment.ExchangeRate);

        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateCashAdvanceSale_UnsupportedDualSecondary_RejectsBeforeCreatingTheSale()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedDefaultCustomerAsync(context);
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 44,
            Name = "Transferencia GBP",
            IsCash = false,
            IsActive = true,
            Currency = "GBP"
        });
        await context.SaveChangesAsync();

        var service = CreateService(
            context,
            CreateProfileMock(MonetaryMode.Dual, "USD", "GBP").Object,
            currencyRateService: CreateManualRateMock(CopRate).Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateCashAdvanceSaleAsync(
                requestedAmountLocal: 40000m,
                commissionAmountLocal: 2000m,
                paymentMethodId: 44,
                paymentMethodName: "Transferencia GBP",
                isTransfer: true,
                exchangeRate: CopRate,
                cashierId: 7));

        Assert.Contains("no está habilitado para ventas", exception.Message);
        Assert.False(await context.Sales.AsNoTracking().AnyAsync());
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync());
    }
}

