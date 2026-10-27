using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
using CashDrawerStatus = Sales.Module.Entities.CashDrawerStatus;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T3b3 (S6): snapshot aditivo de la moneda FISICA de la gaveta en cada CashTransaction.
/// La moneda se resuelve una sola vez en el punto de escritura: efectivo de venta acreditado,
/// abono CxC y reembolso usan la moneda persistida del metodo (nombre solo como fallback legacy);
/// apertura, cierre, vuelto, adelanto, ajustes y cash-in/out usan la moneda local; caja chica y su
/// reversa usan la moneda del gasto. Montos, saldos y matematicas de arqueo no cambian.
/// </summary>
public class CashTransactionCurrencySnapshotTests
{
    private static void AddMethod(SalesDbContext context, int id, string name, string? currency, bool isCash)
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

    private static SalesService CreateSalesService(SalesDbContext context)
    {
        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(i => i.GetCashAdvanceProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaleProductInfoDto { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });
        inventory
            .Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(50m);

        var cashDrawer = new Mock<ICashDrawerService>();
        cashDrawer
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });
        // El reembolso fisico valida liquidez en la gaveta de su moneda: se simula fondo suficiente.
        cashDrawer
            .Setup(c => c.GetPhysicalBalanceAsync(It.IsAny<int>(), It.IsAny<PettyCashCurrency>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1_000_000m);

        return new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            cashDrawer.Object,
            new Mock<ISystemSettingsService>().Object,
            creditService: new CreditService(context));
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

    // ------------------------------------------------------------------ drawer movements

    [Fact]
    public async Task OpeningAndClosing_SnapshotLocalCurrencyAndKeepAmounts()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new CashDrawerService(context);

        var opened = await service.OpenSessionAsync(500m, 50m);
        var closed = await service.CloseSessionAsync(500m, 50m);

        var transactions = await context.CashTransactions.AsNoTracking().OrderBy(t => t.Id).ToListAsync();

        Assert.Equal(2, transactions.Count);
        Assert.Equal(CashTransactionSource.Opening, transactions[0].Source);
        Assert.Equal("Bs.S", transactions[0].CurrencyCode);
        Assert.Equal(500m, transactions[0].AmountLocal);
        Assert.Equal(CashTransactionSource.Closing, transactions[1].Source);
        Assert.Equal("Bs.S", transactions[1].CurrencyCode);
        Assert.Equal(500m, transactions[1].AmountLocal);
        Assert.Equal(500m, closed.ClosingBalanceLocal);
        Assert.Equal(opened.Id, closed.Id);
    }

    [Fact]
    public async Task ManualCashInAndCashOut_SnapshotLocalCurrencyAndKeepBalance()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new CashDrawerService(context);
        var session = await service.OpenSessionAsync(1000m, 50m);

        var cashIn = await service.AddTransactionAsync(
            session.Id, CashTransactionType.Income, CashTransactionSource.CashIn, 100m, 2m, 50m, "Ingreso manual");
        var cashOut = await service.AddTransactionAsync(
            session.Id, CashTransactionType.Expense, CashTransactionSource.CashOut, 50m, 1m, 50m, "Retiro manual");

        Assert.Equal("Bs.S", cashIn.CurrencyCode);
        Assert.Equal("Bs.S", cashOut.CurrencyCode);
        Assert.Equal(100m, cashIn.AmountLocal);
        Assert.Equal(50m, cashOut.AmountLocal);
        Assert.Equal(1050m, await service.GetCurrentBalanceLocalAsync(session.Id));
    }

    [Fact]
    public async Task AddTransaction_SalePaymentIncome_UsesPersistedUsdMethodCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 20, "Efectivo USD", "USD", isCash: true);
        await context.SaveChangesAsync();
        var service = new CashDrawerService(context);
        var session = await service.OpenSessionAsync(1000m, 50m);

        var dto = await service.AddTransactionAsync(
            session.Id,
            CashTransactionType.Income,
            CashTransactionSource.SalePayment,
            500m,
            10m,
            50m,
            "Venta efectivo USD",
            paymentMethodId: 20);

        Assert.Equal("USD", dto.CurrencyCode);
        var persisted = await context.CashTransactions.AsNoTracking().SingleAsync(t => t.Id == dto.Id);
        Assert.Equal("USD", persisted.CurrencyCode);
        Assert.Equal(500m, persisted.AmountLocal);
        Assert.Equal(10m, persisted.AmountUsd);
        Assert.Equal(50m, persisted.ExchangeRate);
    }

    [Fact]
    public async Task AddTransaction_SalePaymentIncome_PersistedLocalCurrencyBeatsUsdLikeName()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 21, "Efectivo USD", "Bs.S", isCash: true);
        await context.SaveChangesAsync();
        var service = new CashDrawerService(context);
        var session = await service.OpenSessionAsync(1000m, 50m);

        var dto = await service.AddTransactionAsync(
            session.Id,
            CashTransactionType.Income,
            CashTransactionSource.SalePayment,
            500m,
            10m,
            50m,
            "Venta efectivo local",
            paymentMethodId: 21);

        Assert.Equal("Bs.S", dto.CurrencyCode);
    }

    [Fact]
    public async Task AddTransaction_SalePaymentExpenseWithUsdMethod_IsLocalChange()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 22, "Efectivo USD", "USD", isCash: true);
        await context.SaveChangesAsync();
        var service = new CashDrawerService(context);
        var session = await service.OpenSessionAsync(1000m, 50m);

        var dto = await service.AddTransactionAsync(
            session.Id,
            CashTransactionType.Expense,
            CashTransactionSource.SalePayment,
            100m,
            2m,
            50m,
            "Vuelto",
            paymentMethodId: 22);

        Assert.Equal("Bs.S", dto.CurrencyCode);
        var persisted = await context.CashTransactions.AsNoTracking().SingleAsync(t => t.Id == dto.Id);
        Assert.Equal(100m, persisted.AmountLocal);
        Assert.Equal(2m, persisted.AmountUsd);
    }

    [Fact]
    public async Task AddTransaction_CashAdvanceWithUsdDigitalMethod_IsLocalCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 23, "Pago Movil USD", "USD", isCash: false);
        await context.SaveChangesAsync();
        var service = new CashDrawerService(context);
        var session = await service.OpenSessionAsync(1000m, 50m);

        var dto = await service.AddTransactionAsync(
            session.Id,
            CashTransactionType.Income,
            CashTransactionSource.CashAdvance,
            50m,
            1m,
            50m,
            "Adelanto de efectivo",
            paymentMethodId: 23);

        Assert.Equal("Bs.S", dto.CurrencyCode);
    }

    [Fact]
    public async Task AddTransaction_CreditPaymentAndCreditRefund_UsePersistedMethodCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 24, "Efectivo USD", "USD", isCash: true);
        await context.SaveChangesAsync();
        var service = new CashDrawerService(context);
        var session = await service.OpenSessionAsync(1000m, 50m);

        var payment = await service.AddTransactionAsync(
            session.Id,
            CashTransactionType.Income,
            CashTransactionSource.CreditPayment,
            500m,
            10m,
            50m,
            "Abono CxC USD",
            paymentMethodId: 24);
        var refund = await service.AddTransactionAsync(
            session.Id,
            CashTransactionType.Expense,
            CashTransactionSource.CreditRefund,
            100m,
            2m,
            50m,
            "Reembolso USD",
            paymentMethodId: 24);

        Assert.Equal("USD", payment.CurrencyCode);
        Assert.Equal("USD", refund.CurrencyCode);
        Assert.Equal(500m, payment.AmountLocal);
        Assert.Equal(100m, refund.AmountLocal);
    }

    [Fact]
    public async Task AddTransaction_WithoutMethodAndPettyCashSources_FallBackToLocalCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 25, "Efectivo USD", "USD", isCash: true);
        await context.SaveChangesAsync();
        var service = new CashDrawerService(context);
        var session = await service.OpenSessionAsync(1000m, 50m);

        var noMethod = await service.AddTransactionAsync(
            session.Id,
            CashTransactionType.Income,
            CashTransactionSource.SalePayment,
            500m,
            10m,
            50m,
            "Venta sin metodo",
            paymentMethodId: null);
        var pettyCashReversal = await service.AddTransactionAsync(
            session.Id,
            CashTransactionType.Income,
            CashTransactionSource.PettyCashReversal,
            500m,
            10m,
            50m,
            "Reversa caja chica",
            paymentMethodId: 25);

        Assert.Equal("Bs.S", noMethod.CurrencyCode);
        Assert.Equal("Bs.S", pettyCashReversal.CurrencyCode);
    }

    [Fact]
    public async Task History_ExposesSnapshotCurrencyCode()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 26, "Efectivo USD", "USD", isCash: true);
        await context.SaveChangesAsync();
        var service = new CashDrawerService(context);
        var session = await service.OpenSessionAsync(1000m, 50m);

        await service.AddTransactionAsync(
            session.Id,
            CashTransactionType.Income,
            CashTransactionSource.SalePayment,
            500m,
            10m,
            50m,
            "Venta efectivo USD",
            paymentMethodId: 26);

        var history = await service.GetHistoryAsync();
        var paged = await service.GetHistoryPagedAsync();

        var historyTx = history.Single(t => t.Source == CashTransactionSource.SalePayment);
        var pagedTx = paged.Items.Single(t => t.Source == CashTransactionSource.SalePayment);
        Assert.Equal("USD", historyTx.CurrencyCode);
        Assert.Equal("USD", pagedTx.CurrencyCode);
        Assert.Equal(500m, historyTx.AmountLocal);
        Assert.Equal(500m, pagedTx.AmountLocal);
    }

    [Fact]
    public async Task History_JsonPublishesCurrencyCodeWhenPopulatedAndOmitsItForLegacyRows()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 27, "Efectivo USD", "USD", isCash: true);
        await context.SaveChangesAsync();
        var service = new CashDrawerService(context);
        var session = await service.OpenSessionAsync(1000m, 50m);

        await service.AddTransactionAsync(
            session.Id,
            CashTransactionType.Income,
            CashTransactionSource.SalePayment,
            500m,
            10m,
            50m,
            "Venta efectivo USD",
            paymentMethodId: 27);

        // Fila legacy sin backfill: el JSON conserva el contrato dorado sin el campo.
        context.CashTransactions.Add(new CashTransaction
        {
            SessionId = session.Id,
            Type = CashTransactionType.Income,
            Source = CashTransactionSource.CashIn,
            AmountUsd = 1m,
            AmountLocal = 50m,
            ExchangeRate = 50m,
            Description = "Fila legacy sin snapshot",
            IsPhysicalCash = true,
            TransactionTime = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var history = await service.GetHistoryAsync();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string usdJson = JsonSerializer.Serialize(history.Single(t => t.PaymentMethodId == 27), options);
        string legacyJson = JsonSerializer.Serialize(history.Single(t => t.Description == "Fila legacy sin snapshot"), options);

        Assert.Contains("\"currencyCode\":\"USD\"", usdJson);
        Assert.DoesNotContain("currencyCode", legacyJson);
    }

    // ------------------------------------------------------------------ sales write paths

    [Fact]
    public async Task CheckoutCashSale_SnapshotsPersistedUsdMethodCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 30, "Efectivo USD", "USD", isCash: true);
        var sale = await SeedPendingSaleAsync(context);
        var service = CreateSalesService(context);

        await service.CompleteSaleAsync(sale.Id, 50m, new List<PaymentInfo> { new(30, 100m, 5000m, null) });

        var cash = await context.CashTransactions.AsNoTracking().SingleAsync(t => t.SaleId == sale.Id);
        Assert.Equal("USD", cash.CurrencyCode);
        Assert.Equal(CashTransactionType.Income, cash.Type);
        Assert.Equal(CashTransactionSource.SalePayment, cash.Source);
        Assert.Equal(100m, cash.AmountUsd);
        Assert.Equal(5000m, cash.AmountLocal);

        var savedSale = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(100m, savedSale.TotalUSD);
        Assert.Equal(5000m, savedSale.TotalBsS);
    }

    [Fact]
    public async Task HoldInitialCashPayment_SnapshotsPersistedUsdMethodCurrency()
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
        AddMethod(context, 31, "Efectivo USD", "USD", isCash: true);
        var sale = await SeedPendingSaleAsync(context);
        var service = CreateSalesService(context);

        await service.HoldSaleAsync(sale.Id, new HoldSaleRequestDto
        {
            CustomerId = 2,
            ExchangeRate = 50m,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = 31,
                AmountUSD = 40m,
                AmountBsS = 2000m,
                ExchangeRate = 50m
            }
        });

        var cash = await context.CashTransactions.AsNoTracking().SingleAsync(t => t.SaleId == sale.Id);
        Assert.Equal("USD", cash.CurrencyCode);
        Assert.Equal(40m, cash.AmountUsd);
        Assert.Equal(2000m, cash.AmountLocal);
    }

    [Fact]
    public async Task HoldCashPaymentSingleAndBatch_SnapshotPersistedUsdMethodCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 32, "Efectivo USD", "USD", isCash: true);
        AddMethod(context, 33, "Efectivo Bs.S", "Bs.S", isCash: true);

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
        var service = CreateSalesService(context);

        await service.AddPaymentToHoldSaleAsync(
            sale.Id,
            new AddPaymentRequestDto
            {
                PaymentMethodId = 32,
                AmountUSD = 10m,
                AmountBsS = 500m,
                ExchangeRate = 50m
            },
            actingUserId: 7);

        await service.AddPaymentsBatchToHoldSaleAsync(
            sale.Id,
            new List<AddPaymentRequestDto>
            {
                new() { PaymentMethodId = 32, AmountUSD = 10m, AmountBsS = 500m, ExchangeRate = 50m },
                new() { PaymentMethodId = 33, AmountBsS = 500m, ExchangeRate = 50m }
            },
            actingUserId: 7);

        var cashTransactions = await context.CashTransactions
            .AsNoTracking()
            .Where(t => t.SaleId == sale.Id)
            .OrderBy(t => t.Id)
            .ToListAsync();

        Assert.Equal(3, cashTransactions.Count);
        Assert.All(cashTransactions, tx => Assert.Equal(CashTransactionType.Income, tx.Type));
        Assert.Equal("USD", cashTransactions[0].CurrencyCode);
        Assert.Equal("USD", cashTransactions[1].CurrencyCode);
        Assert.Equal("Bs.S", cashTransactions[2].CurrencyCode);
        Assert.Equal(500m, cashTransactions[0].AmountLocal);
    }

    [Fact]
    public async Task CreditPaymentCash_SnapshotsPersistedUsdMethodCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCreditCustomerAsync(context);
        await SeedReceivableAsync(context, customer.Id, balanceUsd: 100m);
        AddMethod(context, 40, "Efectivo USD", "USD", isCash: true);
        await context.SaveChangesAsync();
        var service = CreateSalesService(context);

        var result = await service.RegisterCreditPaymentAsync(new CreditPaymentRequest
        {
            CustomerId = customer.Id,
            AmountUSD = 50m,
            PaymentMethodId = 40
        });

        var cash = await context.CashTransactions.AsNoTracking().SingleAsync(t => t.Id == result.CashTransactionId);
        Assert.Equal("USD", cash.CurrencyCode);
        Assert.Equal(50m, cash.AmountUsd);
        Assert.Equal(2500m, cash.AmountLocal);
        Assert.Equal(CashTransactionSource.CreditPayment, cash.Source);
    }

    [Fact]
    public async Task CreditRefundUsdRoute_SnapshotsUsdCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCreditCustomerAsync(context);
        AddMethod(context, 41, "Efectivo USD", "USD", isCash: true);
        AddMethod(context, 42, "Punto de Venta", "Bs.S", isCash: false);
        var (sale, receivable) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m);
        await context.SaveChangesAsync();
        // Ultimo abono por efectivo USD: el reembolso del excedente sale por la misma gaveta.
        await SeedCreditPaymentAsync(context, receivable, amountUsd: 40m, paymentRate: 60m, paymentMethodId: 41);
        var service = CreateSalesService(context);

        var result = await service.RegisterCreditReturnAsync(
            sale.Id,
            new CreditReturnRequest
            {
                Items = new List<CreditReturnItemRequest>
                {
                    new() { SaleItemId = sale.Items.Single().Id, Quantity = 1m }
                }
            },
            actingUserId: 7);

        var cash = await context.CashTransactions.AsNoTracking().SingleAsync(t => t.Id == result.CashTransactionId);
        Assert.Equal("USD", cash.CurrencyCode);
        Assert.Equal(40m, cash.AmountUsd);
        Assert.Equal(2400m, cash.AmountLocal);
        Assert.Equal(CashTransactionSource.CreditRefund, cash.Source);
        Assert.Equal(40.0000m, result.RefundedUSD);
    }

    // ------------------------------------------------------------------ petty cash

    [Fact]
    public async Task PettyCashExpenseAndReversal_SnapshotExpenseCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        AddMethod(context, 50, "Efectivo USD", "USD", isCash: true);
        AddMethod(context, 51, "Efectivo Bs.S", "Bs.S", isCash: true);
        await context.SaveChangesAsync();

        var drawer = new CashDrawerService(context);
        var inventory = new Mock<IInventoryService>();
        inventory.Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(50m);
        var pettyCash = new PettyCashService(context, drawer, inventory.Object, new Mock<ISystemSettingsService>().Object);

        await drawer.OpenSessionAsync(1000m, 50m, cancellationToken: default, openingBalanceUsd: 100m);

        var expense = await pettyCash.RegisterAsync(new PettyCashExpenseRequest
        {
            Currency = PettyCashCurrency.Usd,
            Amount = 20m,
            Category = PettyCashCategory.FletesAcarreo,
            Concept = "Flete en dolares",
            Beneficiary = "Transporte C.A.",
            ReferenceNumber = "FACT-USD-1"
        }, actingUserId: 1, actingRole: UserRole.Admin);

        var movement = await context.CashTransactions.AsNoTracking()
            .SingleAsync(t => t.Source == CashTransactionSource.PettyCashExpense);
        Assert.Equal("USD", movement.CurrencyCode);
        Assert.Equal(20m, movement.AmountUsd);

        await pettyCash.CancelAsync(
            expense.Id,
            "Proveedor no entrego el pedido",
            actingUserId: 1,
            actingRole: UserRole.Admin);

        var reversal = await context.CashTransactions.AsNoTracking()
            .SingleAsync(t => t.Source == CashTransactionSource.PettyCashReversal);
        Assert.Equal("USD", reversal.CurrencyCode);
        Assert.Equal(20m, reversal.AmountUsd);
    }

    // ------------------------------------------------------------------ seeds

    private static async Task<Customer> SeedCreditCustomerAsync(SalesDbContext context)
    {
        var customer = new Customer
        {
            CedulaOrRif = "V-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Cliente Caja USD",
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

    private static async Task<Receivable> SeedReceivableAsync(SalesDbContext context, int customerId, decimal balanceUsd)
    {
        var sale = new Sale
        {
            CustomerId = customerId,
            Status = SaleStatus.Completed,
            InvoiceNumber = Random.Shared.Next(900000, 999999),
            Date = DateTime.UtcNow,
            AppliedRate = 50m,
            TotalUSD = balanceUsd,
            TotalBsS = balanceUsd * 50m
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var receivable = new Receivable
        {
            SaleId = sale.Id,
            CustomerId = customerId,
            OriginalUSD = balanceUsd,
            BalanceUSD = balanceUsd,
            OriginalBsS = balanceUsd * 50m,
            HistoricalRate = 50m,
            IssuedAtUtc = DateTime.UtcNow,
            DueDateUtc = DateTime.UtcNow.AddDays(10),
            Status = ReceivableStatus.Vigente
        };
        context.Receivables.Add(receivable);
        await context.SaveChangesAsync();
        return receivable;
    }

    private static async Task<(Sale Sale, Receivable Receivable)> SeedCreditSaleAsync(
        SalesDbContext context,
        int customerId,
        decimal rate)
    {
        var sale = new Sale
        {
            CustomerId = customerId,
            Status = SaleStatus.Completed,
            InvoiceNumber = Random.Shared.Next(900000, 999999),
            Date = DateTime.UtcNow,
            AppliedRate = rate,
            DeliveryStatus = SaleDeliveryStatus.Delivered
        };
        sale.Items.Add(new SaleItem
        {
            ProductId = 101,
            ProductName = "Cemento",
            Quantity = 1m,
            UnitPrice = 100m,
            Subtotal = 100m,
            UnitPriceBsS = 5000m,
            SubtotalBsS = 5000m
        });
        sale.Subtotal = 100m;
        sale.TotalUSD = 100m;
        sale.TotalBsS = 5000m;
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var receivable = new Receivable
        {
            SaleId = sale.Id,
            CustomerId = customerId,
            OriginalUSD = 100m,
            BalanceUSD = 100m,
            OriginalBsS = 5000m,
            HistoricalRate = rate,
            IssuedAtUtc = DateTime.UtcNow,
            DueDateUtc = DateTime.UtcNow.AddDays(10),
            Status = ReceivableStatus.Vigente
        };
        context.Receivables.Add(receivable);
        await context.SaveChangesAsync();

        return (sale, receivable);
    }

    private static async Task SeedCreditPaymentAsync(
        SalesDbContext context,
        Receivable receivable,
        decimal amountUsd,
        decimal paymentRate,
        int paymentMethodId)
    {
        decimal amountBsS = Math.Round(amountUsd * paymentRate, 2, MidpointRounding.AwayFromZero);
        decimal historicalBsS = Math.Round(amountUsd * receivable.HistoricalRate, 2, MidpointRounding.AwayFromZero);

        var payment = new ReceivablePayment
        {
            CustomerId = receivable.CustomerId,
            AmountUSD = amountUsd,
            AmountBsS = amountBsS,
            PaymentRate = paymentRate,
            FxDifferenceBsS = amountBsS - historicalBsS,
            PaymentMethodId = paymentMethodId,
            ReceivedAtUtc = DateTime.UtcNow
        };
        payment.Allocations.Add(new CreditPaymentAllocation
        {
            ReceivableId = receivable.Id,
            AmountUSD = amountUsd,
            AmountBsS = amountBsS,
            FxDifferenceBsS = amountBsS - historicalBsS
        });
        context.ReceivablePayments.Add(payment);

        var tracked = await context.Receivables.FirstAsync(candidate => candidate.Id == receivable.Id);
        tracked.BalanceUSD = Math.Round(tracked.BalanceUSD - amountUsd, 4, MidpointRounding.AwayFromZero);
        tracked.Status = tracked.BalanceUSD == 0m ? ReceivableStatus.Pagada : ReceivableStatus.Parcial;
        await context.SaveChangesAsync();
    }
}
