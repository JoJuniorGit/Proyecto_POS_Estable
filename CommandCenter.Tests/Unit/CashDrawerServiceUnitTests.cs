using System;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class CashDrawerServiceUnitTests
{
    private (CashDrawerService service, SalesDbContext context) CreateService()
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new CashDrawerService(context);
        return (service, context);
    }

    [Fact]
    public async Task GetOrCreateActiveSessionAsync_CreatesNewSession_WhenNoneIsOpen()
    {
        var (service, context) = CreateService();

        var session = await service.GetOrCreateActiveSessionAsync(50.00m);

        Assert.NotNull(session);
        Assert.True(session.Id > 0);
        Assert.Equal(CashDrawerStatus.Open, session.Status);
        Assert.Equal(50.00m, session.OpeningExchangeRate);
    }

    [Fact]
    public async Task OpenSessionAsync_WhenSessionAlreadyOpen_ThrowsInvalidOperationException()
    {
        var (service, context) = CreateService();

        await service.OpenSessionAsync(1000m, 50m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenSessionAsync(500m, 50m));
        Assert.Contains("already an active cash drawer session", ex.Message);
    }

    [Fact]
    public async Task CloseSessionAsync_SetsClosingBalances_AndStatusClosed()
    {
        var (service, context) = CreateService();

        var session = await service.OpenSessionAsync(500m, 50m);

        // Registrar una venta física de 200 BsS
        await service.AddTransactionAsync(session.Id, CashTransactionType.Income, CashTransactionSource.SalePayment, 200m, 4m, 50m, "Venta", null, isPhysicalCash: true);

        // Cerrar sesión con 700 BsS declarados
        var closed = await service.CloseSessionAsync(700m, 50m);

        Assert.Equal(CashDrawerStatus.Closed, closed.Status);
        Assert.Equal(700m, closed.ClosingBalanceLocal);
        Assert.Equal(50m, closed.ClosingExchangeRate);
        Assert.NotNull(closed.ClosedAt);
    }

    [Fact]
    public async Task GetCurrentBalanceLocalAsync_FiltersStrictlyByPhysicalCash()
    {
        var (service, context) = CreateService();

        var session = await service.OpenSessionAsync(1000m, 50m);

        // Transacción 1: Efectivo físico (+300 BsS)
        await service.AddTransactionAsync(session.Id, CashTransactionType.Income, CashTransactionSource.SalePayment, 300m, 6m, 50m, "Efectivo", null, isPhysicalCash: true);

        // Transacción 2: Pago Móvil electrónico (NO físico, +500 BsS)
        await service.AddTransactionAsync(session.Id, CashTransactionType.Income, CashTransactionSource.SalePayment, 500m, 10m, 50m, "Pago Movil", null, isPhysicalCash: false);

        // Transacción 3: Retiro físico (-200 BsS)
        await service.AddTransactionAsync(session.Id, CashTransactionType.Expense, CashTransactionSource.CashOut, 200m, 4m, 50m, "Gasto", null, isPhysicalCash: true);

        decimal physicalBalance = await service.GetCurrentBalanceLocalAsync(session.Id);

        // Balance físico esperado = 1000 + 300 - 200 = 1100 BsS (excluyendo los 500 electrónicos)
        Assert.Equal(1100m, physicalBalance);
    }

    [Fact]
    public async Task ProcessCashAdvanceAsync_WithIntegerAmount_CreatesPhysicalExpenseAndNonPhysicalCommissionIncome()
    {
        var (service, context) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var session = await service.OpenSessionAsync(5000m, 50m);

        // Adelanto de 1000 BsS vía Transferencia (comisión del 7% = 70 BsS)
        var result = await service.ProcessCashAdvanceAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 4, // Transferencia
            paymentMethodName: "Transferencia Bancaria",
            isTransfer: true,
            exchangeRate: 50m,
            cashierId: 1,
            userName: "Cajero Principal"
        );

        Assert.NotNull(result);
        Assert.Equal(1000m, result.RequestedAmountLocal);
        Assert.Equal(70m, result.CommissionAmountLocal);
        Assert.Equal(1070m, result.TotalChargedLocal);
        Assert.Equal(7.0m, result.CommissionPercentage);

        // Egreso físico
        Assert.True(result.ExpenseTransaction.IsPhysicalCash);
        Assert.Equal(1000m, result.ExpenseTransaction.AmountLocal);
        Assert.Equal(CashTransactionType.Expense, result.ExpenseTransaction.Type);

        // Ingreso por comisión no físico
        Assert.False(result.IncomeTransaction.IsPhysicalCash);
        Assert.Equal(70m, result.IncomeTransaction.AmountLocal);
        Assert.Equal(CashTransactionType.Income, result.IncomeTransaction.Type);

        // Saldo físico restante en caja: 5000 - 1000 = 4000 BsS
        decimal remainingCash = await service.GetCurrentBalanceLocalAsync(session.Id);
        Assert.Equal(4000m, remainingCash);
    }

    [Fact]
    public async Task ProcessCashAdvanceAsync_WithCentsAmount_ThrowsInvalidOperationException()
    {
        var (service, context) = CreateService();
        var session = await service.OpenSessionAsync(5000m, 50m);

        // Monto con centavos (100.50 BsS) no es permitido para entrega en efectivo físico
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessCashAdvanceAsync(
                sessionId: session.Id,
                requestedAmountLocal: 100.50m,
                paymentMethodId: 3,
                paymentMethodName: "Punto de Venta",
                isTransfer: false,
                exchangeRate: 50m
            ));

        Assert.Contains("número entero sin decimales", ex.Message);
    }

    [Fact]
    public async Task ProcessCashAdvanceAsync_WhenInsufficientCashInDrawer_ThrowsInvalidOperationException()
    {
        var (service, context) = CreateService();
        var session = await service.OpenSessionAsync(500m, 50m);

        // Intentar retirar 1000 BsS cuando solo hay 500 BsS en la gaveta
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessCashAdvanceAsync(
                sessionId: session.Id,
                requestedAmountLocal: 1000m,
                paymentMethodId: 3,
                paymentMethodName: "Punto de Venta",
                isTransfer: false,
                exchangeRate: 50m
            ));

        Assert.Contains("Saldo de efectivo en caja insuficiente", ex.Message);
    }
}
