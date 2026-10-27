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
/// 8.211-T3b1 (S6/S8): snapshot aditivo de la moneda del método de pago en cada abono.
/// La moneda se congela desde la columna persistida de <see cref="PaymentMethod.Currency"/>;
/// el nombre del método es solo fallback legacy y un rename posterior no puede reclasificar
/// el historial. Los montos, tasas y totales existentes no se alteran.
/// </summary>
public class SalePaymentCurrencySnapshotTests
{
    private static SalesService CreateService(SalesDbContext context)
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
            new Mock<ISystemSettingsService>().Object);
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

    private static void AddMethod(SalesDbContext context, int id, string name, string? currency, bool isCash = false)
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

    [Fact]
    public async Task Checkout_PersistedCurrencyBeatsUsdLikeName_SnapshotsPersistedCurrencyAndKeepsTotals()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        // S8: a USD-like name cannot override the persisted currency of the drawer.
        AddMethod(context, 10, "Efectivo USD", "Bs.S", isCash: true);
        var sale = await SeedPendingSaleAsync(context);
        var service = CreateService(context);

        await service.CompleteSaleAsync(sale.Id, 50m, new List<PaymentInfo> { new(10, 100m, 5000m, null) });

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);
        Assert.Equal("Bs.S", payment.CurrencyCode);

        // Additive snapshot only: amounts, rate and totals stay canonical.
        Assert.Equal(100m, payment.Amount);
        Assert.Equal(5000m, payment.AmountBsS);
        Assert.Equal(50m, payment.ExchangeRate);

        var savedSale = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(100m, savedSale.TotalUSD);
        Assert.Equal(5000m, savedSale.TotalBsS);
        Assert.Equal(50m, savedSale.AppliedRate);
    }

    [Fact]
    public async Task Checkout_PersistedUsdCurrency_SnapshotsUsd()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 11, "Punto de Venta", "USD");
        var sale = await SeedPendingSaleAsync(context);
        var service = CreateService(context);

        await service.CompleteSaleAsync(sale.Id, 50m, new List<PaymentInfo> { new(11, 100m, 5000m, null) });

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);
        Assert.Equal("USD", payment.CurrencyCode);
    }

    [Fact]
    public async Task Checkout_LegacyMethodWithoutPersistedCurrency_FallsBackToNameRule()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 12, "Zelle USD", currency: null);
        var sale = await SeedPendingSaleAsync(context);
        var service = CreateService(context);

        await service.CompleteSaleAsync(sale.Id, 50m, new List<PaymentInfo> { new(12, 100m, 5000m, null) });

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);
        Assert.Equal("USD", payment.CurrencyCode);
    }

    [Fact]
    public async Task HoldSale_InitialPayment_SnapshotsPersistedMethodCurrency()
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
        AddMethod(context, 13, "Pago Movil", "USD");
        var sale = await SeedPendingSaleAsync(context);
        var service = CreateService(context);

        await service.HoldSaleAsync(sale.Id, new HoldSaleRequestDto
        {
            CustomerId = 2,
            ExchangeRate = 50m,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = 13,
                AmountUSD = 40m,
                AmountBsS = 2000m,
                ExchangeRate = 50m
            }
        });

        var savedSale = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);

        Assert.Equal(SaleStatus.OnHold, savedSale.Status);
        Assert.Equal("USD", payment.CurrencyCode);
        Assert.Equal(40m, payment.Amount);
        Assert.Equal(2000m, payment.AmountBsS);
        Assert.Equal(50m, payment.ExchangeRate);
    }

    [Fact]
    public async Task AddPaymentToHoldSale_SnapshotsPersistedMethodCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 14, "Transferencia", "USD");

        var sale = new Sale
        {
            TotalUSD = 100m,
            Subtotal = 100m,
            AppliedRate = 50m,
            TotalBsS = 5000m,
            SubtotalBsS = 5000m,
            Status = SaleStatus.OnHold,
            ClaimedByUserId = 7,
            ClaimedByUserName = "Cajero"
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context);

        await service.AddPaymentToHoldSaleAsync(
            sale.Id,
            new AddPaymentRequestDto
            {
                PaymentMethodId = 14,
                AmountUSD = 10m,
                AmountBsS = 500m,
                ExchangeRate = 50m
            },
            actingUserId: 7);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);
        Assert.Equal("USD", payment.CurrencyCode);
        Assert.Equal(10m, payment.Amount);
        Assert.Equal(500m, payment.AmountBsS);
        Assert.Equal(50m, payment.ExchangeRate);
    }

    [Fact]
    public async Task AddPaymentsBatchToHoldSale_SnapshotsPersistedMethodCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 15, "Pago Movil", "USD");
        AddMethod(context, 16, "Punto de Venta", "Bs.S");

        var sale = new Sale
        {
            TotalUSD = 100m,
            Subtotal = 100m,
            AppliedRate = 50m,
            TotalBsS = 5000m,
            SubtotalBsS = 5000m,
            Status = SaleStatus.OnHold,
            ClaimedByUserId = 7,
            ClaimedByUserName = "Cajero"
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context);

        await service.AddPaymentsBatchToHoldSaleAsync(
            sale.Id,
            new List<AddPaymentRequestDto>
            {
                new() { PaymentMethodId = 15, AmountUSD = 10m, AmountBsS = 500m, ExchangeRate = 50m },
                new() { PaymentMethodId = 16, AmountBsS = 1000m, ExchangeRate = 50m }
            },
            actingUserId: 7);

        var payments = await context.SalePayments
            .AsNoTracking()
            .Where(p => p.SaleId == sale.Id)
            .OrderBy(p => p.PaymentMethodId)
            .ToListAsync();

        Assert.Equal(2, payments.Count);
        Assert.Equal("USD", payments[0].CurrencyCode);
        Assert.Equal("Bs.S", payments[1].CurrencyCode);
        Assert.Equal(500m, payments[0].AmountBsS);
        Assert.Equal(1000m, payments[1].AmountBsS);
    }

    [Fact]
    public async Task CashAdvance_SalePayment_SnapshotsPersistedMethodCurrencyAndKeepsTotals()
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
        AddMethod(context, 2, "Transferencia", "USD");
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

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == dto.Id);
        Assert.Equal("USD", payment.CurrencyCode);
        Assert.Equal(21.4m, payment.Amount);
        Assert.Equal(1070m, payment.AmountBsS);
        Assert.Equal(50m, payment.ExchangeRate);

        var savedSale = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == dto.Id);
        Assert.Equal(21.4m, savedSale.TotalUSD);
        Assert.Equal(1070m, savedSale.TotalBsS);
        Assert.Equal(50m, savedSale.AppliedRate);
    }

    [Fact]
    public async Task RenamingPaymentMethod_AfterCheckout_DoesNotChangeHistoricalCurrencyCode()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 17, "Punto de Venta", "USD");
        var sale = await SeedPendingSaleAsync(context);
        var service = CreateService(context);

        await service.CompleteSaleAsync(sale.Id, 50m, new List<PaymentInfo> { new(17, 100m, 5000m, null) });

        var method = await context.PaymentMethods.SingleAsync(pm => pm.Id == 17);
        method.Name = "Punto de Venta Bs.S";
        method.Currency = "Bs.S";
        await context.SaveChangesAsync();

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(p => p.SaleId == sale.Id);
        Assert.Equal("USD", payment.CurrencyCode);
    }

    [Fact]
    public async Task GetSaleAsync_ExposesStoredPaymentCurrencyCode()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 18, "Punto de Venta", "Bs.S");
        var sale = await SeedPendingSaleAsync(context);
        var service = CreateService(context);

        await service.CompleteSaleAsync(sale.Id, 50m, new List<PaymentInfo> { new(18, 100m, 5000m, null) });

        var dto = await service.GetSaleAsync(sale.Id);
        var payment = Assert.Single(dto.Payments);
        Assert.Equal("Bs.S", payment.CurrencyCode);
        Assert.Equal("Punto de Venta", payment.PaymentMethodName);
        Assert.Equal(100m, payment.Amount);
        Assert.Equal(5000m, payment.AmountBsS);
        Assert.Equal(50m, payment.ExchangeRate);
    }
}
