using System;
using System.Collections.Generic;
using System.Linq;
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
using Sales.Module.Services;
using Xunit;
using SalesService = Sales.Module.Services.SalesService;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// T3b2 (F2b, Paso 11/12, S5/S7): anulación sin abonos, devoluciones totales/parciales y
/// overpago de una venta a crédito. La anulación simple solo procede sin movimientos de cartera
/// (restituye stock y cupo, cero caja); la devolución reduce primero el saldo con base EFECTIVA por
/// línea y el excedente efectivamente pagado se reembolsa a la tasa congelada del último abono.
/// </summary>
public class CreditReturnTests
{
    // ---------------------------------------------------------------- anulación sin abonos

    [Fact]
    public async Task CancelCreditSale_WithoutPayments_RestocksAllItemsCancelsSaleAndRestoresCredit()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, creditLimitUsd: 200m);
        var (sale, receivable) = await SeedCreditSaleAsync(
            context,
            customer.Id,
            rate: 50m,
            (101, "Harina Pan", 2m, 25m, 2500m),
            (102, "Arroz", 1m, 50m, 2500m));
        var (service, inventory, _) = CreateHarness(context);
        var stockRequests = CaptureStockRequests(inventory);

        var result = await service.CancelCreditSaleAsync(sale.Id, "Anulación autorizada por el cliente", actingUserId: 7);

        Assert.Equal(sale.Id, result.SaleId);
        Assert.Equal(receivable.Id, result.ReceivableId);
        Assert.Equal(customer.Id, result.CustomerId);
        Assert.Equal(100.0000m, result.CancelledOriginalUSD);
        Assert.Equal(ReceivableStatus.Anulada, result.Status);
        Assert.Equal(SaleStatus.Cancelled, result.SaleStatus);
        Assert.Equal("Anulación autorizada por el cliente", result.Reason);

        var persistedSale = await context.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == sale.Id);
        Assert.Equal(SaleStatus.Cancelled, persistedSale.Status);

        var persistedReceivable = await context.Receivables.AsNoTracking().FirstAsync(candidate => candidate.Id == receivable.Id);
        Assert.Equal(ReceivableStatus.Anulada, persistedReceivable.Status);
        Assert.Equal(0m, persistedReceivable.BalanceUSD);

        // Restitución completa del stock por el camino de inventario, con motivo Return.
        Assert.Equal(2, stockRequests.Count);
        Assert.Contains(
            stockRequests,
            request => request.ProductId == 101
                && request.QuantityChange == 2m
                && request.SaleId == sale.Id
                && request.OriginType == StockMovementOriginType.System
                && request.ReasonCode == StockMovementReason.Return);
        Assert.Contains(
            stockRequests,
            request => request.ProductId == 102
                && request.QuantityChange == 1m
                && request.ReasonCode == StockMovementReason.Return);

        // Cero movimiento de caja: la anulación sin abonos no genera salidas inexistentes.
        Assert.Empty(await context.CashTransactions.AsNoTracking().ToListAsync());

        // Cupo restituido implícitamente (la obligación anulada deja de computar deuda).
        var profile = await new CreditService(context).GetCreditProfileAsync(customer.Id);
        Assert.Equal(0m, profile.CurrentDebtUSD);
        Assert.Equal(200m, profile.AvailableCreditUSD);
    }

    [Fact]
    public async Task CancelCreditSale_WithPayments_IsBlockedWithoutMovement()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var (sale, receivable) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (101, "Cemento", 1m, 100m, 5000m));
        await SeedCreditPaymentAsync(context, receivable, amountUsd: 40m, paymentRate: 60m);
        var (service, inventory, _) = CreateHarness(context);
        var stockRequests = CaptureStockRequests(inventory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CancelCreditSaleAsync(sale.Id, "Intento con abonos", actingUserId: 7));

        Assert.Contains("abonos", ex.Message);
        Assert.Empty(stockRequests);
        Assert.Empty(await context.CashTransactions.AsNoTracking().ToListAsync());
        Assert.Equal(SaleStatus.Completed, (await context.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == sale.Id)).Status);

        var persistedReceivable = await context.Receivables.AsNoTracking().FirstAsync(candidate => candidate.Id == receivable.Id);
        Assert.Equal(ReceivableStatus.Parcial, persistedReceivable.Status);
        Assert.Equal(60.0000m, persistedReceivable.BalanceUSD);
    }

    [Fact]
    public async Task CancelCreditSale_MixedSaleWithInitialPaymentOnly_IsBlockedBecauseMoneyWasCollected()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        await SeedPaymentMethodsAsync(context);
        // Venta mixta: 100 USD totales con pago inicial de 40 USD; la obligación financia 60 USD.
        var (sale, receivable) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (101, "Cemento", 1m, 100m, 5000m));
        await SeedMixedCreditSaleAsync(context, sale, receivable, rate: 50m, initialPaymentUsd: 40m, initialPaymentMethodId: 2);
        var (service, inventory, _) = CreateHarness(context);
        var stockRequests = CaptureStockRequests(inventory);

        // El pago inicial es dinero real: sin abonos imputados la anulación simple igual queda
        // bloqueada porque "cero movimiento de caja" solo es válido cuando NADA se pagó.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CancelCreditSaleAsync(sale.Id, "Intento de anulación con pago inicial", actingUserId: 7));

        Assert.Contains("pago inicial", ex.Message);
        Assert.Contains("flujo de devolución", ex.Message);
        Assert.Empty(stockRequests);
        Assert.Empty(await context.CashTransactions.AsNoTracking().ToListAsync());
        Assert.Equal(SaleStatus.Completed, (await context.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == sale.Id)).Status);

        var persistedReceivable = await context.Receivables.AsNoTracking().FirstAsync(candidate => candidate.Id == receivable.Id);
        Assert.Equal(ReceivableStatus.Vigente, persistedReceivable.Status);
        Assert.Equal(60.0000m, persistedReceivable.OriginalUSD);
        Assert.Equal(60.0000m, persistedReceivable.BalanceUSD);
    }

    [Fact]
    public async Task CancelCreditSale_AfterReturn_IsBlockedBecausePortfolioAlreadyMoved()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var (sale, _) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (101, "Harina Pan", 2m, 25m, 2500m));
        var (service, _, _) = CreateHarness(context);

        await service.RegisterCreditReturnAsync(
            sale.Id,
            new CreditReturnRequest
            {
                Items = new List<CreditReturnItemRequest> { new() { SaleItemId = sale.Items.Single().Id, Quantity = 1m } }
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CancelCreditSaleAsync(sale.Id, "Anulación tras devolución"));

        Assert.Contains("devoluciones", ex.Message);
        Assert.Equal(SaleStatus.Completed, (await context.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == sale.Id)).Status);
        Assert.NotEqual(ReceivableStatus.Anulada, (await context.Receivables.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task CancelCreditSale_WithoutReceivableOrUnknownSale_IsRejected()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var cashSale = new Sale
        {
            CustomerId = customer.Id,
            Status = SaleStatus.Completed,
            InvoiceNumber = 7100,
            Date = DateTime.UtcNow,
            AppliedRate = 50m,
            TotalUSD = 10m,
            TotalBsS = 500m
        };
        context.Sales.Add(cashSale);
        await context.SaveChangesAsync();
        var (service, _, _) = CreateHarness(context);

        var noReceivable = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CancelCreditSaleAsync(cashSale.Id, null));
        Assert.Contains("cuenta por cobrar", noReceivable.Message);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CancelCreditSaleAsync(999999, null));
    }

    // ---------------------------------------------------------------- devoluciones

    [Fact]
    public async Task RegisterCreditReturn_PartialReturn_ReducesBalanceRestocksOnlyReturnedQuantityAndTouchesNoCash()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var (sale, receivable) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (101, "Harina Pan", 2m, 25m, 2500m));
        var saleItemId = sale.Items.Single().Id;
        var (service, inventory, _) = CreateHarness(context);
        var stockRequests = CaptureStockRequests(inventory);

        var result = await service.RegisterCreditReturnAsync(
            sale.Id,
            new CreditReturnRequest
            {
                Items = new List<CreditReturnItemRequest> { new() { SaleItemId = saleItemId, Quantity = 1m } },
                Reason = "Producto defectuoso",
            },
            actingUserId: 7);

        // Valor desde la base EFECTIVA: 2500 Bs.S / 50 = 50.0000 USD la línea completa; mitad = 25.0000.
        Assert.Equal(25.0000m, result.ReturnedUSD);
        Assert.Equal(25.0000m, result.BalanceAfterUSD);
        Assert.Equal(0m, result.RefundedUSD);
        Assert.Equal(0m, result.RefundedBsS);
        Assert.Null(result.RefundRate);
        Assert.Null(result.CashTransactionId);
        Assert.Equal(ReceivableStatus.Vigente, result.Status);
        Assert.Equal(SaleStatus.Completed, result.SaleStatus);
        Assert.False(result.IsReplay);

        var persistedReturn = await context.CreditReturns
            .AsNoTracking()
            .Include(candidate => candidate.Items)
            .SingleAsync();
        Assert.Equal(sale.Id, persistedReturn.SaleId);
        Assert.Equal(receivable.Id, persistedReturn.ReceivableId);
        Assert.Equal(customer.Id, persistedReturn.CustomerId);
        Assert.Equal(25.0000m, persistedReturn.ReturnedUSD);
        Assert.Equal(0m, persistedReturn.RefundedUSD);
        Assert.Equal(0m, persistedReturn.RefundedBsS);
        Assert.Null(persistedReturn.RefundRate);
        Assert.Null(persistedReturn.CashTransactionId);
        Assert.Equal(7, persistedReturn.CreatedByUserId);
        Assert.Equal("Producto defectuoso", persistedReturn.Reason);
        var persistedLine = Assert.Single(persistedReturn.Items);
        Assert.Equal(saleItemId, persistedLine.SaleItemId);
        Assert.Equal(1m, persistedLine.Quantity);
        Assert.Equal(25.0000m, persistedLine.AmountUSD);

        var persistedReceivable = await context.Receivables.AsNoTracking().FirstAsync(candidate => candidate.Id == receivable.Id);
        Assert.Equal(25.0000m, persistedReceivable.BalanceUSD);

        // Solo se restituye la cantidad devuelta, con motivo Return y sin caja.
        var stockRequest = Assert.Single(stockRequests);
        Assert.Equal(101, stockRequest.ProductId);
        Assert.Equal(1m, stockRequest.QuantityChange);
        Assert.Equal(StockMovementReason.Return, stockRequest.ReasonCode);
        Assert.Empty(await context.CashTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RegisterCreditReturn_FullReturnWithPayments_ClosesReceivableAndRefundsOverpayAtLatestPaymentRate()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        await SeedPaymentMethodsAsync(context);
        var (sale, receivable) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (101, "Cemento", 1m, 100m, 5000m));
        var saleItemId = sale.Items.Single().Id;
        // Dos abonos: el ÚLTIMO (tasa 60, recibido después) es el que rige el reembolso.
        await SeedCreditPaymentAsync(context, receivable, amountUsd: 10m, paymentRate: 55m, receivedAtUtc: DateTime.UtcNow.AddMinutes(-10));
        await SeedCreditPaymentAsync(context, receivable, amountUsd: 30m, paymentRate: 60m, receivedAtUtc: DateTime.UtcNow.AddMinutes(-5));
        var (service, inventory, cashDrawer) = CreateHarness(context);
        var stockRequests = CaptureStockRequests(inventory);

        var result = await service.RegisterCreditReturnAsync(
            sale.Id,
            new CreditReturnRequest
            {
                Items = new List<CreditReturnItemRequest> { new() { SaleItemId = saleItemId, Quantity = 1m } }
            },
            actingUserId: 7);

        Assert.Equal(100.0000m, result.ReturnedUSD);
        Assert.Equal(0m, result.BalanceAfterUSD);
        Assert.Equal(ReceivableStatus.Anulada, result.Status);
        // Con abonos la venta ya no se anula: la obligación queda Anulada pero la factura se conserva.
        Assert.Equal(SaleStatus.Completed, result.SaleStatus);

        // Overpago = 100 devuelto − 60 pendiente = 40 efectivamente pagado, a la tasa del último abono (60).
        Assert.Equal(40.0000m, result.RefundedUSD);
        Assert.Equal(2400.00m, result.RefundedBsS);
        Assert.Equal(60.0000m, result.RefundRate);
        Assert.NotNull(result.CashTransactionId);

        var cash = await context.CashTransactions.AsNoTracking().SingleAsync();
        Assert.Equal(cash.Id, result.CashTransactionId);
        Assert.Equal(CashTransactionType.Expense, cash.Type);
        Assert.Equal(CashTransactionSource.CreditRefund, cash.Source);
        Assert.Equal(40m, cash.AmountUsd);
        Assert.Equal(2400.00m, cash.AmountLocal);
        Assert.Equal(60.0000m, cash.ExchangeRate);
        Assert.Equal(1, cash.PaymentMethodId);
        Assert.Equal(1, cash.SessionId);
        Assert.Equal(sale.Id, cash.SaleId);
        Assert.True(cash.IsPhysicalCash);

        var persistedReturn = await context.CreditReturns.AsNoTracking().SingleAsync();
        Assert.Equal(40.0000m, persistedReturn.RefundedUSD);
        Assert.Equal(2400.00m, persistedReturn.RefundedBsS);
        Assert.Equal(60.0000m, persistedReturn.RefundRate);
        Assert.Equal(cash.Id, persistedReturn.CashTransactionId);

        var persistedReceivable = await context.Receivables.AsNoTracking().FirstAsync(candidate => candidate.Id == receivable.Id);
        Assert.Equal(0m, persistedReceivable.BalanceUSD);
        Assert.Equal(ReceivableStatus.Anulada, persistedReceivable.Status);

        var stockRequest = Assert.Single(stockRequests);
        Assert.Equal(1m, stockRequest.QuantityChange);
        cashDrawer.Verify(
            drawer => drawer.GetOrCreateActiveSessionAsync(60.0000m, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RegisterCreditReturn_MixedSaleFullReturn_RefundsInitialPaymentAtSalePaymentRateAndMethod()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        await SeedPaymentMethodsAsync(context);
        // Venta mixta: 100 USD totales pagados 40 (método 2) y financiados 60.
        var (sale, receivable) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (101, "Cemento", 1m, 100m, 5000m));
        await SeedMixedCreditSaleAsync(context, sale, receivable, rate: 50m, initialPaymentUsd: 40m, initialPaymentMethodId: 2);
        var (service, inventory, cashDrawer) = CreateHarness(context);
        var stockRequests = CaptureStockRequests(inventory);

        var result = await service.RegisterCreditReturnAsync(
            sale.Id,
            new CreditReturnRequest
            {
                Items = new List<CreditReturnItemRequest> { new() { SaleItemId = sale.Items.Single().Id, Quantity = 1m } }
            },
            actingUserId: 7);

        Assert.Equal(100.0000m, result.ReturnedUSD);
        Assert.Equal(0m, result.BalanceAfterUSD);
        Assert.Equal(ReceivableStatus.Anulada, result.Status);
        // Con dinero recibido la factura se conserva y el reembolso sale por caja: la devolución
        // total de una venta mixta no puede quedarse silenciosamente con el pago inicial.
        Assert.Equal(SaleStatus.Completed, result.SaleStatus);
        Assert.Equal(40.0000m, result.RefundedUSD);
        Assert.Equal(2000.00m, result.RefundedBsS);
        Assert.Equal(50.0000m, result.RefundRate);
        Assert.NotNull(result.CashTransactionId);

        var cash = await context.CashTransactions.AsNoTracking().SingleAsync();
        Assert.Equal(cash.Id, result.CashTransactionId);
        Assert.Equal(CashTransactionType.Expense, cash.Type);
        Assert.Equal(CashTransactionSource.CreditRefund, cash.Source);
        Assert.Equal(40m, cash.AmountUsd);
        Assert.Equal(2000.00m, cash.AmountLocal);
        Assert.Equal(50.0000m, cash.ExchangeRate);
        // Tasa y método salen del SalePayment del pago inicial (método 2, no el primer efectivo).
        Assert.Equal(2, cash.PaymentMethodId);
        Assert.Equal(sale.Id, cash.SaleId);
        Assert.True(cash.IsPhysicalCash);

        var persistedReturn = await context.CreditReturns.AsNoTracking().SingleAsync();
        Assert.Equal(40.0000m, persistedReturn.RefundedUSD);
        Assert.Equal(2000.00m, persistedReturn.RefundedBsS);
        Assert.Equal(50.0000m, persistedReturn.RefundRate);
        Assert.Equal(cash.Id, persistedReturn.CashTransactionId);

        var persistedReceivable = await context.Receivables.AsNoTracking().FirstAsync(candidate => candidate.Id == receivable.Id);
        Assert.Equal(0m, persistedReceivable.BalanceUSD);
        Assert.Equal(ReceivableStatus.Anulada, persistedReceivable.Status);

        var persistedSale = await context.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, persistedSale.Status);

        var stockRequest = Assert.Single(stockRequests);
        Assert.Equal(1m, stockRequest.QuantityChange);
        cashDrawer.Verify(
            drawer => drawer.GetOrCreateActiveSessionAsync(50.0000m, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RegisterCreditReturn_FullReturnWithoutPayments_AnnullsReceivableCancelsSaleAndDoesNotTouchCash()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var (sale, receivable) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (101, "Harina Pan", 1m, 80m, 4000m));
        var (service, _, _) = CreateHarness(context);

        var result = await service.RegisterCreditReturnAsync(
            sale.Id,
            new CreditReturnRequest
            {
                Items = new List<CreditReturnItemRequest> { new() { SaleItemId = sale.Items.Single().Id, Quantity = 1m } }
            });

        Assert.Equal(80.0000m, result.ReturnedUSD);
        Assert.Equal(0m, result.BalanceAfterUSD);
        Assert.Equal(ReceivableStatus.Anulada, result.Status);
        // Devolución total sin abonos: no hay dinero pagado, la venta también se anula.
        Assert.Equal(SaleStatus.Cancelled, result.SaleStatus);
        Assert.Equal(0m, result.RefundedUSD);
        Assert.Null(result.CashTransactionId);

        Assert.Equal(SaleStatus.Cancelled, (await context.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == sale.Id)).Status);
        Assert.Empty(await context.CashTransactions.AsNoTracking().ToListAsync());
        Assert.Equal(ReceivableStatus.Anulada, (await context.Receivables.AsNoTracking().FirstAsync(candidate => candidate.Id == receivable.Id)).Status);
    }

    [Fact]
    public async Task RegisterCreditReturn_OverCumulativeQuantity_IsRejectedWithoutNewMovement()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var (sale, receivable) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (101, "Harina Pan", 2m, 25m, 2500m));
        var saleItemId = sale.Items.Single().Id;
        var (service, inventory, _) = CreateHarness(context);
        var stockRequests = CaptureStockRequests(inventory);

        await service.RegisterCreditReturnAsync(
            sale.Id,
            new CreditReturnRequest
            {
                Items = new List<CreditReturnItemRequest> { new() { SaleItemId = saleItemId, Quantity = 1m } }
            });

        // 1 ya devuelta + 2 solicitadas > 2 vendidas: rechazo acumulativo.
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.RegisterCreditReturnAsync(
                sale.Id,
                new CreditReturnRequest
                {
                    Items = new List<CreditReturnItemRequest> { new() { SaleItemId = saleItemId, Quantity = 2m } }
                }));

        Assert.Contains("excede", ex.Message);
        Assert.Single(await context.CreditReturns.AsNoTracking().ToListAsync());
        Assert.Equal(25.0000m, (await context.Receivables.AsNoTracking().FirstAsync(candidate => candidate.Id == receivable.Id)).BalanceUSD);
        Assert.Single(stockRequests);
    }

    [Fact]
    public async Task RegisterCreditReturn_ForeignItemOrClosedReceivable_IsRejected()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var (sale, receivable) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (101, "Harina Pan", 1m, 25m, 1250m));
        var (foreignSale, _) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (102, "Arroz", 1m, 25m, 1250m));
        var (service, _, _) = CreateHarness(context);

        var foreignEx = await Assert.ThrowsAsync<ArgumentException>(
            () => service.RegisterCreditReturnAsync(
                sale.Id,
                new CreditReturnRequest
                {
                    Items = new List<CreditReturnItemRequest> { new() { SaleItemId = foreignSale.Items.Single().Id, Quantity = 1m } }
                }));
        Assert.Contains("no pertenece", foreignEx.Message);

        // Obligación cerrada (Pagada/Anulada): no admite devoluciones.
        var tracked = await context.Receivables.FirstAsync(candidate => candidate.Id == receivable.Id);
        tracked.Status = ReceivableStatus.Anulada;
        tracked.BalanceUSD = 0m;
        await context.SaveChangesAsync();

        var closedEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RegisterCreditReturnAsync(
                sale.Id,
                new CreditReturnRequest
                {
                    Items = new List<CreditReturnItemRequest> { new() { SaleItemId = sale.Items.Single().Id, Quantity = 1m } }
                }));
        Assert.Contains("cerrada", closedEx.Message);

        Assert.Empty(await context.CreditReturns.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RegisterCreditReturn_InvalidRequestLines_AreRejected()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var (sale, _) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (101, "Harina Pan", 2m, 25m, 2500m));
        var saleItemId = sale.Items.Single().Id;
        var (service, _, _) = CreateHarness(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.RegisterCreditReturnAsync(sale.Id, new CreditReturnRequest()));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.RegisterCreditReturnAsync(
                sale.Id,
                new CreditReturnRequest
                {
                    Items = new List<CreditReturnItemRequest> { new() { SaleItemId = saleItemId, Quantity = 0m } }
                }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.RegisterCreditReturnAsync(
                sale.Id,
                new CreditReturnRequest
                {
                    Items = new List<CreditReturnItemRequest> { new() { SaleItemId = saleItemId, Quantity = -1m } }
                }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.RegisterCreditReturnAsync(
                sale.Id,
                new CreditReturnRequest
                {
                    Items = new List<CreditReturnItemRequest>
                    {
                        new() { SaleItemId = saleItemId, Quantity = 1m },
                        new() { SaleItemId = saleItemId, Quantity = 1m }
                    }
                }));

        Assert.Empty(await context.CreditReturns.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RegisterCreditReturn_RepeatedWithinLimits_IsAllowedAndAccumulates()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var (sale, receivable) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (101, "Harina Pan", 3m, 10m, 1500m));
        var saleItemId = sale.Items.Single().Id;
        var (service, inventory, _) = CreateHarness(context);
        var stockRequests = CaptureStockRequests(inventory);

        var first = await service.RegisterCreditReturnAsync(
            sale.Id,
            new CreditReturnRequest
            {
                Items = new List<CreditReturnItemRequest> { new() { SaleItemId = saleItemId, Quantity = 1m } }
            });
        var second = await service.RegisterCreditReturnAsync(
            sale.Id,
            new CreditReturnRequest
            {
                Items = new List<CreditReturnItemRequest> { new() { SaleItemId = saleItemId, Quantity = 1m } }
            });

        Assert.Equal(10.0000m, first.ReturnedUSD);
        Assert.Equal(10.0000m, second.ReturnedUSD);
        Assert.Equal(10.0000m, second.BalanceAfterUSD);
        Assert.Equal(ReceivableStatus.Vigente, second.Status);

        Assert.Equal(2, await context.CreditReturns.AsNoTracking().CountAsync());
        Assert.Equal(2, stockRequests.Count);
        Assert.All(stockRequests, request => Assert.Equal(1m, request.QuantityChange));
        Assert.Equal(10.0000m, (await context.Receivables.AsNoTracking().FirstAsync(candidate => candidate.Id == receivable.Id)).BalanceUSD);

        // Una tercera devolución de 2 excedería el acumulado (1 + 1 + 2 > 3).
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.RegisterCreditReturnAsync(
                sale.Id,
                new CreditReturnRequest
                {
                    Items = new List<CreditReturnItemRequest> { new() { SaleItemId = saleItemId, Quantity = 2m } }
                }));
        Assert.Equal(2, await context.CreditReturns.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task RegisterCreditReturn_IdempotentReplay_ReturnsExistingWithoutNewCashStockOrBalance()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        await SeedPaymentMethodsAsync(context);
        var (sale, receivable) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (101, "Cemento", 1m, 100m, 5000m));
        await SeedCreditPaymentAsync(context, receivable, amountUsd: 40m, paymentRate: 60m);
        var (service, inventory, _) = CreateHarness(context);
        var stockRequests = CaptureStockRequests(inventory);

        var request = new CreditReturnRequest
        {
            Items = new List<CreditReturnItemRequest> { new() { SaleItemId = sale.Items.Single().Id, Quantity = 1m } },
            IdempotencyKey = "cxc-devol-1"
        };

        var first = await service.RegisterCreditReturnAsync(sale.Id, request, actingUserId: 7);
        // El reintento con la misma clave replica la devolución aunque la obligación ya esté anulada.
        var replay = await service.RegisterCreditReturnAsync(sale.Id, request, actingUserId: 7);

        Assert.False(first.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.ReturnId, replay.ReturnId);
        Assert.Equal(first.RefundedUSD, replay.RefundedUSD);
        Assert.Equal(first.RefundedBsS, replay.RefundedBsS);
        Assert.Equal(0m, replay.BalanceAfterUSD);
        Assert.Equal(ReceivableStatus.Anulada, replay.Status);

        Assert.Single(await context.CreditReturns.AsNoTracking().ToListAsync());
        Assert.Single(await context.CreditReturnItems.AsNoTracking().ToListAsync());
        Assert.Single(await context.CashTransactions.AsNoTracking().ToListAsync());
        Assert.Single(stockRequests);

        // La misma clave reutilizada en otra venta es un conflicto explícito.
        var (otherSale, _) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (103, "Arroz", 1m, 10m, 500m));
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RegisterCreditReturnAsync(
                otherSale.Id,
                new CreditReturnRequest
                {
                    Items = new List<CreditReturnItemRequest> { new() { SaleItemId = otherSale.Items.Single().Id, Quantity = 1m } },
                    IdempotencyKey = "cxc-devol-1"
                }));
        Assert.Contains("idempotencia", conflict.Message);

        // La venta de un abono ya aplicado conserva su estado histórico.
        Assert.Equal(SaleStatus.Completed, (await context.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == sale.Id)).Status);
    }

    [Fact]
    public async Task RegisterCreditReturn_OnSaleWithoutReceivable_IsRejected()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var cashSale = new Sale
        {
            CustomerId = customer.Id,
            Status = SaleStatus.Completed,
            InvoiceNumber = 7200,
            Date = DateTime.UtcNow,
            AppliedRate = 50m,
            TotalUSD = 10m,
            TotalBsS = 500m
        };
        cashSale.Items.Add(new SaleItem
        {
            ProductId = 101,
            ProductName = "Harina Pan",
            Quantity = 1m,
            UnitPrice = 10m,
            Subtotal = 10m,
            SubtotalBsS = 500m
        });
        context.Sales.Add(cashSale);
        await context.SaveChangesAsync();
        var (service, _, _) = CreateHarness(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RegisterCreditReturnAsync(
                cashSale.Id,
                new CreditReturnRequest
                {
                    Items = new List<CreditReturnItemRequest> { new() { SaleItemId = cashSale.Items.Single().Id, Quantity = 1m } }
                }));
        Assert.Contains("cuenta por cobrar", ex.Message);
    }

    // ---------------------------------------------------------------- math efectivo (ExactMath / regla cash)

    [Fact]
    public async Task RegisterCreditReturn_ValuesLinesFromEffectiveBase_NotCanonicalUsd()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        // ExactMath: 0.813 × 50 = 40.65 Bs.S efectivos → 0.8130 USD (el canónico redondea a 0.81).
        var (exactSale, _) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (101, "Exacto", 1m, 0.813m, 40.65m));
        // VesCashFriendlyStep5: 0.81 × 50 = 40.50 → escalón ×5 = 45.00 Bs.S → 0.9000 USD.
        var (stepSale, _) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (102, "Escalonado", 1m, 0.81m, 45.00m));
        var (service, _, _) = CreateHarness(context);

        var exact = await service.RegisterCreditReturnAsync(
            exactSale.Id,
            new CreditReturnRequest
            {
                Items = new List<CreditReturnItemRequest> { new() { SaleItemId = exactSale.Items.Single().Id, Quantity = 1m } }
            });
        var step = await service.RegisterCreditReturnAsync(
            stepSale.Id,
            new CreditReturnRequest
            {
                Items = new List<CreditReturnItemRequest> { new() { SaleItemId = stepSale.Items.Single().Id, Quantity = 1m } }
            });

        Assert.Equal(PricingCalculator.ToUSD(40.65m, 50m, 4), exact.ReturnedUSD);
        Assert.Equal(0.8130m, exact.ReturnedUSD);
        Assert.Equal(0.9000m, step.ReturnedUSD);
        Assert.Equal(0m, exact.BalanceAfterUSD);
        Assert.Equal(0m, step.BalanceAfterUSD);
    }

    // ---------------------------------------------------------------- cierre Z

    [Fact]
    public async Task GetExpectedTotalsByPaymentMethod_AfterMixedSaleFullReturn_NetsRefundAndKeepsOtherMethodsUnchanged()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        var closureService = DailyClosureTestHelper.CreateService(context);

        var customer = await SeedCustomerAsync(context);
        var (sale, receivable) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (101, "Cemento", 1m, 100m, 5000m));
        await SeedMixedCreditSaleAsync(context, sale, receivable, rate: 50m, initialPaymentUsd: 40m, initialPaymentMethodId: 1);
        // Pago ajeno, sin devolución: su esperado no debe moverse con el reembolso.
        var (otherSale, _) = await SeedCreditSaleAsync(context, customer.Id, rate: 50m, (102, "Arroz", 1m, 10m, 500m));
        await SeedSalePaymentAsync(context, otherSale.Id, amountUsd: 10m, amountBsS: 500m, exchangeRate: 50m, paymentMethodId: 3);

        var before = await closureService.GetExpectedTotalsByPaymentMethodAsync(DateTime.UtcNow);
        Assert.Equal(2000m, before.Single(total => total.PaymentMethodId == 1).ExpectedAmountBsS);
        Assert.Equal(500m, before.Single(total => total.PaymentMethodId == 3).ExpectedAmountBsS);

        var (service, _, _) = CreateHarness(context);
        var result = await service.RegisterCreditReturnAsync(
            sale.Id,
            new CreditReturnRequest
            {
                Items = new List<CreditReturnItemRequest> { new() { SaleItemId = sale.Items.Single().Id, Quantity = 1m } }
            },
            actingUserId: 7);

        // El pago inicial (40 USD × 50) sale íntegro por caja como reembolso físico del método 1.
        Assert.Equal(40.0000m, result.RefundedUSD);
        Assert.Equal(2000.00m, result.RefundedBsS);
        Assert.Equal(0m, result.BalanceAfterUSD);
        Assert.NotNull(result.CashTransactionId);

        var after = await closureService.GetExpectedTotalsByPaymentMethodAsync(DateTime.UtcNow);

        // Ingreso del pago inicial (2000 Bs.S) − reembolso físico (2000 Bs.S) = 0 esperado en gaveta.
        Assert.Equal(0m, after.Single(total => total.PaymentMethodId == 1).ExpectedAmountBsS);
        // Sin reembolso asociado, los demás métodos conservan su esperado intacto.
        Assert.Equal(500m, after.Single(total => total.PaymentMethodId == 3).ExpectedAmountBsS);
    }

    // ---------------------------------------------------------------- endpoints

    [Fact]
    public async Task CreditCancelEndpoint_ForwardsReasonAndMapsNotFound()
    {
        var salesService = new Mock<ISalesService>();
        salesService
            .Setup(service => service.CancelCreditSaleAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreditCancelResultDto { SaleId = 5, ReceivableId = 9, Status = ReceivableStatus.Anulada, SaleStatus = SaleStatus.Cancelled });

        var controller = CreateController(salesService.Object, "12", "Admin");

        var ok = await controller.CancelCreditSaleAsync(5, new CreditCancelRequest { Reason = "Devolución total" }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(ok.Result);

        salesService.Verify(
            service => service.CancelCreditSaleAsync(5, "Devolución total", 12, It.IsAny<CancellationToken>()),
            Times.Once);

        salesService
            .Setup(service => service.CancelCreditSaleAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Venta 404 no encontrada."));

        var missing = await controller.CancelCreditSaleAsync(404, new CreditCancelRequest(), CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(missing.Result);
    }

    [Fact]
    public async Task CreditReturnEndpoint_ForwardsActorAndReturnsResult()
    {
        CreditReturnRequest? captured = null;
        var salesService = new Mock<ISalesService>();
        salesService
            .Setup(service => service.RegisterCreditReturnAsync(It.IsAny<int>(), It.IsAny<CreditReturnRequest>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<int, CreditReturnRequest, int?, CancellationToken>((_, request, _, _) => captured = request)
            .ReturnsAsync(new CreditReturnResultDto { ReturnId = 3, SaleId = 5, ReturnedUSD = 25m });

        var controller = CreateController(salesService.Object, "12", "Cashier");

        var result = await controller.RegisterCreditReturnAsync(
            5,
            new CreditReturnRequest
            {
                Items = new List<CreditReturnItemRequest> { new() { SaleItemId = 1, Quantity = 1m } }
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(captured);
        Assert.Single(captured!.Items);
        salesService.Verify(
            service => service.RegisterCreditReturnAsync(5, It.IsAny<CreditReturnRequest>(), 12, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ---------------------------------------------------------------- harness

    private static (SalesService Service, Mock<IInventoryService> Inventory, Mock<ICashDrawerService> CashDrawer) CreateHarness(
        SalesDbContext context,
        decimal bcvRate = 60m)
    {
        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(inventoryService => inventoryService.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(bcvRate);
        inventory
            .Setup(inventoryService => inventoryService.GetSaleProductsByIdsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SaleProductInfoDto>());
        inventory
            .Setup(inventoryService => inventoryService.UpdateStockBatchAsync(It.IsAny<IEnumerable<StockDeductionRequest>>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

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
            creditService: new CreditService(context));

        return (service, inventory, cashDrawer);
    }

    private static List<StockDeductionRequest> CaptureStockRequests(Mock<IInventoryService> inventory)
    {
        var captured = new List<StockDeductionRequest>();
        inventory
            .Setup(inventoryService => inventoryService.UpdateStockBatchAsync(It.IsAny<IEnumerable<StockDeductionRequest>>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<StockDeductionRequest>, string?, bool, CancellationToken>((items, _, _, _) => captured.AddRange(items))
            .Returns(Task.CompletedTask);
        return captured;
    }

    private static SalesController CreateController(ISalesService salesService, string userId, string role)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(service => service.UserId).Returns(userId);

        var controller = new SalesController(salesService, currentUser.Object);
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
        return controller;
    }

    private static async Task<Customer> SeedCustomerAsync(SalesDbContext context, decimal creditLimitUsd = 1000m)
    {
        var customer = new Customer
        {
            CedulaOrRif = "V-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Cliente Devoluciones CxC",
            IsCreditEnabled = true,
            CreditLimitUSD = creditLimitUsd,
            PaymentTermDays = 30,
            CreditGraceDays = 3,
            CreditStatus = CreditStatus.Active,
            IsActive = true
        };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        return customer;
    }

    private static int _invoiceSequence = 8100;

    private static async Task<(Sale Sale, Receivable Receivable)> SeedCreditSaleAsync(
        SalesDbContext context,
        int customerId,
        decimal rate,
        params (int ProductId, string Name, decimal Quantity, decimal UnitPriceUsd, decimal SubtotalBsS)[] items)
    {
        var sale = new Sale
        {
            CustomerId = customerId,
            Status = SaleStatus.Completed,
            InvoiceNumber = System.Threading.Interlocked.Increment(ref _invoiceSequence),
            Date = DateTime.UtcNow,
            AppliedRate = rate,
            DeliveryStatus = SaleDeliveryStatus.Delivered
        };

        decimal subtotalUsd = 0m;
        decimal totalUsd = 0m;
        decimal totalBsS = 0m;
        foreach (var item in items)
        {
            decimal lineUsd = Math.Round(item.UnitPriceUsd * item.Quantity, 4, MidpointRounding.AwayFromZero);
            sale.Items.Add(new SaleItem
            {
                ProductId = item.ProductId,
                ProductName = item.Name,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPriceUsd,
                Subtotal = lineUsd,
                UnitPriceBsS = Math.Round(item.UnitPriceUsd * rate, 4, MidpointRounding.AwayFromZero),
                SubtotalBsS = item.SubtotalBsS
            });
            subtotalUsd += lineUsd;
            totalUsd += Math.Round(item.UnitPriceUsd * item.Quantity, 2, MidpointRounding.AwayFromZero);
            totalBsS += item.SubtotalBsS;
        }

        sale.Subtotal = Math.Round(subtotalUsd, 4, MidpointRounding.AwayFromZero);
        sale.TotalUSD = Math.Round(totalUsd, 2, MidpointRounding.AwayFromZero);
        sale.TotalBsS = Math.Round(totalBsS, 2, MidpointRounding.AwayFromZero);

        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        decimal originalUsd = Math.Round(
            items.Sum(item => PricingCalculator.ToUSD(item.SubtotalBsS, rate, 4)),
            4,
            MidpointRounding.AwayFromZero);
        var receivable = new Receivable
        {
            SaleId = sale.Id,
            CustomerId = customerId,
            OriginalUSD = originalUsd,
            BalanceUSD = originalUsd,
            OriginalBsS = sale.TotalBsS,
            HistoricalRate = rate,
            IssuedAtUtc = DateTime.UtcNow,
            DueDateUtc = DateTime.UtcNow.AddDays(10),
            Status = ReceivableStatus.Vigente
        };
        context.Receivables.Add(receivable);
        await context.SaveChangesAsync();

        return (sale, receivable);
    }

    private static async Task SeedSalePaymentAsync(
        SalesDbContext context,
        int saleId,
        decimal amountUsd,
        decimal amountBsS,
        decimal exchangeRate,
        int paymentMethodId,
        DateTime? createdAtUtc = null)
    {
        context.SalePayments.Add(new SalePayment
        {
            SaleId = saleId,
            PaymentMethodId = paymentMethodId,
            Amount = amountUsd,
            AmountBsS = amountBsS,
            ExchangeRate = exchangeRate,
            CreatedAt = createdAtUtc ?? DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Convierte la venta sembrada en MIXTA (S2): registra el pago inicial (SalePayment, igual que
    /// el checkout) y deja la obligación financiando solo el remanente no cubierto.
    /// </summary>
    private static async Task SeedMixedCreditSaleAsync(
        SalesDbContext context,
        Sale sale,
        Receivable receivable,
        decimal rate,
        decimal initialPaymentUsd,
        int initialPaymentMethodId)
    {
        decimal initialPaymentBsS = Math.Round(initialPaymentUsd * rate, 2, MidpointRounding.AwayFromZero);
        await SeedSalePaymentAsync(
            context,
            sale.Id,
            initialPaymentUsd,
            initialPaymentBsS,
            rate,
            initialPaymentMethodId,
            createdAtUtc: DateTime.UtcNow.AddMinutes(-5));

        decimal financedUsd = Math.Round(receivable.OriginalUSD - initialPaymentUsd, 4, MidpointRounding.AwayFromZero);
        receivable.OriginalUSD = financedUsd;
        receivable.BalanceUSD = financedUsd;
        receivable.OriginalBsS = Math.Round(sale.TotalBsS - initialPaymentBsS, 2, MidpointRounding.AwayFromZero);
        await context.SaveChangesAsync();
    }

    private static async Task SeedCreditPaymentAsync(
        SalesDbContext context,
        Receivable receivable,
        decimal amountUsd,
        decimal paymentRate,
        int paymentMethodId = 1,
        DateTime? receivedAtUtc = null)
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
            ReceivedAtUtc = receivedAtUtc ?? DateTime.UtcNow
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

    private static async Task SeedPaymentMethodsAsync(SalesDbContext context)
    {
        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true, IsActive = true, DisplayOrder = 1 },
            new PaymentMethod { Id = 2, Name = "Punto de Venta", IsCash = false, IsActive = true, DisplayOrder = 2 });
        await context.SaveChangesAsync();
    }
}
