using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
/// 8.211-T3a (S6): snapshot aditivo del perfil monetario vigente al completar una venta.
/// La captura no puede alterar montos, tasas ni redondeos: los totales históricos siguen
/// siendo la fuente de cálculo y estas pruebas los fijan contra los valores canónicos.
/// 8.211-T3c3b2 (S4/S5): Single COP completes as a regular sale with identity rate; Dual
/// EUR/GBP stays rejected with the supported-profile message.
/// </summary>
public class SaleMonetarySnapshotTests
{
    // 8.211-T3c3c2: exact rejection message for profiles outside Dual USD/{VES,COP,MXN,EUR}
    // and Single 2dp.
    private const string UnsupportedCheckoutProfileMessage =
        "El perfil monetario actual no está habilitado para ventas: esta fase admite Dual USD/VES, USD/COP, USD/MXN o USD/EUR, o Single con una moneda de 2 decimales.";

    private static SalesService CreateService(
        SalesDbContext context,
        IMonetaryProfileService? monetaryProfileService = null,
        ILogger<SalesService>? logger = null)
    {
        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(i => i.GetCashAdvanceProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaleProductInfoDto { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var settings = new Mock<ISystemSettingsService>();

        // The optional monetary profile service is the last constructor parameter; named
        // arguments keep every historical positional caller untouched.
        return new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            cashDrawer.Object,
            settings.Object,
            monetaryProfileService: monetaryProfileService,
            logger: logger);
    }

    private static async Task<Sale> SeedPendingSaleAsync(SalesDbContext context, decimal totalUsd = 100m, decimal rate = 50m)
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
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 1,
            Name = "Punto de Venta",
            IsCash = false,
            IsActive = true,
            Currency = "Bs.S"
        });

        await context.SaveChangesAsync();
        return sale;
    }

    private static Mock<IMonetaryProfileService> CreateProfileMock(MonetaryProfileDto profile)
    {
        var mock = new Mock<IMonetaryProfileService>();
        mock.Setup(p => p.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        return mock;
    }

    private static List<PaymentInfo> ExactPayment(decimal amountUsd, decimal amountLocal) =>
        new() { new PaymentInfo(1, amountUsd, amountLocal, null) };

    [Fact]
    public async Task CompletedSale_WithoutMonetaryProfileService_PersistsLegacySnapshotAndKeepsFinancialTotals()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = CreateService(context);
        var sale = await SeedPendingSaleAsync(context);

        await service.CompleteSaleAsync(sale.Id, 50m, ExactPayment(100m, 5000m));

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal("USD", saved.PrimaryCurrencyCode);
        Assert.Equal("VES", saved.SecondaryCurrencyCode);
        Assert.Equal("Dual", saved.MonetaryMode);

        // Financial snapshot unchanged: the additive profile snapshot never recalculates amounts.
        Assert.Equal(100m, saved.TotalUSD);
        Assert.Equal(5000m, saved.TotalBsS);
        Assert.Equal(50m, saved.AppliedRate);
    }

    [Fact]
    public async Task CompletedSale_WithUnsupportedDualProfile_IsRejectedWithSupportedProfileMessage()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        // 8.211-T3c3b2: Dual EUR/GBP is still not enabled for checkout.
        var profile = CreateProfileMock(new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "EUR",
            PrimaryCurrencySymbol = "€",
            SecondaryCurrencyCode = "GBP",
            SecondaryCurrencySymbol = "£",
            IsConfigured = true
        });

        var service = CreateService(context, profile.Object);
        var sale = await SeedPendingSaleAsync(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteSaleAsync(sale.Id, 50m, ExactPayment(100m, 5000m)));

        Assert.Equal(UnsupportedCheckoutProfileMessage, exception.Message);

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Pending, saved.Status);
        Assert.Null(saved.PrimaryAmount);
        Assert.Null(saved.SecondaryAmount);
        Assert.Null(saved.AppliedRateSource);
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync(p => p.SaleId == sale.Id));

        // The rejected attempt never recalculates the canonical financial totals.
        Assert.Equal(100m, saved.TotalUSD);
        Assert.Equal(5000m, saved.TotalBsS);
        Assert.Equal(50m, saved.AppliedRate);
    }

    [Fact]
    public async Task CompletedSale_WithSingleCopProfile_PersistsNullSecondaryCurrencyAndIdentitySnapshot()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var profile = CreateProfileMock(new MonetaryProfileDto
        {
            Mode = MonetaryMode.Single,
            PrimaryCurrencyCode = "COP",
            PrimaryCurrencySymbol = "$",
            SecondaryCurrencyCode = null,
            SecondaryCurrencySymbol = null,
            IsConfigured = true
        });

        var service = CreateService(context, profile.Object);
        var sale = await SeedPendingSaleAsync(context);
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 50,
            Name = "Punto de Venta COP",
            IsCash = false,
            IsActive = true,
            Currency = "COP"
        });
        await context.SaveChangesAsync();

        await service.CompleteSaleAsync(
            sale.Id,
            50m,
            new List<PaymentInfo> { new(50, 100m, 100m, null) });

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal("COP", saved.PrimaryCurrencyCode);
        Assert.Null(saved.SecondaryCurrencyCode);
        Assert.Equal("Single", saved.MonetaryMode);
        Assert.Equal(1m, saved.AppliedRate);
        Assert.Equal(100m, saved.TotalUSD);
        Assert.Equal(100m, saved.TotalBsS);
        Assert.Equal(100m, saved.PrimaryAmount);
        Assert.Null(saved.SecondaryAmount);
        Assert.Equal("Identity", saved.AppliedRateSource);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);
        Assert.Equal("COP", payment.CurrencyCode);
        Assert.Equal(100m, payment.Amount);
        Assert.Equal(100m, payment.AmountBsS);
    }

    [Fact]
    public async Task CompletedSale_WithSingleProfile_OnRelationalProvider_UpdatesSecondaryToNull()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            var profile = CreateProfileMock(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "COP",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = null,
                SecondaryCurrencySymbol = null,
                IsConfigured = true
            });

            var service = CreateService(context, profile.Object);
            var sale = new Sale
            {
                TotalUSD = 100m,
                Subtotal = 100m,
                AppliedRate = 50m,
                TotalBsS = 5000m,
                SubtotalBsS = 5000m,
                Status = SaleStatus.Pending
            };
            context.Sales.Add(sale);
            // Id 100 avoids the HasData "Cash" method (Id 1), which would require a real drawer row.
            context.PaymentMethods.Add(new PaymentMethod
            {
                Id = 100,
                Name = "Punto de Venta Test",
                IsCash = false,
                IsActive = true,
                Currency = "COP"
            });
            await context.SaveChangesAsync();

            await service.CompleteSaleAsync(
                sale.Id,
                50m,
                new List<PaymentInfo> { new(100, 100m, 100m, null) });

            var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
            Assert.Equal(SaleStatus.Completed, saved.Status);
            Assert.Equal(1m, saved.AppliedRate);
            Assert.Equal("COP", saved.PrimaryCurrencyCode);
            // Explicit NULL wins over the column default even on the relational provider.
            Assert.Null(saved.SecondaryCurrencyCode);
            Assert.Equal("Single", saved.MonetaryMode);
            Assert.Equal(100m, saved.PrimaryAmount);
            Assert.Null(saved.SecondaryAmount);
            Assert.Equal("Identity", saved.AppliedRateSource);

            var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);
            Assert.Equal("COP", payment.CurrencyCode);
        }
    }

    [Fact]
    public async Task HoldSale_WithFullInitialPayment_WithDualProfile_PersistsSnapshotAndKeepsHistoricalAmounts()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Customers.Add(new Customer
        {
            Id = 2,
            Name = "Cliente Real",
            CedulaOrRif = "V-12345678",
            IsDefault = false,
            IsActive = true
        });
        await context.SaveChangesAsync();

        var profile = CreateProfileMock(new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "EUR",
            PrimaryCurrencySymbol = "€",
            SecondaryCurrencyCode = "GBP",
            SecondaryCurrencySymbol = "£",
            IsConfigured = true
        });

        var service = CreateService(context, profile.Object);
        var sale = await SeedPendingSaleAsync(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.HoldSaleAsync(sale.Id, new HoldSaleRequestDto
            {
                CustomerId = 2,
                ExchangeRate = 50m,
                InitialPayment = new AddPaymentRequestDto
                {
                    PaymentMethodId = 1,
                    AmountUSD = 100m,
                    AmountBsS = 5000m,
                    ExchangeRate = 50m
                }
            }));

        Assert.Equal(UnsupportedCheckoutProfileMessage, exception.Message);

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Pending, saved.Status);
        Assert.Null(saved.PrimaryAmount);
        Assert.Null(saved.SecondaryAmount);
        Assert.Null(saved.AppliedRateSource);
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync(p => p.SaleId == sale.Id));

        // The rejected completion never recalculates or rewrites the historical totals.
        Assert.Equal(100m, saved.TotalUSD);
        Assert.Equal(5000m, saved.TotalBsS);
        Assert.Equal(50m, saved.AppliedRate);
    }

    [Fact]
    public async Task CashAdvance_WithDualProfile_PersistsSnapshotAndKeepsHistoricalAmounts()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Customers.Add(new Customer
        {
            Id = 1,
            Name = "Consumidor Final",
            CedulaOrRif = "V-00000000",
            IsDefault = true,
            IsActive = true
        });
        await context.SaveChangesAsync();

        var profile = CreateProfileMock(new MonetaryProfileDto
        {
            Mode = MonetaryMode.Dual,
            PrimaryCurrencyCode = "EUR",
            PrimaryCurrencySymbol = "€",
            SecondaryCurrencyCode = "GBP",
            SecondaryCurrencySymbol = "£",
            IsConfigured = true
        });

        var service = CreateService(context, profile.Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateCashAdvanceSaleAsync(
                requestedAmountLocal: 1000m,
                commissionAmountLocal: 70m,
                paymentMethodId: 2,
                paymentMethodName: "Transferencia",
                isTransfer: true,
                exchangeRate: 50m,
                cashierId: 7));

        Assert.Equal(UnsupportedCheckoutProfileMessage, exception.Message);

        // The cash advance creates its completed sale directly: a rejected profile must create none.
        Assert.False(await context.Sales.AsNoTracking().AnyAsync());
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task CompletedSale_WhenProfileReadFails_FailsClosedWithoutMutatingTheSale()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var profile = new Mock<IMonetaryProfileService>();
        profile
            .Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("settings store unavailable"));
        var logger = new Mock<ILogger<SalesService>>();

        var service = CreateService(context, profile.Object, logger.Object);
        var sale = await SeedPendingSaleAsync(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteSaleAsync(sale.Id, 50m, ExactPayment(100m, 5000m)));

        Assert.Contains("perfil monetario", exception.Message, StringComparison.Ordinal);
        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Pending, saved.Status);
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task SaleWithExplicitNullSecondaryCurrency_PersistsNullDespiteColumnDefaultOnRelationalProvider()
    {
        // S6 decision: Single mode persists NULL while legacy rows are backfilled to VES by the
        // column default. SQLite enforces column defaults, so this guards the runtime insert
        // semantics that InMemory does not exercise.
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            context.Sales.Add(new Sale
            {
                Status = SaleStatus.Completed,
                TotalUSD = 1m,
                AppliedRate = 1m,
                TotalBsS = 1m,
                PrimaryCurrencyCode = "COP",
                SecondaryCurrencyCode = null,
                MonetaryMode = "Single"
            });

            await context.SaveChangesAsync();

            var saved = await context.Sales.AsNoTracking().SingleAsync();
            Assert.Equal("COP", saved.PrimaryCurrencyCode);
            Assert.Null(saved.SecondaryCurrencyCode);
            Assert.Equal("Single", saved.MonetaryMode);
        }
    }
}


