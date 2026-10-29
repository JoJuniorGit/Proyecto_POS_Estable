using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using CommandCenter.Tests.TestHelpers;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Receipts;
using Sales.Module.Services;
using Xunit;
using SalesService = Sales.Module.Services.SalesService;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// T3a (F2b, Paso 11/12, S3/S6/S7): abonos CxC FIFO/factura explícita con FX realizado,
/// autoridad de caja (solo lo cobrado entra a la gaveta), idempotencia del cobro, transiciones
/// de estado, estado de cuenta dual, comprobante de abono y cierre Z que incorpora los abonos.
/// </summary>
public class CreditPaymentTests
{
    // ---------------------------------------------------------------- FIFO / imputación

    [Fact]
    public async Task RegisterCreditPayment_Fifo_AllocatesOldestDueFirstAndContinuesToNext()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var older = await SeedReceivableAsync(context, customer.Id, balanceUsd: 40m, invoiceNumber: 9101, dueInDays: 5);
        var newer = await SeedReceivableAsync(context, customer.Id, balanceUsd: 60m, invoiceNumber: 9102, dueInDays: 20);
        await SeedPaymentMethodsAsync(context);
        var (service, _, _) = CreateHarness(context, bcvRate: 60m);

        var result = await service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 50m,
            PaymentMethodId = 1,
            ReferenceNumber = "REF-001"
        });

        Assert.Equal(2, result.Allocations.Count);

        var first = result.Allocations[0];
        Assert.Equal(older.Id, first.ReceivableId);
        Assert.Equal(40.0000m, first.AmountUSD);
        Assert.Equal(2400.00m, first.AmountBsS);        // 40 × 60
        Assert.Equal(400.00m, first.FxDifferenceBsS);   // 2400 − 40×50

        var second = result.Allocations[1];
        Assert.Equal(newer.Id, second.ReceivableId);
        Assert.Equal(10.0000m, second.AmountUSD);
        Assert.Equal(600.00m, second.AmountBsS);        // 10 × 60
        Assert.Equal(100.00m, second.FxDifferenceBsS);  // 600 − 10×50

        Assert.Equal(50.0000m, result.AmountUSD);
        Assert.Equal(3000.00m, result.AmountBsS);
        Assert.Equal(500.00m, result.FxDifferenceBsS);
        Assert.Equal(60.0000m, result.PaymentRate);
        Assert.False(result.IsReplay);

        var persistedOlder = await context.Receivables.AsNoTracking().FirstAsync(candidate => candidate.Id == older.Id);
        Assert.Equal(0m, persistedOlder.BalanceUSD);
        Assert.Equal(ReceivableStatus.Pagada, persistedOlder.Status);

        var persistedNewer = await context.Receivables.AsNoTracking().FirstAsync(candidate => candidate.Id == newer.Id);
        Assert.Equal(50.0000m, persistedNewer.BalanceUSD);
        Assert.Equal(ReceivableStatus.Parcial, persistedNewer.Status);

        var payment = await context.ReceivablePayments.AsNoTracking().SingleAsync();
        Assert.Equal(50.0000m, payment.AmountUSD);
        Assert.Equal(3000.00m, payment.AmountBsS);
        Assert.Equal(60.0000m, payment.PaymentRate);
        Assert.Equal(500.00m, payment.FxDifferenceBsS);
        Assert.Equal("REF-001", payment.ReferenceNumber);
        Assert.True((DateTime.UtcNow - payment.ReceivedAtUtc).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task RegisterCreditPayment_ExplicitIds_RespectsGivenOrderAndRejectsForeignOrClosedReceivables()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var other = await SeedCustomerAsync(context);
        var first = await SeedReceivableAsync(context, customer.Id, balanceUsd: 30m, invoiceNumber: 9201, dueInDays: 5);
        var second = await SeedReceivableAsync(context, customer.Id, balanceUsd: 30m, invoiceNumber: 9202, dueInDays: 20);
        var foreign = await SeedReceivableAsync(context, other.Id, balanceUsd: 30m, invoiceNumber: 9203, dueInDays: 5);
        var closed = await SeedReceivableAsync(context, customer.Id, balanceUsd: 0m, invoiceNumber: 9204, dueInDays: 5, status: ReceivableStatus.Pagada);

        await SeedPaymentMethodsAsync(context);
        var (service, _, _) = CreateHarness(context, bcvRate: 60m);

        var result = await service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 45m, // 30 salda la primera factura del orden informado; 15 avanza a la siguiente
            PaymentMethodId = 1,
            ReceivableIds = new List<int> { second.Id, first.Id }
        });

        Assert.Equal(new[] { second.Id, first.Id }, result.Allocations.Select(allocation => allocation.ReceivableId).ToArray());
        Assert.Equal(30.0000m, result.Allocations[0].AmountUSD);
        Assert.Equal(15.0000m, result.Allocations[1].AmountUSD);

        // Factura de otro cliente: rechazo sin movimiento.
        var foreignEx = await Assert.ThrowsAsync<ArgumentException>(() => service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 10m,
            PaymentMethodId = 1,
            ReceivableIds = new List<int> { foreign.Id }
        }));
        Assert.Contains("cuentas por cobrar", foreignEx.Message);

        // Factura ya pagada (no abierta): rechazo sin movimiento.
        var closedEx = await Assert.ThrowsAsync<ArgumentException>(() => service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 10m,
            PaymentMethodId = 1,
            ReceivableIds = new List<int> { closed.Id }
        }));
        Assert.Contains("cuentas por cobrar", closedEx.Message);

        // Imputación repetida: rechazo defensivo.
        var duplicateEx = await Assert.ThrowsAsync<ArgumentException>(() => service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 10m,
            PaymentMethodId = 1,
            ReceivableIds = new List<int> { first.Id, first.Id }
        }));
        Assert.Contains("repetidas", duplicateEx.Message);

        Assert.Equal(1, await context.ReceivablePayments.AsNoTracking().CountAsync());
        Assert.Equal(2, await context.CreditPaymentAllocations.AsNoTracking().CountAsync());
    }

    // ---------------------------------------------------------------- FX realizado

    [Fact]
    public async Task RegisterCreditPayment_RateDown_RecordsSignedLossWithServerSnapshotRate()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        await SeedReceivableAsync(context, customer.Id, balanceUsd: 100m, historicalRate: 50m, invoiceNumber: 9301);
        await SeedPaymentMethodsAsync(context);
        var (service, inventory, _) = CreateHarness(context, bcvRate: 40m);

        var result = await service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 100m,
            PaymentMethodId = 1
        });

        Assert.Equal(40.0000m, result.PaymentRate);
        Assert.Equal(4000.00m, result.AmountBsS);            // 100 × 40
        Assert.Equal(-1000.00m, result.FxDifferenceBsS);     // 4000 − 100×50 (pérdida con signo)

        // La tasa es server-side (fuente BCV cacheada); el request no expone tasa alguna.
        inventory.Verify(inventoryService => inventoryService.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RegisterCreditPayment_WithoutRegisteredBcvRate_IsRejectedWithoutMovement()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        await SeedReceivableAsync(context, customer.Id, balanceUsd: 100m, invoiceNumber: 9302);
        await SeedPaymentMethodsAsync(context);
        var (service, _, _) = CreateHarness(context, bcvRate: 0m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 10m,
            PaymentMethodId = 1
        }));

        Assert.Contains("tasa BCV", ex.Message);
        Assert.Equal(0, await context.ReceivablePayments.AsNoTracking().CountAsync());
        Assert.Equal(100.0000m, (await context.Receivables.AsNoTracking().SingleAsync()).BalanceUSD);
    }

    [Fact]
    public async Task RegisterCreditPayment_SingleProfile_UsesIdentityRateAndSkipsBcv()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        await SeedReceivableAsync(context, customer.Id, balanceUsd: 100m, historicalRate: 50m, invoiceNumber: 9303);
        await SeedPaymentMethodsAsync(context);

        var profile = new Mock<IMonetaryProfileService>();
        profile.Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "USD",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = null,
                SecondaryCurrencySymbol = null,
                IsConfigured = true
            });
        var (service, inventory, _) = CreateHarness(context, bcvRate: 60m, monetaryProfileService: profile.Object);

        var result = await service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 100m,
            PaymentMethodId = 1
        });

        Assert.Equal(1.0000m, result.PaymentRate);
        Assert.Equal(100.00m, result.AmountBsS);
        Assert.Equal(-4900.00m, result.FxDifferenceBsS); // 100 − 100×50
        inventory.Verify(inventoryService => inventoryService.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------- polvo decimal / topes

    [Fact]
    public async Task RegisterCreditPayment_PayingFullBsSEquivalence_ClampsDustAndMarksPagada()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        // Saldo derivado del Bs.S efectivo: 4999.96 / 50 = 99.9992 (4dp). Se paga 100.00:
        // el residuo de punto fijo (0.0008 ≤ 0.005) se absorbe y el saldo queda exactamente en 0.
        var receivable = await SeedReceivableAsync(context, customer.Id, balanceUsd: 99.9992m, historicalRate: 50m, invoiceNumber: 9401);
        await SeedPaymentMethodsAsync(context);
        var (service, _, _) = CreateHarness(context, bcvRate: 50m);

        var result = await service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 100m,
            PaymentMethodId = 1
        });

        Assert.Equal(99.9992m, result.AmountUSD);
        Assert.Equal(4999.96m, result.AmountBsS); // 99.9992 × 50
        Assert.Equal(0.0000m, result.Allocations.Single().BalanceAfterUSD);

        var persisted = await context.Receivables.AsNoTracking().SingleAsync(candidate => candidate.Id == receivable.Id);
        Assert.Equal(0m, persisted.BalanceUSD);
        Assert.Equal(ReceivableStatus.Pagada, persisted.Status);

        var allocation = await context.CreditPaymentAllocations.AsNoTracking().SingleAsync();
        Assert.Equal(99.9992m, allocation.AmountUSD);
    }

    [Fact]
    public async Task RegisterCreditPayment_OverpayBeyondDustTolerance_IsRejectedWithoutMovement()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        await SeedReceivableAsync(context, customer.Id, balanceUsd: 100m, invoiceNumber: 9402);
        await SeedPaymentMethodsAsync(context);
        var (service, _, _) = CreateHarness(context, bcvRate: 60m);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 100.01m, // 0.01 > 0.005 de tolerancia
            PaymentMethodId = 1
        }));

        Assert.Contains("excede", ex.Message);
        Assert.Equal(0, await context.ReceivablePayments.AsNoTracking().CountAsync());
        Assert.Equal(100.0000m, (await context.Receivables.AsNoTracking().SingleAsync()).BalanceUSD);
    }

    [Fact]
    public async Task RegisterCreditPayment_WithoutOpenReceivables_IsRejected()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        await SeedReceivableAsync(context, customer.Id, balanceUsd: 0m, invoiceNumber: 9501, status: ReceivableStatus.Pagada);
        await SeedPaymentMethodsAsync(context);
        var (service, _, _) = CreateHarness(context, bcvRate: 60m);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 10m,
            PaymentMethodId = 1
        }));

        Assert.Contains("saldo pendiente", ex.Message);
        Assert.Equal(0, await context.ReceivablePayments.AsNoTracking().CountAsync());
    }

    // ---------------------------------------------------------------- caja e idempotencia

    [Fact]
    public async Task RegisterCreditPayment_CashMethod_RegistersIncomeOnlyForCollectedMoney_WithCreditPaymentSource()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        await SeedPaymentMethodsAsync(context);
        var first = await SeedReceivableAsync(context, customer.Id, balanceUsd: 20m, invoiceNumber: 9601);
        var (service, _, cashDrawer) = CreateHarness(context, bcvRate: 60m);

        var result = await service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 20m,
            PaymentMethodId = 1
        });

        var cash = await context.CashTransactions.AsNoTracking().SingleAsync();
        Assert.Equal(CashTransactionType.Income, cash.Type);
        Assert.Equal(CashTransactionSource.CreditPayment, cash.Source);
        Assert.Equal(1, cash.SessionId);
        Assert.Equal(20m, cash.AmountUsd);
        Assert.Equal(1200.00m, cash.AmountLocal); // 20 × 60
        Assert.True(cash.IsPhysicalCash);
        Assert.Equal(1, cash.PaymentMethodId);
        Assert.Equal(cash.Id, result.CashTransactionId);
        Assert.Equal(ReceivableStatus.Pagada, (await context.Receivables.AsNoTracking().SingleAsync(candidate => candidate.Id == first.Id)).Status);
        cashDrawer.Verify(drawer => drawer.GetOrCreateActiveSessionAsync(60m, It.IsAny<CancellationToken>()), Times.Once);

        // Método NO cash: el abono no toca la gaveta (solo lo cobrado en efectivo entra).
        await SeedReceivableAsync(context, customer.Id, balanceUsd: 15m, invoiceNumber: 9602);
        await service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 15m,
            PaymentMethodId = 2
        });

        Assert.Equal(1, await context.CashTransactions.AsNoTracking().CountAsync());
        Assert.Equal(2, await context.ReceivablePayments.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task RegisterCreditPayment_IdempotentReplay_ReturnsExistingPaymentWithoutNewCashOrAllocations()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        await SeedPaymentMethodsAsync(context);
        await SeedReceivableAsync(context, customer.Id, balanceUsd: 20m, invoiceNumber: 9701);
        var (service, _, _) = CreateHarness(context, bcvRate: 60m);

        var request = new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 20m,
            PaymentMethodId = 1,
            IdempotencyKey = "cxc-idem-1"
        };

        var first = await service.RegisterCreditPaymentAsync(request);
        // El reintento con la misma clave replica el cobro aunque la deuda ya esté saldada.
        var replay = await service.RegisterCreditPaymentAsync(request);

        Assert.False(first.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.PaymentId, replay.PaymentId);
        Assert.Equal(1, await context.ReceivablePayments.AsNoTracking().CountAsync());
        Assert.Equal(1, await context.CashTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await context.CreditPaymentAllocations.AsNoTracking().CountAsync());
    }

    // ---------------------------------------------------------------- transiciones de estado

    [Fact]
    public async Task RegisterCreditPayment_StatusTransitions_ParcialVencidaAndPagada()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, graceDays: 0);
        var current = await SeedReceivableAsync(context, customer.Id, balanceUsd: 50m, invoiceNumber: 9801, dueInDays: 10);
        var overdue = await SeedReceivableAsync(context, customer.Id, balanceUsd: 50m, invoiceNumber: 9802, dueInDays: -5);
        await SeedPaymentMethodsAsync(context);
        var (service, _, _) = CreateHarness(context, bcvRate: 60m);

        // Vigente → Parcial (factura corriente, imputación explícita).
        await service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 10m,
            PaymentMethodId = 1,
            ReceivableIds = new List<int> { current.Id }
        });
        Assert.Equal(ReceivableStatus.Parcial, (await context.Receivables.AsNoTracking().FirstAsync(r => r.Id == current.Id)).Status);

        // Vencida: factura fuera del período de gracia con abono parcial.
        await service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 10m,
            PaymentMethodId = 1,
            ReceivableIds = new List<int> { overdue.Id }
        });
        Assert.Equal(ReceivableStatus.Vencida, (await context.Receivables.AsNoTracking().FirstAsync(r => r.Id == overdue.Id)).Status);

        // → Pagada al saldar el resto.
        await service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 40m,
            PaymentMethodId = 1,
            ReceivableIds = new List<int> { current.Id }
        });
        var settled = await context.Receivables.AsNoTracking().FirstAsync(r => r.Id == current.Id);
        Assert.Equal(0m, settled.BalanceUSD);
        Assert.Equal(ReceivableStatus.Pagada, settled.Status);
    }

    // ---------------------------------------------------------------- estado de cuenta

    [Fact]
    public async Task GetCreditStatement_ReturnsProfileReceivablesPaymentsAndTodayRate()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, graceDays: 0);
        var current = await SeedReceivableAsync(context, customer.Id, balanceUsd: 100m, historicalRate: 50m, invoiceNumber: 9901, dueInDays: 10);
        var overdue = await SeedReceivableAsync(context, customer.Id, balanceUsd: 25m, historicalRate: 50m, invoiceNumber: 9902, dueInDays: -5);
        await SeedPaymentMethodsAsync(context);

        var (service, _, _) = CreateHarness(context, bcvRate: 60m);
        await service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 40m,
            PaymentMethodId = 1,
            ReceivableIds = new List<int> { current.Id }
        });

        var rateProvider = new Mock<ITodayExchangeRateProvider>();
        rateProvider.Setup(provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(55m);
        var creditService = new CreditService(context, rateProvider.Object);

        var statement = await creditService.GetCreditStatementAsync(customer.Id);

        Assert.Equal(55m, statement.TodayRate);
        Assert.Equal(85.0000m, statement.Profile.CurrentDebtUSD); // 60 + 25
        Assert.Equal(4675.00m, statement.CurrentDebtTodayBsS);   // 85 × 55

        var line = statement.Receivables.Single(receivable => receivable.ReceivableId == current.Id);
        Assert.Equal(9901, line.InvoiceNumber);
        Assert.Equal(100.0000m, line.OriginalUSD);
        Assert.Equal(60.0000m, line.BalanceUSD);
        Assert.Equal(5000.00m, line.OriginalBsS);
        Assert.Equal(50.0000m, line.HistoricalRate);
        Assert.Equal(3300.00m, line.BalanceTodayBsS); // 60 × 55
        Assert.Equal("Parcial", line.Status);
        Assert.False(line.IsOverdue);

        var overdueLine = statement.Receivables.Single(receivable => receivable.ReceivableId == overdue.Id);
        Assert.Equal("Vigente", overdueLine.Status);
        Assert.True(overdueLine.IsOverdue); // fuera del período de gracia (hoy Venezuela)
        Assert.Equal(1375.00m, overdueLine.BalanceTodayBsS); // 25 × 55

        var payment = Assert.Single(statement.Payments);
        Assert.Equal(40.0000m, payment.AmountUSD);
        Assert.Equal(2400.00m, payment.AmountBsS);
        Assert.Equal(60.0000m, payment.PaymentRate);
        Assert.Equal(400.00m, payment.FxDifferenceBsS);
        Assert.Equal("Efectivo USD", payment.PaymentMethodName);
        Assert.True((DateTime.UtcNow - payment.ReceivedAtUtc).Duration() < TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => creditService.GetCreditStatementAsync(999999));
    }

    // ---------------------------------------------------------------- cierre Z

    [Fact]
    public async Task GetExpectedTotalsByPaymentMethod_IncludesCreditPaymentsReceivedInWindow()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        var customer = await SeedCustomerAsync(context);
        await SeedReceivableAsync(context, customer.Id, balanceUsd: 25m, invoiceNumber: 9950);
        var (service, _, _) = CreateHarness(context, bcvRate: 60m);
        var closureService = DailyClosureTestHelper.CreateService(context);

        var before = await closureService.GetExpectedTotalsByPaymentMethodAsync(DateTime.UtcNow);
        Assert.All(before, total => Assert.Equal(0m, total.ExpectedAmountBsS));

        await service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 25m,
            PaymentMethodId = 3 // no-cash: computa en lo esperado del método sin volver a contarlo en gaveta
        });

        var after = await closureService.GetExpectedTotalsByPaymentMethodAsync(DateTime.UtcNow);
        Assert.Equal(1500.00m, after.Single(total => total.PaymentMethodId == 3).ExpectedAmountBsS);
        Assert.All(after.Where(total => total.PaymentMethodId != 3), total => Assert.Equal(0m, total.ExpectedAmountBsS));

        // Un abono fuera de la ventana del cierre no computa (ventana = jornada corriente).
        context.ReceivablePayments.Add(new ReceivablePayment
        {
            CustomerId = customer.Id,
            AmountUSD = 10m,
            AmountBsS = 500m,
            PaymentRate = 50m,
            FxDifferenceBsS = 0m,
            PaymentMethodId = 4,
            ReceivedAtUtc = DateTime.UtcNow.AddDays(-2)
        });
        await context.SaveChangesAsync();

        var afterStale = await closureService.GetExpectedTotalsByPaymentMethodAsync(DateTime.UtcNow);
        Assert.Equal(0m, afterStale.Single(total => total.PaymentMethodId == 4).ExpectedAmountBsS);
        Assert.Equal(1500.00m, afterStale.Single(total => total.PaymentMethodId == 3).ExpectedAmountBsS);
    }

    // ---------------------------------------------------------------- comprobante de abono

    [Fact]
    public async Task CreditPaymentReceipt_ServiceBuildsContextAndGeneratorProducesPdf()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        await SeedPaymentMethodsAsync(context);
        var current = await SeedReceivableAsync(context, customer.Id, balanceUsd: 100m, historicalRate: 50m, invoiceNumber: 9960, dueInDays: 10);
        await SeedReceivableAsync(context, customer.Id, balanceUsd: 50m, historicalRate: 50m, invoiceNumber: 9961, dueInDays: 20);
        var (service, _, _) = CreateHarness(context, bcvRate: 60m);

        var payment = await service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 40m,
            PaymentMethodId = 1,
            ReceivableIds = new List<int> { current.Id },
            ReferenceNumber = "TRF-7788"
        });

        var receiptService = new SalesReceiptService(context);
        var data = await receiptService.GetCreditPaymentReceiptDataAsync(payment.PaymentId, todayRate: 55m);
        Assert.NotNull(data);

        var receipt = data!.Context;
        Assert.Equal(payment.PaymentId, receipt.PaymentId);
        Assert.Equal(customer.Name, receipt.CustomerName);
        Assert.Equal(60.0000m, receipt.PaymentRate);
        Assert.Equal(55m, receipt.TodayRate);
        Assert.Equal("Efectivo USD", receipt.PaymentMethodName);
        Assert.Equal("TRF-7788", receipt.ReferenceNumber);
        Assert.Equal(40.0000m, receipt.TotalAmountUSD);
        Assert.Equal(2400.00m, receipt.TotalAmountBsS);
        Assert.Equal(400.00m, receipt.FxDifferenceBsS);
        Assert.Equal(110.0000m, receipt.CurrentDebtUSD);        // 60 + 50 pendientes hoy
        Assert.Equal(6050.00m, receipt.CurrentDebtTodayBsS);    // 110 × 55

        var line = Assert.Single(receipt.Lines);
        Assert.Equal(9960, line.InvoiceNumber);
        Assert.Equal(100.0000m, line.BalanceBeforeUSD);
        Assert.Equal(40.0000m, line.AmountUSD);
        Assert.Equal(60.0000m, line.BalanceAfterUSD);

        var bytes = CreditPaymentReceiptPdfGenerator.BuildPdf(receipt, MoneyDisplayFormat.Venezuelan);
        Assert.NotEmpty(bytes);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        var content = Encoding.ASCII.GetString(bytes);
        Assert.Contains("COMPROBANTE DE ABONO", content);
        Assert.Contains("2.400,00", content); // monto abonado en Bs.S (formato Venezuela)
    }

    [Fact]
    public async Task GetCreditPaymentReceipt_Controller_OwnCashierSeesPdf_OtherCashierForbidden_ElevatedBypass()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        await SeedReceivableAsync(context, customer.Id, balanceUsd: 50m, invoiceNumber: 9970);
        await SeedPaymentMethodsAsync(context);
        var (service, _, _) = CreateHarness(context, bcvRate: 60m);

        var payment = await service.RegisterCreditPaymentAsync(
            new CreditPaymentRequest { CustomerId = customer.Id, AmountUSD = 50m, PaymentMethodId = 1 },
            actingUserId: 7);

        var rateProvider = new Mock<ITodayExchangeRateProvider>();
        rateProvider.Setup(provider => provider.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(55m);

        // Cajero dueño del cobro: descarga.
        var owner = CreateReceiptsController(context, "7", "Cashier");
        var ownerResult = await owner.GetCreditPaymentReceiptAsync(payment.PaymentId, rateProvider.Object, CancellationToken.None);
        var file = Assert.IsType<FileContentResult>(ownerResult);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.StartsWith("Comprobante_Abono_", file.FileDownloadName);
        Assert.NotNull(file.FileContents);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(file.FileContents!, 0, 5));

        // Otro cajero: 403.
        var intruder = CreateReceiptsController(context, "8", "Cashier");
        var forbidden = await intruder.GetCreditPaymentReceiptAsync(payment.PaymentId, rateProvider.Object, CancellationToken.None);
        Assert.IsType<ObjectResult>(forbidden);
        Assert.Equal(403, ((ObjectResult)forbidden).StatusCode);

        // Elevado (Admin): bypass.
        var admin = CreateReceiptsController(context, "99", "Admin");
        Assert.IsType<FileContentResult>(await admin.GetCreditPaymentReceiptAsync(payment.PaymentId, rateProvider.Object, CancellationToken.None));

        // Abono inexistente: 404.
        var controller = CreateReceiptsController(context, "7", "Cashier");
        var missing = await controller.GetCreditPaymentReceiptAsync(999999, rateProvider.Object, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(missing);
    }

    // ---------------------------------------------------------------- endpoint: receptor efectivo

    [Fact]
    public async Task RegisterCreditPayment_Endpoint_OverridesReceiverWithAuthenticatedUser()
    {
        CreditPaymentRequest? captured = null;
        var salesService = new Mock<ISalesService>();
        salesService
            .Setup(service => service.RegisterCreditPaymentAsync(It.IsAny<CreditPaymentRequest>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<CreditPaymentRequest, int?, CancellationToken>((request, _, _) => captured = request)
            .ReturnsAsync(new CreditPaymentResultDto { PaymentId = 7 });

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(user => user.UserId).Returns("12");

        var controller = new SalesController(salesService.Object, currentUser.Object);
        AttachUser(controller, "12", "Cashier");

        var result = await controller.RegisterCreditPaymentAsync(
            new CreditPaymentRequest { CustomerId = 3, AmountUSD = 10m, PaymentMethodId = 1, ReceivedByUserId = 99 },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(captured);
        Assert.Equal(12, captured!.ReceivedByUserId); // el usuario autenticado prevalece
    }

    // ---------------------------------------------------------------- harness

    private static (SalesService Service, Mock<IInventoryService> Inventory, Mock<ICashDrawerService> CashDrawer) CreateHarness(
        SalesDbContext context,
        decimal bcvRate = 60m,
        IMonetaryProfileService? monetaryProfileService = null,
        ICurrencyRateService? currencyRateService = null)
    {
        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(inventoryService => inventoryService.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(bcvRate);

        var mediator = new Mock<IMediator>();
        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer
            .Setup(drawer => drawer.GetOrCreateActiveSessionAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });
        var settings = new Mock<ISystemSettingsService>();

        var service = new SalesService(
            context,
            inventory.Object,
            mediator.Object,
            cashDrawer.Object,
            settings.Object,
            creditService: new CreditService(context),
            monetaryProfileService: monetaryProfileService,
            currencyRateService: currencyRateService);

        return (service, inventory, cashDrawer);
    }

    private static ReceiptsController CreateReceiptsController(SalesDbContext context, string userId, string role)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(service => service.UserId).Returns(userId);

        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(service => service.GetSettingAsync("CurrencyFormat")).ReturnsAsync("Venezuelan");

        var controller = new ReceiptsController(new SalesReceiptService(context), new SaleReceiptRenderer(), currentUser.Object, settings.Object);
        AttachUser(controller, userId, role);
        return controller;
    }

    private static void AttachUser(ControllerBase controller, string userId, string role)
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, userId),
            new(System.Security.Claims.ClaimTypes.Role, role)
        };
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "mock"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = user }
        };
        // Contrato endurecido: el abono CxC exige Idempotency-Key.
        controller.ControllerContext.HttpContext.Request.Headers["Idempotency-Key"] =
            $"test-credit-pay-{Guid.NewGuid():N}";
    }

    private static async Task<Customer> SeedCustomerAsync(SalesDbContext context, int graceDays = 0)
    {
        var customer = new Customer
        {
            CedulaOrRif = "V-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Cliente Abonos CxC",
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

    private static async Task<Receivable> SeedReceivableAsync(
        SalesDbContext context,
        int customerId,
        decimal balanceUsd,
        decimal historicalRate = 50m,
        int dueInDays = 10,
        int issuedDaysAgo = 0,
        ReceivableStatus status = ReceivableStatus.Vigente,
        int invoiceNumber = 9001)
    {
        var sale = new Sale
        {
            CustomerId = customerId,
            Status = SaleStatus.Completed,
            InvoiceNumber = invoiceNumber,
            Date = DateTime.UtcNow.AddDays(-issuedDaysAgo),
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
            IssuedAtUtc = DateTime.UtcNow.AddDays(-issuedDaysAgo),
            DueDateUtc = DateTime.UtcNow.AddDays(dueInDays),
            Status = status
        };
        context.Receivables.Add(receivable);
        await context.SaveChangesAsync();
        return receivable;
    }

    private static async Task SeedPaymentMethodsAsync(SalesDbContext context)
    {
        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true, IsActive = true, DisplayOrder = 1 },
            new PaymentMethod { Id = 2, Name = "Punto de Venta", IsCash = false, IsActive = true, DisplayOrder = 2 });
        await context.SaveChangesAsync();
    }
}
