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
using ICashDrawerService = Sales.Module.Interfaces.ICashDrawerService;
using CashDrawerStatus = Sales.Module.Entities.CashDrawerStatus;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T3c3b2 (S4/S5/S7): Single mode is enabled for regular checkout when the primary
/// currency uses 2 decimals. The identity rate from T3c3b1 is the effective rate, the Bs.S
/// mirror keeps the legacy column semantics, and payment methods must belong to the active
/// profile. Hold orders, add-payment-to-hold and cash advances stay blocked for Single.
/// </summary>
public class SingleCurrencyCheckoutTests
{
    private const string SupportedCheckoutProfileMessage =
        "El perfil monetario actual no está habilitado para ventas: esta fase admite Dual USD/VES o Single con una moneda de 2 decimales. La conversión para otras monedas se habilitará en la siguiente fase.";

    private const string SingleOperationPendingMessage =
        "La operación con moneda única se habilitará en la siguiente fase.";

    private static SalesService CreateService(
        SalesDbContext context,
        IMonetaryProfileService? monetaryProfileService = null,
        ICashDrawerService? cashDrawerService = null)
    {
        var inventory = new Mock<IInventoryService>();
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
            monetaryProfileService: monetaryProfileService);
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
        await context.SaveChangesAsync();
        return sale;
    }

    private static void AddMethod(
        SalesDbContext context,
        int id,
        string name,
        string? currency,
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

    private static Mock<IMonetaryProfileService> CreateProfileMock(MonetaryProfileDto profile)
    {
        var mock = new Mock<IMonetaryProfileService>();
        mock.Setup(p => p.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        return mock;
    }

    private static Mock<IMonetaryProfileService> SingleProfile(string primaryCurrencyCode) =>
        CreateProfileMock(new MonetaryProfileDto
        {
            Mode = MonetaryMode.Single,
            PrimaryCurrencyCode = primaryCurrencyCode,
            PrimaryCurrencySymbol = "$",
            SecondaryCurrencyCode = null,
            SecondaryCurrencySymbol = null,
            IsConfigured = true
        });

    private static List<PaymentInfo> Payment(int methodId, decimal amountUsd, decimal amountLocal) =>
        new() { new PaymentInfo(methodId, amountUsd, amountLocal, null) };

    [Fact]
    public async Task CompletedSale_SingleCopProfile_PersistsIdentitySnapshotAndCopPayment()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 21, "Punto de Venta COP", "COP");
        await context.SaveChangesAsync();

        var profile = SingleProfile("COP");
        var service = CreateService(context, profile.Object);
        var sale = await SeedPendingSaleAsync(context);

        await service.CompleteSaleAsync(sale.Id, 50m, Payment(21, 100m, 100m));

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(1m, saved.AppliedRate);
        Assert.Equal(100m, saved.TotalUSD);
        // The Bs.S mirror keeps the legacy column semantics: with identity it equals the USD total.
        Assert.Equal(100m, saved.TotalBsS);
        Assert.Equal(saved.TotalUSD, saved.PrimaryAmount);
        Assert.Null(saved.SecondaryAmount);
        Assert.Equal("Identity", saved.AppliedRateSource);
        Assert.Equal("COP", saved.PrimaryCurrencyCode);
        Assert.Null(saved.SecondaryCurrencyCode);
        Assert.Equal("Single", saved.MonetaryMode);
        Assert.Equal(100m, saved.FinalPaidAmountBsS);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);
        Assert.Equal("COP", payment.CurrencyCode);
        Assert.Equal(100m, payment.Amount);
        Assert.Equal(100m, payment.AmountBsS);
        Assert.Equal(100m, payment.OriginalAmount);
        Assert.Equal(1m, payment.ExchangeRate);
    }

    [Fact]
    public async Task CompletedSale_SingleCopCashWithCents_AcceptsFractionalCashAndReturnsChangeInCop()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 22, "Efectivo COP", "COP", isCash: true);
        await context.SaveChangesAsync();

        var profile = SingleProfile("COP");
        var cashDrawer = new CashDrawerService(context, profile.Object);
        await cashDrawer.OpenSessionAsync(1000m, 1m);

        var service = CreateService(context, profile.Object, cashDrawer);
        var sale = await SeedPendingSaleAsync(context);

        await service.CompleteSaleAsync(sale.Id, 50m, Payment(22, 100.50m, 100.50m));

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(1m, saved.AppliedRate);
        Assert.Equal(100m, saved.TotalUSD);
        Assert.Equal(100m, saved.TotalBsS);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);
        Assert.Equal("COP", payment.CurrencyCode);
        Assert.Equal(100.50m, payment.Amount);
        Assert.Equal(100.50m, payment.AmountBsS);
        Assert.Equal(100.50m, payment.OriginalAmount);

        var income = await context.CashTransactions.AsNoTracking()
            .SingleAsync(t => t.SaleId == sale.Id
                && t.Type == CashTransactionType.Income
                && t.Source == CashTransactionSource.SalePayment);
        Assert.Equal("COP", income.CurrencyCode);
        Assert.Equal(100.50m, income.AmountLocal);

        // The change is attributed to the Single primary cash method and paid in COP.
        var change = await context.CashTransactions.AsNoTracking()
            .SingleAsync(t => t.SaleId == sale.Id
                && t.Type == CashTransactionType.Expense
                && t.Source == CashTransactionSource.SalePayment);
        Assert.Equal("COP", change.CurrencyCode);
        Assert.Equal(0.50m, change.AmountLocal);
        Assert.Equal(0.50m, change.AmountUsd);
        Assert.Equal(22, change.PaymentMethodId);
    }

    [Fact]
    public async Task CompletedSale_SingleCopProfile_RejectsLocalCurrencyMethod()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 23, "Punto de Venta", "Bs.S");
        await context.SaveChangesAsync();

        var service = CreateService(context, SingleProfile("COP").Object);
        var sale = await SeedPendingSaleAsync(context);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CompleteSaleAsync(sale.Id, 50m, Payment(23, 100m, 100m)));

        Assert.Equal(
            "El método de pago 'Punto de Venta' usa la moneda VES, pero el perfil actual solo admite COP.",
            exception.Message);

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Pending, saved.Status);
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync(p => p.SaleId == sale.Id));
    }

    [Fact]
    public async Task CompletedSale_DualUsdVesProfile_RejectsCopMethod()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 24, "Punto de Venta COP", "COP");
        await context.SaveChangesAsync();

        // No profile service means the historical Dual USD/VES default.
        var service = CreateService(context);
        var sale = await SeedPendingSaleAsync(context);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CompleteSaleAsync(sale.Id, 50m, Payment(24, 100m, 5000m)));

        Assert.Equal(
            "El método de pago 'Punto de Venta COP' usa la moneda COP, pero el perfil actual solo admite USD y VES.",
            exception.Message);

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Pending, saved.Status);
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync(p => p.SaleId == sale.Id));
    }

    [Fact]
    public async Task HoldSale_SingleProfile_IsRejectedBeforeAnyMutation()
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
        AddMethod(context, 25, "Punto de Venta COP", "COP");
        await context.SaveChangesAsync();

        var service = CreateService(context, SingleProfile("COP").Object);
        var sale = await SeedPendingSaleAsync(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.HoldSaleAsync(sale.Id, new HoldSaleRequestDto
            {
                CustomerId = 2,
                ExchangeRate = 50m,
                InitialPayment = new AddPaymentRequestDto
                {
                    PaymentMethodId = 25,
                    AmountUSD = 100m,
                    AmountBsS = 100m,
                    ExchangeRate = 1m
                }
            }));

        Assert.Equal(SingleOperationPendingMessage, exception.Message);

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Pending, saved.Status);
        Assert.Null(saved.PrimaryAmount);
        Assert.Null(saved.SecondaryAmount);
        Assert.Null(saved.AppliedRateSource);
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync(p => p.SaleId == sale.Id));
        Assert.False(await context.CashTransactions.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task CashAdvance_SingleProfile_IsRejectedBeforeCreatingTheSale()
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
        AddMethod(context, 26, "Transferencia COP", "COP");
        await context.SaveChangesAsync();

        var service = CreateService(context, SingleProfile("COP").Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateCashAdvanceSaleAsync(
                requestedAmountLocal: 1000m,
                commissionAmountLocal: 70m,
                paymentMethodId: 26,
                paymentMethodName: "Transferencia COP",
                isTransfer: true,
                exchangeRate: 50m,
                cashierId: 7));

        Assert.Equal(SingleOperationPendingMessage, exception.Message);
        Assert.False(await context.Sales.AsNoTracking().AnyAsync());
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task CheckoutPreview_SingleCopProfile_UsesIdentityRateAndCopMirror()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 27, "Punto de Venta COP", "COP");
        await context.SaveChangesAsync();

        var service = CreateService(context, SingleProfile("COP").Object);
        var sale = await SeedPendingSaleAsync(context);

        var previewService = new CheckoutCalculationService(service);
        var preview = await previewService.CalculatePreviewAsync(sale.Id, new CheckoutPreviewRequest
        {
            ExchangeRate = 50m,
            Payments = new List<SalePaymentDto>
            {
                new() { PaymentMethodId = 27, Amount = 100m, AmountBsS = 100m }
            }
        });

        // The identity rate overrides the BCV-style request rate: no conversion happens.
        Assert.Equal(100m, preview.TotalUSD);
        Assert.Equal(100m, preview.TotalBsS);
        Assert.Equal(100m, preview.EffectiveTotalUSD);
        Assert.Equal(100m, preview.TotalPaidUSD);
        Assert.Equal(100m, preview.TotalPaidBsS);
        Assert.True(preview.IsFullyPaid);
        Assert.Equal(0m, preview.ChangeDueUSD);
        Assert.Equal(0m, preview.ChangeDueBsS);
    }

    [Fact]
    public async Task CompletedSale_SingleJpyProfile_IsRejectedWithSupportedProfileMessage()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 28, "Punto de Venta JPY", "JPY");
        await context.SaveChangesAsync();

        var service = CreateService(context, SingleProfile("JPY").Object);
        var sale = await SeedPendingSaleAsync(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteSaleAsync(sale.Id, 50m, Payment(28, 100m, 100m)));

        Assert.Equal(SupportedCheckoutProfileMessage, exception.Message);

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Pending, saved.Status);
        Assert.Null(saved.PrimaryAmount);
        Assert.Null(saved.SecondaryAmount);
        Assert.Null(saved.AppliedRateSource);
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync(p => p.SaleId == sale.Id));
    }
}
