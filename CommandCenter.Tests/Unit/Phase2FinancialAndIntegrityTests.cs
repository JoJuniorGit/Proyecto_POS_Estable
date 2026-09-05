using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class Phase2FinancialAndIntegrityTests
{
    private (DailyClosureService service, SalesDbContext context) CreateClosureService()
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new DailyClosureService(context);
        return (service, context);
    }

    private (CashDrawerService service, SalesDbContext context) CreateDrawerService()
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new CashDrawerService(context);
        return (service, context);
    }

    [Fact]
    public async Task DailyClosure_IncludesDeactivatedPaymentMethod_WhenItHasSalesOnTheDay()
    {
        var (service, context) = CreateClosureService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        // Crear un método de pago nuevo y luego desactivarlo
        var legacyMethod = new PaymentMethod
        {
            Id = 99,
            Name = "Cupón Descontinuado",
            IsCash = false,
            IsActive = false,
            IsDeleted = false,
            DisplayOrder = 99
        };
        context.PaymentMethods.Add(legacyMethod);

        // Crear una venta completada en el día de hoy usando el método desactivado
        var now = DateTime.UtcNow;
        var sale = new Sale
        {
            Id = 901,
            Date = now,
            Status = SaleStatus.Completed,
            TotalUSD = 10m,
            TotalBsS = 500m,
            AppliedRate = 50m,
            Payments = new List<SalePayment>
            {
                new SalePayment
                {
                    PaymentMethodId = 99,
                    Amount = 10m,
                    AmountBsS = 500m
                }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        // 1. Verificar GetExpectedTotalsByPaymentMethodAsync
        var totals = await service.GetExpectedTotalsByPaymentMethodAsync(now);

        var legacyTotal = totals.FirstOrDefault(t => t.PaymentMethodId == 99);
        Assert.NotNull(legacyTotal);
        Assert.Equal("Cupón Descontinuado", legacyTotal.PaymentMethodName);
        Assert.Equal(500m, legacyTotal.ExpectedAmountBsS);

        // 2. Verificar CreateClosureAsync con cálculo automático
        var closure = new DailyClosure
        {
            ClosureDate = now,
            UserId = "Admin1",
            Observation = "Cierre con método desactivado pero con cobros"
        };

        var created = await service.CreateClosureAsync(closure);
        Assert.NotNull(created);
        var detail = created.Details.FirstOrDefault(d => d.PaymentMethodId == 99);
        Assert.NotNull(detail);
        Assert.Equal(500m, detail.ExpectedAmountBsS);
    }

    [Fact]
    public async Task DailyClosure_ExcludesDeactivatedPaymentMethod_WhenItHasZeroSales()
    {
        var (service, context) = CreateClosureService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var unusedDeactivated = new PaymentMethod
        {
            Id = 88,
            Name = "Método Inactivo Sin Ventas",
            IsCash = false,
            IsActive = false,
            IsDeleted = false,
            DisplayOrder = 88
        };
        context.PaymentMethods.Add(unusedDeactivated);
        await context.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var totals = await service.GetExpectedTotalsByPaymentMethodAsync(now);

        Assert.DoesNotContain(totals, t => t.PaymentMethodId == 88);
    }

    [Fact]
    public async Task CashDrawerService_GetCurrentBalanceLocalAsync_ExcludesOpeningTransactionToAvoidDoubleCounting()
    {
        var (service, context) = CreateDrawerService();

        // 1. Abrir sesión con 1,000 BsS de saldo inicial
        var session = await service.OpenSessionAsync(1000m, 50m);

        // AddOpeningTransaction se registra al abrir la sesión con Source == CashTransactionSource.Opening
        // Verificamos que exista la transacción de apertura
        var openingTx = await context.CashTransactions
            .FirstOrDefaultAsync(t => t.SessionId == session.Id && t.Source == CashTransactionSource.Opening);
        Assert.NotNull(openingTx);
        Assert.True(openingTx.IsPhysicalCash);
        Assert.Equal(1000m, openingTx.AmountLocal);

        // 2. Registrar venta física de 300 BsS
        await service.AddTransactionAsync(
            session.Id,
            CashTransactionType.Income,
            CashTransactionSource.SalePayment,
            300m,
            6m,
            50m,
            "Venta física",
            null,
            isPhysicalCash: true);

        // 3. Registrar gasto físico de 100 BsS
        await service.AddTransactionAsync(
            session.Id,
            CashTransactionType.Expense,
            CashTransactionSource.CashOut,
            100m,
            2m,
            50m,
            "Gasto caja menor",
            null,
            isPhysicalCash: true);

        // 4. Saldo esperado = OpeningBalanceLocal (1000) + Incomes físicos (300) - Expenses físicos (100) = 1200 BsS.
        // Si Source == Opening no se excluyera de los ingresos, daría 1000 + (1000 + 300) - 100 = 2200 (duplicación).
        decimal currentBalance = await service.GetCurrentBalanceLocalAsync(session.Id);

        Assert.Equal(1200m, currentBalance);
    }
}
