using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using CommandCenter.Tests.TestHelpers;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Remediación de la auditoría financiera bimonetaria: gaveta dual en el rollover, Reporte Z
/// reconstruido desde movimientos fechados, reembolso de crédito protegido, idempotencia
/// obligatoria y contrato de tasa operativa con techo a 2 decimales.
/// </summary>
public class FinancialAuditRemediationTests
{
    private const decimal Rate = 50m;

    private static SalesDbContext CreateContext() => TestDatabaseFactory.CreateSalesDbContext();

    private static async Task SeedMethodsAsync(SalesDbContext context)
    {
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
    }

    // ------------------------------------------------------------------ S3: Reporte Z

    [Fact]
    public async Task GetExpectedTotals_IncludesOnHoldDepositByPaymentDate()
    {
        using var context = CreateContext();
        await SeedMethodsAsync(context);
        var now = DateTime.UtcNow;

        // Apartado: la venta aún no se completa, pero el abono ya entró físicamente a la gaveta.
        context.Sales.Add(new Sale
        {
            Id = 900,
            Status = SaleStatus.OnHold,
            Date = now.AddDays(1),
            TotalUSD = 20m,
            TotalBsS = 1000m,
            AppliedRate = Rate
        });
        context.SalePayments.Add(new SalePayment
        {
            SaleId = 900,
            PaymentMethodId = 2,
            Amount = 14m,
            AmountBsS = 700m,
            ExchangeRate = Rate,
            CreatedAt = now
        });
        await context.SaveChangesAsync();

        var totals = await DailyClosureTestHelper.CreateService(context)
            .GetExpectedTotalsByPaymentMethodAsync(now);

        Assert.Equal(700m, totals.Single(total => total.PaymentMethodId == 2).ExpectedAmountBsS);
    }

    [Fact]
    public async Task GetExpectedTotals_IncludesCashAdvanceAndManualMovementsWithoutDoubleCounting()
    {
        using var context = CreateContext();
        await SeedMethodsAsync(context);
        var now = DateTime.UtcNow;

        context.Sales.Add(new Sale
        {
            Id = 901,
            Status = SaleStatus.Completed,
            Date = now,
            TotalUSD = 20m,
            TotalBsS = 1000m,
            AppliedRate = Rate
        });
        context.SalePayments.Add(new SalePayment
        {
            SaleId = 901,
            PaymentMethodId = 2,
            Amount = 20m,
            AmountBsS = 1000m,
            ExchangeRate = Rate,
            CreatedAt = now
        });

        context.CashTransactions.AddRange(
            // Egreso físico del adelanto de efectivo.
            new CashTransaction
            {
                SessionId = 1, Type = CashTransactionType.Expense, Source = CashTransactionSource.CashAdvance,
                AmountLocal = 300m, AmountUsd = 6m, ExchangeRate = Rate, IsPhysicalCash = true,
                PaymentMethodId = 2, TransactionTime = now
            },
            // Retiro manual de caja.
            new CashTransaction
            {
                SessionId = 1, Type = CashTransactionType.Expense, Source = CashTransactionSource.CashOut,
                AmountLocal = 100m, AmountUsd = 2m, ExchangeRate = Rate, IsPhysicalCash = true,
                PaymentMethodId = 2, TransactionTime = now
            },
            // Ingreso manual de caja.
            new CashTransaction
            {
                SessionId = 1, Type = CashTransactionType.Income, Source = CashTransactionSource.CashIn,
                AmountLocal = 50m, AmountUsd = 1m, ExchangeRate = Rate, IsPhysicalCash = true,
                PaymentMethodId = 2, TransactionTime = now
            },
            // Ingresos YA representados por SalePayments/ReceivablePayments: no deben duplicar.
            new CashTransaction
            {
                SessionId = 1, Type = CashTransactionType.Income, Source = CashTransactionSource.SalePayment,
                AmountLocal = 999m, AmountUsd = 19.98m, ExchangeRate = Rate, IsPhysicalCash = true,
                PaymentMethodId = 2, TransactionTime = now
            },
            new CashTransaction
            {
                SessionId = 1, Type = CashTransactionType.Income, Source = CashTransactionSource.CreditPayment,
                AmountLocal = 200m, AmountUsd = 4m, ExchangeRate = Rate, IsPhysicalCash = true,
                PaymentMethodId = 2, TransactionTime = now
            });
        await context.SaveChangesAsync();

        var totals = await DailyClosureTestHelper.CreateService(context)
            .GetExpectedTotalsByPaymentMethodAsync(now);

        // 1000 cobrado − 300 adelanto − 100 retiro + 50 ingreso manual.
        Assert.Equal(650m, totals.Single(total => total.PaymentMethodId == 2).ExpectedAmountBsS);
    }

    [Fact]
    public async Task GetExpectedTotals_DeductsActivePettyCashOnceAndIgnoresCancelledPair()
    {
        using var context = CreateContext();
        await SeedMethodsAsync(context);
        var now = DateTime.UtcNow;

        context.CashDrawerSessions.Add(new CashDrawerSession
        {
            Id = 1,
            OpenedAt = now.AddHours(-1),
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = 0m,
            OpeningExchangeRate = Rate
        });
        context.Sales.Add(new Sale
        {
            Id = 902,
            Status = SaleStatus.Completed,
            Date = now,
            TotalUSD = 20m,
            TotalBsS = 1000m,
            AppliedRate = Rate
        });
        context.SalePayments.Add(new SalePayment
        {
            SaleId = 902,
            PaymentMethodId = 2,
            Amount = 20m,
            AmountBsS = 1000m,
            ExchangeRate = Rate,
            CreatedAt = now
        });

        context.PettyCashExpenses.AddRange(
            new PettyCashExpense
            {
                Id = 1, VoucherNumber = 1, SessionId = 1, Category = PettyCashCategory.Varios,
                Currency = PettyCashCurrency.BsS, AmountBsS = 120m, AmountUsd = 2.4m, HistoricalRate = Rate,
                Concept = "Compra", Beneficiary = "Proveedor", ReferenceNumber = "R-1",
                CreatedByUserId = 1, CreatedAtUtc = now, Status = PettyCashStatus.Active
            },
            new PettyCashExpense
            {
                Id = 2, VoucherNumber = 2, SessionId = 1, Category = PettyCashCategory.Varios,
                Currency = PettyCashCurrency.BsS, AmountBsS = 50m, AmountUsd = 1m, HistoricalRate = Rate,
                Concept = "Anulado", Beneficiary = "Proveedor", ReferenceNumber = "R-2",
                CreatedByUserId = 1, CreatedAtUtc = now, Status = PettyCashStatus.Cancelled
            });

        context.CashTransactions.AddRange(
            new CashTransaction
            {
                SessionId = 1, Type = CashTransactionType.Expense, Source = CashTransactionSource.PettyCashExpense,
                AmountLocal = 120m, AmountUsd = 2.4m, ExchangeRate = Rate, IsPhysicalCash = true,
                PaymentMethodId = 2, TransactionTime = now
            },
            // Par gasto anulado + reversa: neto cero y fuera del OPEX.
            new CashTransaction
            {
                SessionId = 1, Type = CashTransactionType.Expense, Source = CashTransactionSource.PettyCashExpense,
                AmountLocal = 50m, AmountUsd = 1m, ExchangeRate = Rate, IsPhysicalCash = true,
                PaymentMethodId = 2, TransactionTime = now
            },
            new CashTransaction
            {
                SessionId = 1, Type = CashTransactionType.Income, Source = CashTransactionSource.PettyCashReversal,
                AmountLocal = 50m, AmountUsd = 1m, ExchangeRate = Rate, IsPhysicalCash = true,
                PaymentMethodId = 2, TransactionTime = now
            });
        await context.SaveChangesAsync();

        var totals = await DailyClosureTestHelper.CreateService(context)
            .GetExpectedTotalsByPaymentMethodAsync(now);

        // Solo el gasto Active se resta una vez (1000 − 120); el par anulado netea cero.
        Assert.Equal(880m, totals.Single(total => total.PaymentMethodId == 2).ExpectedAmountBsS);
    }

    // ------------------------------------------------------------------ S1: gaveta dual

    [Fact]
    public async Task LocalBalance_ExcludesUsdMovements_AndPhysicalBucketsStaySegregated()
    {
        using var context = CreateContext();
        await SeedMethodsAsync(context);

        context.CashDrawerSessions.Add(new CashDrawerSession
        {
            Id = 1,
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = 1000m,
            OpeningBalanceUsd = 50m,
            OpeningExchangeRate = Rate
        });
        context.CashTransactions.AddRange(
            new CashTransaction
            {
                SessionId = 1, Type = CashTransactionType.Income, Source = CashTransactionSource.SalePayment,
                AmountLocal = 500m, AmountUsd = 10m, ExchangeRate = Rate, IsPhysicalCash = true,
                PaymentMethodId = 2, TransactionTime = DateTime.UtcNow
            },
            new CashTransaction
            {
                SessionId = 1, Type = CashTransactionType.Income, Source = CashTransactionSource.SalePayment,
                AmountLocal = 300m, AmountUsd = 6m, ExchangeRate = Rate, IsPhysicalCash = true,
                PaymentMethodId = 1, TransactionTime = DateTime.UtcNow
            });
        await context.SaveChangesAsync();

        var drawer = new CashDrawerService(context);

        Assert.Equal(1500m, await drawer.GetCurrentBalanceLocalAsync(1));
        Assert.Equal(1500m, await drawer.GetPhysicalBalanceAsync(1, PettyCashCurrency.BsS));
        Assert.Equal(56m, await drawer.GetPhysicalBalanceAsync(1, PettyCashCurrency.Usd));
    }

    [Fact]
    public async Task Rollover_CarriesLocalAndUsdBucketsIntoTheNextSession()
    {
        using var context = CreateContext();
        await SeedMethodsAsync(context);

        context.CashDrawerSessions.Add(new CashDrawerSession
        {
            Id = 1,
            OpenedAt = DateTime.UtcNow.AddHours(-8),
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = 100m,
            OpeningBalanceUsd = 50m,
            OpeningExchangeRate = Rate
        });
        context.CashTransactions.AddRange(
            new CashTransaction
            {
                SessionId = 1, Type = CashTransactionType.Income, Source = CashTransactionSource.SalePayment,
                AmountLocal = 200m, AmountUsd = 4m, ExchangeRate = Rate, IsPhysicalCash = true,
                PaymentMethodId = 2, TransactionTime = DateTime.UtcNow
            },
            new CashTransaction
            {
                SessionId = 1, Type = CashTransactionType.Income, Source = CashTransactionSource.SalePayment,
                AmountLocal = 1000m, AmountUsd = 20m, ExchangeRate = Rate, IsPhysicalCash = true,
                PaymentMethodId = 1, TransactionTime = DateTime.UtcNow
            });
        await context.SaveChangesAsync();

        var drawer = new CashDrawerService(context);
        await drawer.RolloverSessionAfterClosureAsync(Rate);

        var previous = await context.CashDrawerSessions.AsNoTracking().SingleAsync(session => session.Id == 1);
        Assert.Equal(300m, previous.ClosingBalanceLocal);
        Assert.Equal(70m, previous.ClosingBalanceUsd);

        var current = await drawer.GetActiveSessionAsync();
        Assert.NotNull(current);
        Assert.Equal(300m, current!.OpeningBalanceLocal);
        Assert.Equal(70m, current.OpeningBalanceUsd);
        Assert.Equal(300m, await drawer.GetCurrentBalanceLocalAsync(current.Id));
        Assert.Equal(70m, await drawer.GetPhysicalBalanceAsync(current.Id, PettyCashCurrency.Usd));
    }

    // ------------------------------------------------------------------ S6/S7: contratos

    [Theory]
    [InlineData(36.450001, 36.46)]
    [InlineData(36.456789, 36.46)]
    [InlineData(36.45, 36.45)]
    [InlineData(804.63001, 804.64)]
    public void OperationalRate_IsAlwaysCeilingTwoDecimals(decimal received, decimal expected)
    {
        Assert.Equal(expected, PricingCalculator.RoundExchangeRateCeiling(received));
    }

    [Fact]
    public async Task CreditPaymentEndpoint_WithoutIdempotencyKey_Returns400()
    {
        var salesService = new Mock<ISalesService>();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(user => user.UserId).Returns("12");

        var controller = new SalesController(salesService.Object, currentUser.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var response = await controller.RegisterCreditPaymentAsync(
            new CreditPaymentRequest { CustomerId = 3, AmountUSD = 10m, PaymentMethodId = 1 },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Contains("Idempotency-Key", problem.Detail ?? string.Empty);
        salesService.Verify(
            service => service.RegisterCreditPaymentAsync(
                It.IsAny<CreditPaymentRequest>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PettyCashRegisterEndpoint_WithoutIdempotencyKey_Returns400()
    {
        var pettyCashService = new Mock<IPettyCashService>();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(user => user.UserId).Returns("12");
        currentUser.SetupGet(user => user.UserRole).Returns(UserRole.Cashier);

        var controller = new PettyCashController(pettyCashService.Object, currentUser.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var response = await controller.RegisterAsync(
            new PettyCashExpenseRequest
            {
                Currency = PettyCashCurrency.BsS,
                Amount = 10m,
                Category = PettyCashCategory.Varios,
                Concept = "Compra",
                Beneficiary = "Proveedor",
                ReferenceNumber = "R-1"
            },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Contains("Idempotency-Key", problem.Detail ?? string.Empty);
        pettyCashService.Verify(
            service => service.RegisterAsync(
                It.IsAny<PettyCashExpenseRequest>(), It.IsAny<int?>(), It.IsAny<UserRole?>(),
                It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
