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
using CashDrawerStatus = Sales.Module.Entities.CashDrawerStatus;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 14 (F1-WU4, S7): wiring del snapshot de comision en cada camino de creacion de pagos.
/// La comision es solo un costo del negocio: estos tests verifican que se persiste sin alterar
/// los montos cobrados, la tasa del pago ni los totales de la venta.
/// </summary>
public class PaymentMethodFeeSnapshotTests
{
    private const int CardMethodId = 10;
    private const int CashMethodId = 11;

    [Fact]
    public async Task CompleteSale_CardPayment_PersistsFeeSnapshotWithoutChangingTotals()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        var rule = await SeedRuleAsync(context, CardMethodId, percentage: 1m);

        var sale = new Sale
        {
            Id = 1,
            TotalUSD = 100m,
            Subtotal = 100m,
            AppliedRate = 50m,
            TotalBsS = 5000m,
            SubtotalBsS = 5000m,
            Status = SaleStatus.Pending
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.CompleteSaleAsync(
            sale.Id,
            50m,
            new[] { new PaymentInfo(CardMethodId, 100m, 5000m, null) });

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(candidate => candidate.SaleId == sale.Id);
        Assert.Equal(rule.Id, payment.PaymentMethodFeeRuleId);
        Assert.Equal(PaymentMethodFeeType.Percentage, payment.FeeType);
        Assert.Equal(1m, payment.FeePercentage);
        Assert.Equal("VES", payment.FeeCurrencyCode);
        Assert.Equal(50.00m, payment.FeeAmountLocal);
        Assert.Equal(1.0000m, payment.FeeAmountUsd);
        Assert.Equal(50m, payment.FeeRate);
        Assert.True(payment.FeeConfigured);
        Assert.Equal(rule.EffectiveFromUtc, payment.FeeRuleEffectiveFromUtc);
        Assert.Equal(100m, payment.Amount);
        Assert.Equal(5000m, payment.AmountBsS);

        var persistedSale = await context.Sales.AsNoTracking().SingleAsync(candidate => candidate.Id == sale.Id);
        Assert.Equal(100m, persistedSale.TotalUSD);
        Assert.Equal(5000m, persistedSale.TotalBsS);
        Assert.Equal(5000m, persistedSale.FinalPaidAmountBsS);
    }

    [Fact]
    public async Task CompleteSale_CashPayment_HasNoFeeEvenWithConfiguredRule()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CashMethodId, isCash: true);
        await SeedRuleAsync(context, CashMethodId, percentage: 1m);

        var sale = new Sale
        {
            Id = 2,
            TotalUSD = 100m,
            Subtotal = 100m,
            AppliedRate = 50m,
            TotalBsS = 5000m,
            SubtotalBsS = 5000m,
            Status = SaleStatus.Pending
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.CompleteSaleAsync(
            sale.Id,
            50m,
            new[] { new PaymentInfo(CashMethodId, 0m, 5000m, null) });

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(candidate => candidate.SaleId == sale.Id);
        Assert.False(payment.FeeConfigured);
        Assert.Equal(0m, payment.FeeAmountUsd);
        Assert.Equal(0m, payment.FeeAmountLocal);
        Assert.Null(payment.PaymentMethodFeeRuleId);
        Assert.Equal(5000m, payment.AmountBsS);
    }

    [Fact]
    public async Task HoldSale_InitialPayment_PersistsFeeSnapshot()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        var rule = await SeedRuleAsync(context, CardMethodId, percentage: 2m);

        context.Customers.Add(new Customer
        {
            Id = 1,
            CedulaOrRif = "V-12345678",
            Name = "Juan Perez",
            IsActive = true
        });
        context.Sales.Add(new Sale
        {
            Id = 1,
            TotalUSD = 100m,
            Status = SaleStatus.Pending
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var result = await service.HoldSaleAsync(1, new HoldSaleRequestDto
        {
            CustomerId = 1,
            ExchangeRate = 50m,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = CardMethodId,
                AmountUSD = 20m,
                AmountBsS = 1000m,
                ExchangeRate = 50m
            }
        });

        Assert.Equal("OnHold", result.Status);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(candidate => candidate.SaleId == 1);
        Assert.Equal(rule.Id, payment.PaymentMethodFeeRuleId);
        Assert.Equal(PaymentMethodFeeType.Percentage, payment.FeeType);
        Assert.Equal("VES", payment.FeeCurrencyCode);
        Assert.Equal(20.00m, payment.FeeAmountLocal);
        Assert.Equal(0.4000m, payment.FeeAmountUsd);
        Assert.Equal(50m, payment.FeeRate);
        Assert.True(payment.FeeConfigured);
        Assert.Equal(20m, payment.Amount);
        Assert.Equal(1000m, payment.AmountBsS);
    }

    [Fact]
    public async Task AddPaymentToHoldSale_PersistsFeeSnapshot()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        var rule = await SeedRuleAsync(context, CardMethodId, percentage: 1m);
        context.Sales.Add(new Sale
        {
            Id = 3,
            TotalUSD = 100m,
            TotalBsS = 5000m,
            AppliedRate = 50m,
            Status = SaleStatus.OnHold,
            ClaimedByUserId = 42,
            ClaimedByUserName = "Cajero",
            ClaimAction = SaleClaimAction.Editing,
            ClaimedAtUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.AddPaymentToHoldSaleAsync(3, new AddPaymentRequestDto
        {
            PaymentMethodId = CardMethodId,
            AmountUSD = 10m,
            AmountBsS = 500m,
            ExchangeRate = 50m
        }, actingUserId: 42);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(candidate => candidate.SaleId == 3);
        Assert.Equal(rule.Id, payment.PaymentMethodFeeRuleId);
        Assert.Equal("VES", payment.FeeCurrencyCode);
        Assert.Equal(5.00m, payment.FeeAmountLocal);
        Assert.Equal(0.1000m, payment.FeeAmountUsd);
        Assert.True(payment.FeeConfigured);
        Assert.Equal(10m, payment.Amount);
        Assert.Equal(500m, payment.AmountBsS);
    }

    [Fact]
    public async Task AddPaymentsBatchToHoldSale_PersistsFeePerPaymentAndSkipsCash()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        await SeedMethodAsync(context, CashMethodId, isCash: true);
        var rule = await SeedRuleAsync(context, CardMethodId, percentage: 1m);
        await SeedRuleAsync(context, CashMethodId, percentage: 1m);
        context.Sales.Add(new Sale
        {
            Id = 4,
            TotalUSD = 100m,
            TotalBsS = 5000m,
            AppliedRate = 50m,
            Status = SaleStatus.OnHold,
            ClaimedByUserId = 42,
            ClaimedByUserName = "Cajero",
            ClaimAction = SaleClaimAction.Editing,
            ClaimedAtUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.AddPaymentsBatchToHoldSaleAsync(4, new List<AddPaymentRequestDto>
        {
            new()
            {
                PaymentMethodId = CardMethodId,
                AmountUSD = 10m,
                AmountBsS = 500m,
                ExchangeRate = 50m
            },
            new()
            {
                PaymentMethodId = CashMethodId,
                AmountUSD = 10m,
                AmountBsS = 500m,
                ExchangeRate = 50m
            }
        }, actingUserId: 42);

        var payments = await context.SalePayments.AsNoTracking()
            .Where(candidate => candidate.SaleId == 4)
            .ToListAsync();
        Assert.Equal(2, payments.Count);

        var cardPayment = Assert.Single(payments, candidate => candidate.PaymentMethodId == CardMethodId);
        Assert.Equal(rule.Id, cardPayment.PaymentMethodFeeRuleId);
        Assert.Equal(5.00m, cardPayment.FeeAmountLocal);
        Assert.Equal(0.1000m, cardPayment.FeeAmountUsd);
        Assert.True(cardPayment.FeeConfigured);

        var cashPayment = Assert.Single(payments, candidate => candidate.PaymentMethodId == CashMethodId);
        Assert.False(cashPayment.FeeConfigured);
        Assert.Equal(0m, cashPayment.FeeAmountUsd);
        Assert.Equal(0m, cashPayment.FeeAmountLocal);
    }

    [Fact]
    public async Task CreateCashAdvanceSale_ChargesFeeOnTotalCharged()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        var rule = await SeedRuleAsync(context, CardMethodId, percentage: 1m);

        var service = CreateService(context);
        var sale = await service.CreateCashAdvanceSaleAsync(
            requestedAmountLocal: 1000m,
            commissionAmountLocal: 70m,
            paymentMethodId: CardMethodId,
            paymentMethodName: "Punto de Venta",
            isTransfer: true,
            exchangeRate: 50m);

        var payment = await context.SalePayments.AsNoTracking().SingleAsync(candidate => candidate.SaleId == sale.Id);
        Assert.Equal(rule.Id, payment.PaymentMethodFeeRuleId);
        Assert.Equal("VES", payment.FeeCurrencyCode);
        // 1% sobre el total cobrado (1000 capital + 70 comision de servicio) = 10.70 local.
        Assert.Equal(10.70m, payment.FeeAmountLocal);
        Assert.Equal(0.2140m, payment.FeeAmountUsd);
        Assert.Equal(50m, payment.FeeRate);
        Assert.True(payment.FeeConfigured);
        Assert.Equal(1070m, payment.AmountBsS);
        Assert.Equal(21.4m, payment.Amount);
    }

    [Fact]
    public async Task RegisterCreditPayment_PersistsFeeSnapshotWithoutChangingAllocatedAmounts()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await SeedMethodAsync(context, CardMethodId, isCash: false);
        var rule = await SeedRuleAsync(context, CardMethodId, percentage: 1m);

        var customer = new Customer
        {
            Id = 7,
            CedulaOrRif = "V-70000007",
            Name = "Cliente Credito",
            IsActive = true
        };
        var sale = new Sale
        {
            Id = 70,
            CustomerId = 7,
            TotalUSD = 100m,
            TotalBsS = 5000m,
            AppliedRate = 50m,
            Status = SaleStatus.Completed,
            InvoiceNumber = 700
        };
        context.Customers.Add(customer);
        context.Sales.Add(sale);
        context.Receivables.Add(new Receivable
        {
            Id = 700,
            SaleId = 70,
            CustomerId = 7,
            OriginalUSD = 100m,
            BalanceUSD = 100m,
            OriginalBsS = 5000m,
            HistoricalRate = 50m,
            IssuedAtUtc = DateTime.UtcNow.AddDays(-10),
            DueDateUtc = DateTime.UtcNow.AddDays(20),
            Status = ReceivableStatus.Vigente
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = 7,
            AmountUSD = 100m,
            PaymentMethodId = CardMethodId
        });

        var payment = await context.ReceivablePayments.AsNoTracking()
            .SingleAsync(candidate => candidate.CustomerId == 7);
        Assert.Equal(rule.Id, payment.PaymentMethodFeeRuleId);
        Assert.Equal("VES", payment.FeeCurrencyCode);
        Assert.Equal(50.00m, payment.FeeAmountLocal);
        Assert.Equal(1.0000m, payment.FeeAmountUsd);
        Assert.Equal(50m, payment.FeeRate);
        Assert.True(payment.FeeConfigured);
        Assert.Equal(100m, payment.AmountUSD);
        Assert.Equal(5000m, payment.AmountBsS);
    }

    private static SalesService CreateService(SalesDbContext context)
    {
        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(service => service.GetCashAdvanceProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaleProductInfoDto { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });
        inventory
            .Setup(service => service.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(50m);

        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer
            .Setup(service => service.GetOrCreateActiveSessionAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });
        cashDrawer
            .Setup(service => service.LockOpenSessionForWriteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        return new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            cashDrawer.Object,
            new Mock<ISystemSettingsService>().Object);
    }

    private static async Task SeedMethodAsync(SalesDbContext context, int id, bool isCash)
    {
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = id,
            Name = isCash ? $"Efectivo {id}" : $"Punto de Venta {id}",
            IsCash = isCash,
            IsActive = true,
            IsDeleted = false,
            Currency = "Bs.S",
            DisplayOrder = id
        });
        await context.SaveChangesAsync();
    }

    private static async Task<PaymentMethodFeeRule> SeedRuleAsync(
        SalesDbContext context,
        int paymentMethodId,
        decimal percentage)
    {
        var rule = new PaymentMethodFeeRule
        {
            PaymentMethodId = paymentMethodId,
            FeeType = PaymentMethodFeeType.Percentage,
            Percentage = percentage,
            CurrencyCode = "VES",
            EffectiveFromUtc = DateTime.UtcNow.AddDays(-1),
            IsActive = true
        };
        context.PaymentMethodFeeRules.Add(rule);
        await context.SaveChangesAsync();
        return rule;
    }
}
