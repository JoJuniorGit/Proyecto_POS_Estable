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
/// 8.211-T3c3c2 (S7): Dual USD/{VES,COP,MXN,EUR} is enabled for checkout. The secondary keeps its
/// own 2dp tender, the manual daily pair is the only rate source for non-VES secondaries, and the
/// local drawer/change use the normalized secondary currency. Profiles outside the supported set
/// are still rejected before completing the sale.
/// </summary>
public class DualSecondaryCurrencyCheckoutTests
{
    // Exact contract message from SalesService.EnsureSupportedCheckoutMonetaryProfile.
    private const string SupportedCheckoutProfileMessage =
        "El perfil monetario actual no está habilitado para ventas: esta fase admite Dual USD/VES, USD/COP, USD/MXN o USD/EUR, o Single con una moneda de 2 decimales.";

    private const decimal CopRate = 4000m;

    private static SalesService CreateService(
        SalesDbContext context,
        IMonetaryProfileService? monetaryProfileService = null,
        ICashDrawerService? cashDrawerService = null,
        ICurrencyRateService? currencyRateService = null)
    {
        var inventory = new Mock<IInventoryService>();
        // BCV must never be the fallback for a manual secondary; tests that assert fail-closed
        // verify this setup is never invoked.
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

    private static Mock<IMonetaryProfileService> CreateDualProfileMock(
        string secondaryCode,
        string primaryCode = "USD")
    {
        var mock = new Mock<IMonetaryProfileService>();
        mock
            .Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = primaryCode,
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = secondaryCode,
                SecondaryCurrencySymbol = secondaryCode == "VES" ? "Bs.S" : secondaryCode,
                IsConfigured = true
            });
        return mock;
    }

    private static Mock<ICurrencyRateService> CreateManualRateMock(
        string secondaryCode,
        decimal rate,
        CurrencyRateSource source = CurrencyRateSource.Manual)
    {
        var mock = new Mock<ICurrencyRateService>();
        mock
            .Setup(r => r.GetRateAsync("USD", secondaryCode, It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrencyRateQuote(rate, source, TimeZoneHelper.GetVenezuelaDate(), "USD", secondaryCode));
        return mock;
    }

    private static async Task<Sale> SeedPendingSaleAsync(
        SalesDbContext context,
        decimal totalUsd = 10m,
        decimal rate = CopRate)
    {
        var sale = new Sale
        {
            TotalUSD = totalUsd,
            Subtotal = totalUsd,
            AppliedRate = rate,
            TotalBsS = totalUsd * rate,
            SubtotalBsS = totalUsd * rate,
            Status = SaleStatus.Pending
        };

        context.Sales.Add(sale);
        await context.SaveChangesAsync();
        return sale;
    }

    private static void AddMethod(
        SalesDbContext context,
        int id,
        string name,
        string currency,
        bool isCash = false)
    {
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = id,
            Name = name,
            IsCash = isCash,
            IsActive = true,
            Currency = currency
        });
    }

    private static List<PaymentInfo> Payment(int methodId, decimal amountUsd, decimal amountLocal) =>
        new() { new PaymentInfo(methodId, amountUsd, amountLocal, null) };

    [Fact]
    public async Task CompletedSale_DualUsdCop_WithManualQuote_PersistsCopSnapshotsAndTender()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 31, "Pago Movil COP", "COP");
        await context.SaveChangesAsync();

        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(50m);
        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var service = new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            cashDrawer.Object,
            new Mock<ISystemSettingsService>().Object,
            monetaryProfileService: CreateDualProfileMock("COP").Object,
            currencyRateService: CreateManualRateMock("COP", CopRate).Object);

        var sale = await SeedPendingSaleAsync(context);

        await service.CompleteSaleAsync(sale.Id, CopRate, Payment(31, 10m, 40000m));

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal("USD", saved.PrimaryCurrencyCode);
        Assert.Equal("COP", saved.SecondaryCurrencyCode);
        Assert.Equal("Dual", saved.MonetaryMode);
        Assert.Equal("Manual", saved.AppliedRateSource);
        Assert.Equal(saved.TotalUSD, saved.PrimaryAmount);
        Assert.Equal(saved.TotalBsS, saved.SecondaryAmount);
        Assert.Equal(10m, saved.PrimaryAmount);
        Assert.Equal(40000m, saved.SecondaryAmount);
        Assert.Equal(4000m, saved.AppliedRate);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);
        Assert.Equal("COP", payment.CurrencyCode);
        Assert.Equal(40000m, payment.AmountBsS);
        Assert.Equal(40000m, payment.OriginalAmount);
        Assert.Equal(10m, payment.Amount);

        // Non-cash tender never writes to the physical drawer.
        Assert.False(await context.CashTransactions.AsNoTracking().AnyAsync());
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompletedSale_DualUsdCop_FractionalCashOverpayment_RecordsCopIncomeChangeAndLocalBalance()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 32, "Efectivo COP", "COP", isCash: true);
        await context.SaveChangesAsync();

        var profile = CreateDualProfileMock("COP");
        var cashDrawer = new CashDrawerService(context, profile.Object);
        var session = await cashDrawer.OpenSessionAsync(5000m, CopRate);

        var service = CreateService(
            context,
            profile.Object,
            cashDrawer,
            CreateManualRateMock("COP", CopRate).Object);

        var sale = await SeedPendingSaleAsync(context);

        // 41,000.50 COP for a 40,000.00 COP total: fractional cash is accepted and the 1,000.50
        // excess is returned in the local (COP) drawer.
        await service.CompleteSaleAsync(sale.Id, CopRate, Payment(32, 10.25m, 41000.50m));

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal("Manual", saved.AppliedRateSource);
        Assert.Equal("COP", saved.SecondaryCurrencyCode);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);
        Assert.Equal("COP", payment.CurrencyCode);
        Assert.Equal(41000.50m, payment.AmountBsS);
        Assert.Equal(41000.50m, payment.OriginalAmount);

        var income = await context.CashTransactions.AsNoTracking()
            .SingleAsync(t => t.SaleId == sale.Id
                && t.Type == CashTransactionType.Income
                && t.Source == CashTransactionSource.SalePayment);
        Assert.Equal("COP", income.CurrencyCode);
        Assert.Equal(41000.50m, income.AmountLocal);

        var change = await context.CashTransactions.AsNoTracking()
            .SingleAsync(t => t.SaleId == sale.Id
                && t.Type == CashTransactionType.Expense
                && t.Source == CashTransactionSource.SalePayment);
        Assert.Equal("COP", change.CurrencyCode);
        Assert.Equal(1000.50m, change.AmountLocal);
        Assert.Equal(0.25m, change.AmountUsd);
        Assert.Equal(32, change.PaymentMethodId);

        // Opening 5,000 + cash income 41,000.50 - change 1,000.50, all in the COP bucket.
        Assert.Equal(45000m, await cashDrawer.GetCurrentBalanceLocalAsync(session.Id));
    }

    [Fact]
    public async Task CompletedSale_DualUsdCop_WithoutManualRateService_FailsClosedWithoutBcvFallback()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 33, "Efectivo COP", "COP", isCash: true);
        await context.SaveChangesAsync();

        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(50m);
        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var service = new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            cashDrawer.Object,
            new Mock<ISystemSettingsService>().Object,
            monetaryProfileService: CreateDualProfileMock("COP").Object);

        var sale = await SeedPendingSaleAsync(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteSaleAsync(sale.Id, CopRate, Payment(33, 10m, 40000m)));

        Assert.Contains("No hay servicio de tasas manuales configurado para el par USD/COP", exception.Message);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Pending, saved.Status);
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync(p => p.SaleId == sale.Id));
        Assert.False(await context.CashTransactions.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task CompletedSale_DualUsdCop_WithoutTodaysQuote_FailsClosedWithoutBcvFallback()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 34, "Pago Movil COP", "COP");
        await context.SaveChangesAsync();

        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(50m);
        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var rateService = new Mock<ICurrencyRateService>();
        rateService
            .Setup(r => r.GetRateAsync("USD", "COP", It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No hay tasa manual configurada para USD->COP en la fecha 2026-10-28."));

        var service = new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            cashDrawer.Object,
            new Mock<ISystemSettingsService>().Object,
            monetaryProfileService: CreateDualProfileMock("COP").Object,
            currencyRateService: rateService.Object);

        var sale = await SeedPendingSaleAsync(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteSaleAsync(sale.Id, CopRate, Payment(34, 10m, 40000m)));

        Assert.Contains("No hay tasa manual configurada", exception.Message);
        inventory.Verify(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Pending, saved.Status);
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync(p => p.SaleId == sale.Id));
    }

    [Theory]
    [InlineData("USD", "GBP")]
    [InlineData("EUR", "COP")]
    public async Task CompletedSale_UnsupportedDualProfile_StillRejectsWithUpdatedMessage(
        string primaryCode,
        string secondaryCode)
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 35, "Efectivo COP", "COP", isCash: true);
        await context.SaveChangesAsync();

        var service = CreateService(
            context,
            CreateDualProfileMock(secondaryCode, primaryCode).Object,
            currencyRateService: CreateManualRateMock(secondaryCode, CopRate).Object);

        var sale = await SeedPendingSaleAsync(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteSaleAsync(sale.Id, CopRate, Payment(35, 10m, 40000m)));

        Assert.Equal(SupportedCheckoutProfileMessage, exception.Message);

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Pending, saved.Status);
        Assert.Null(saved.SecondaryAmount);
        Assert.Null(saved.AppliedRateSource);
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync(p => p.SaleId == sale.Id));
    }

    [Fact]
    public async Task CompletedSale_DualUsdEur_PreservesManualRatePrecisionInSnapshotsAndTotals()
    {
        // 8.211-T3c3d (S4/S5): 0.9245 must survive the sale snapshot and the EUR tender. The old
        // 2dp ceiling produced 0.93 and a 0.5% FX distortion on every sale.
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        const decimal rate = 0.9245m;
        AddMethod(context, 38, "Punto de Venta EUR", "EUR");
        await context.SaveChangesAsync();

        var service = CreateService(
            context,
            CreateDualProfileMock("EUR").Object,
            currencyRateService: CreateManualRateMock("EUR", rate).Object);

        var sale = await SeedPendingSaleAsync(context, totalUsd: 100m, rate: rate);

        await service.CompleteSaleAsync(sale.Id, rate, Payment(38, 100m, 92.45m));

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(0.9245m, saved.AppliedRate);
        Assert.Equal(92.45m, saved.TotalBsS);
        Assert.Equal(92.45m, saved.SecondaryAmount);
        Assert.Equal("Manual", saved.AppliedRateSource);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);
        Assert.Equal(0.9245m, payment.ExchangeRate);
        Assert.Equal(92.45m, payment.AmountBsS);
        Assert.Equal(92.45m, payment.OriginalAmount);
    }

    [Fact]
    public async Task CompletedSale_DualUsdEur_UsesEightDecimalCalculationRateForTheTotal()
    {
        // 8.211-T3c3d (S4/S5): the sale total must use the 8dp calculation rate, not the 4dp
        // snapshot. 110 × 0.924049 = 101.64539 → 101.65; the 4dp snapshot (0.9240) yields 101.64.
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        const decimal rate = 0.924049m;
        AddMethod(context, 39, "Punto de Venta EUR", "EUR");
        await context.SaveChangesAsync();

        var service = CreateService(
            context,
            CreateDualProfileMock("EUR").Object,
            currencyRateService: CreateManualRateMock("EUR", rate).Object);

        var sale = await SeedPendingSaleAsync(context, totalUsd: 110m, rate: rate);

        await service.CompleteSaleAsync(sale.Id, rate, Payment(39, 110m, 101.65m));

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(0.9240m, saved.AppliedRate);
        Assert.Equal(101.65m, saved.TotalBsS);
        Assert.Equal(101.65m, saved.SecondaryAmount);
    }

    [Theory]
    [InlineData("EUR", 0.92, 100, 92, 36)]
    [InlineData("MXN", 18, 100, 1800, 37)]
    public async Task CompletedSale_DualUsdNonVes_WithManualQuote_CompletesWithManualSource(
        string secondaryCode,
        decimal rate,
        decimal totalUsd,
        decimal totalLocal,
        int methodId)
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, methodId, $"Punto de Venta {secondaryCode}", secondaryCode);
        await context.SaveChangesAsync();

        var service = CreateService(
            context,
            CreateDualProfileMock(secondaryCode).Object,
            currencyRateService: CreateManualRateMock(secondaryCode, rate).Object);

        var sale = await SeedPendingSaleAsync(context, totalUsd: totalUsd, rate: rate);

        await service.CompleteSaleAsync(sale.Id, rate, Payment(methodId, totalUsd, totalLocal));

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(secondaryCode, saved.SecondaryCurrencyCode);
        Assert.Equal("Manual", saved.AppliedRateSource);
        Assert.Equal(saved.TotalBsS, saved.SecondaryAmount);
        Assert.Equal(totalLocal, saved.SecondaryAmount);
        Assert.Equal(rate, saved.AppliedRate);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);
        Assert.Equal(secondaryCode, payment.CurrencyCode);
        Assert.Equal(totalLocal, payment.OriginalAmount);
    }
}
