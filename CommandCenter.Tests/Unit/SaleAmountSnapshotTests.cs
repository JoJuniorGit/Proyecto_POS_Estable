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
/// 8.211-T3c2 (S6): snapshots aditivos de importes para la ruta USD-primaria actual.
/// <see cref="Sale.PrimaryAmount"/>/<see cref="Sale.SecondaryAmount"/> duplican los totales
/// existentes sin recalcularlos, <see cref="Sale.AppliedRateSource"/> describe la fuente de la
/// tasa y <see cref="SalePayment.OriginalAmount"/> conserva el importe en la moneda del método.
/// La guarda de perfil solo habilita <c>Dual USD/VES</c>: cualquier otro régimen se rechaza
/// antes de completar la venta, sin convertir moneda ni alterar montos, tasas o redondeos.
/// </summary>
public class SaleAmountSnapshotTests
{
    // Exact contract message from SalesService.EnsureSupportedCheckoutMonetaryProfile.
    private const string SupportedCheckoutProfileMessage =
        "El perfil monetario actual no está habilitado para ventas: esta fase solo admite Dual USD/VES. La conversión para otras monedas se habilitará en la siguiente fase.";

    private static SalesService CreateService(
        SalesDbContext context,
        IMonetaryProfileService? monetaryProfileService = null)
    {
        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(i => i.GetCashAdvanceProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaleProductInfoDto { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        return new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            cashDrawer.Object,
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

    private static MonetaryProfileDto UnsupportedProfile(
        MonetaryMode mode,
        string primaryCurrencyCode,
        string? secondaryCurrencyCode) => new()
    {
        Mode = mode,
        PrimaryCurrencyCode = primaryCurrencyCode,
        PrimaryCurrencySymbol = "$",
        SecondaryCurrencyCode = secondaryCurrencyCode,
        SecondaryCurrencySymbol = secondaryCurrencyCode is null ? null : "S",
        IsConfigured = true
    };

    private static List<PaymentInfo> Payment(int methodId, decimal amountUsd, decimal amountLocal) =>
        new() { new PaymentInfo(methodId, amountUsd, amountLocal, null) };

    [Fact]
    public async Task CompletedSale_DefaultDualUsdVes_PersistsAmountSnapshotsAndKeepsFinancialTotals()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 3, "Punto de Venta", "Bs.S");
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var sale = await SeedPendingSaleAsync(context);

        await service.CompleteSaleAsync(sale.Id, 50m, Payment(3, 100m, 5000m));

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);

        // The new amounts duplicate the canonical totals; they do not reinterpret them.
        Assert.Equal(saved.TotalUSD, saved.PrimaryAmount);
        Assert.Equal(saved.TotalBsS, saved.SecondaryAmount);
        Assert.Equal(100m, saved.PrimaryAmount);
        Assert.Equal(5000m, saved.SecondaryAmount);
        Assert.Equal("Bcv", saved.AppliedRateSource);

        // No amount, rate or rounding drift on the supported path.
        Assert.Equal(100m, saved.TotalUSD);
        Assert.Equal(5000m, saved.TotalBsS);
        Assert.Equal(50m, saved.AppliedRate);
        Assert.Equal(0m, saved.RoundingAdjustment);
        Assert.Equal(5000m, saved.FinalPaidAmountBsS);
    }

    [Fact]
    public async Task CompletedSale_ExposesAmountSnapshotsThroughSaleAndPaymentDtos()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 3, "Punto de Venta", "Bs.S");
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var sale = await SeedPendingSaleAsync(context);

        await service.CompleteSaleAsync(sale.Id, 50m, Payment(3, 100m, 5000m));

        var dto = await service.GetSaleAsync(sale.Id);
        Assert.Equal(100m, dto.PrimaryAmount);
        Assert.Equal(5000m, dto.SecondaryAmount);
        Assert.Equal("Bcv", dto.AppliedRateSource);

        var payment = Assert.Single(dto.Payments);
        Assert.Equal(5000m, payment.OriginalAmount);
        Assert.Equal(5000m, payment.AmountBsS);
        Assert.Equal(100m, payment.Amount);
    }

    [Fact]
    public async Task Checkout_PaymentOriginalAmount_UsesTheTenderCurrencyOfEachMethod()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 3, "Punto de Venta", "Bs.S");
        AddMethod(context, 4, "Zelle USD", "USD");
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var sale = await SeedPendingSaleAsync(context);

        await service.CompleteSaleAsync(
            sale.Id,
            50m,
            new List<PaymentInfo>
            {
                new(3, 40m, 2000m, null),
                new(4, 60m, 3000m, null)
            });

        var payments = await context.SalePayments
            .AsNoTracking()
            .Where(p => p.SaleId == sale.Id)
            .OrderBy(p => p.PaymentMethodId)
            .ToListAsync();

        Assert.Equal(2, payments.Count);
        // Local method: original amount is the local tender amount.
        Assert.Equal(2000m, payments[0].OriginalAmount);
        Assert.Equal(payments[0].AmountBsS, payments[0].OriginalAmount);
        // USD method: original amount is the USD amount.
        Assert.Equal(60m, payments[1].OriginalAmount);
        Assert.Equal(payments[1].Amount, payments[1].OriginalAmount);
    }

    [Fact]
    public async Task CashAdvance_DefaultProfile_PersistsAmountSnapshotsAndPaymentOriginalAmount()
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
        AddMethod(context, 2, "Transferencia", "Bs.S");
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var dto = await service.CreateCashAdvanceSaleAsync(
            requestedAmountLocal: 1000m,
            commissionAmountLocal: 70m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia",
            isTransfer: true,
            exchangeRate: 50m,
            cashierId: 7);

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == dto.Id);
        Assert.Equal(21.4m, saved.PrimaryAmount);
        Assert.Equal(1070m, saved.SecondaryAmount);
        Assert.Equal("Bcv", saved.AppliedRateSource);
        Assert.Equal(21.4m, saved.TotalUSD);
        Assert.Equal(1070m, saved.TotalBsS);
        Assert.Equal(50m, saved.AppliedRate);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == dto.Id);
        Assert.Equal(1070m, payment.OriginalAmount);
        Assert.Equal(1070m, payment.AmountBsS);
    }

    [Fact]
    public async Task HoldSale_CompletedWithFullInitialPayment_PersistsAmountSnapshots()
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
        AddMethod(context, 3, "Punto de Venta", "Bs.S");
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var sale = await SeedPendingSaleAsync(context);

        await service.HoldSaleAsync(sale.Id, new HoldSaleRequestDto
        {
            CustomerId = 2,
            ExchangeRate = 50m,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = 3,
                AmountUSD = 100m,
                AmountBsS = 5000m,
                ExchangeRate = 50m
            }
        });

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(100m, saved.PrimaryAmount);
        Assert.Equal(5000m, saved.SecondaryAmount);
        Assert.Equal("Bcv", saved.AppliedRateSource);
        Assert.Equal(100m, saved.TotalUSD);
        Assert.Equal(5000m, saved.TotalBsS);
        Assert.Equal(50m, saved.AppliedRate);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);
        Assert.Equal(5000m, payment.OriginalAmount);
        Assert.Equal(5000m, payment.AmountBsS);
    }

    [Theory]
    [InlineData(MonetaryMode.Single, "USD", null)]
    [InlineData(MonetaryMode.Dual, "USD", "EUR")]
    [InlineData(MonetaryMode.Single, "COP", null)]
    public async Task CompletedSale_UnsupportedMonetaryProfile_IsRejectedAndDoesNotCompleteTheSale(
        MonetaryMode mode,
        string primaryCurrencyCode,
        string? secondaryCurrencyCode)
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 3, "Punto de Venta", "Bs.S");
        await context.SaveChangesAsync();

        var profile = CreateProfileMock(UnsupportedProfile(mode, primaryCurrencyCode, secondaryCurrencyCode));
        var service = CreateService(context, profile.Object);
        var sale = await SeedPendingSaleAsync(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteSaleAsync(sale.Id, 50m, Payment(3, 100m, 5000m)));

        Assert.Equal(SupportedCheckoutProfileMessage, exception.Message);

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Pending, saved.Status);
        Assert.Null(saved.PrimaryAmount);
        Assert.Null(saved.SecondaryAmount);
        Assert.Null(saved.AppliedRateSource);
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync(p => p.SaleId == sale.Id));
    }

    [Fact]
    public async Task CashAdvance_UnsupportedMonetaryProfile_IsRejectedBeforeCreatingTheSale()
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
        AddMethod(context, 2, "Transferencia", "Bs.S");
        await context.SaveChangesAsync();

        var profile = CreateProfileMock(UnsupportedProfile(MonetaryMode.Single, "USD", null));
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

        Assert.Equal(SupportedCheckoutProfileMessage, exception.Message);
        Assert.False(await context.Sales.AsNoTracking().AnyAsync());
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task HoldSale_UnsupportedMonetaryProfile_IsRejectedAndDoesNotCompleteTheSale()
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
        AddMethod(context, 3, "Punto de Venta", "Bs.S");
        await context.SaveChangesAsync();

        var profile = CreateProfileMock(UnsupportedProfile(MonetaryMode.Dual, "USD", "EUR"));
        var service = CreateService(context, profile.Object);
        var sale = await SeedPendingSaleAsync(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.HoldSaleAsync(sale.Id, new HoldSaleRequestDto
            {
                CustomerId = 2,
                ExchangeRate = 50m,
                InitialPayment = new AddPaymentRequestDto
                {
                    PaymentMethodId = 3,
                    AmountUSD = 100m,
                    AmountBsS = 5000m,
                    ExchangeRate = 50m
                }
            }));

        Assert.Equal(SupportedCheckoutProfileMessage, exception.Message);

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Pending, saved.Status);
        Assert.Null(saved.PrimaryAmount);
        Assert.Null(saved.SecondaryAmount);
        Assert.Null(saved.AppliedRateSource);
        Assert.False(await context.SalePayments.AsNoTracking().AnyAsync(p => p.SaleId == sale.Id));
    }
}
