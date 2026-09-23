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
        Assert.Contains("sesión de caja activa", ex.Message);
    }

    [Fact]
    public async Task CloseSessionAsync_SetsClosingBalances_AndStatusClosed()
    {
        var (service, context) = CreateService();

        var session = await service.OpenSessionAsync(500m, 50m);

        await service.AddTransactionAsync(session.Id, CashTransactionType.Income, CashTransactionSource.SalePayment, 200m, 4m, 50m, "Venta", null, isPhysicalCash: true);

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

        await service.AddTransactionAsync(session.Id, CashTransactionType.Income, CashTransactionSource.SalePayment, 300m, 6m, 50m, "Efectivo", null, isPhysicalCash: true);

        await service.AddTransactionAsync(session.Id, CashTransactionType.Income, CashTransactionSource.SalePayment, 500m, 10m, 50m, "Pago Movil", null, isPhysicalCash: false);

        await service.AddTransactionAsync(session.Id, CashTransactionType.Expense, CashTransactionSource.CashOut, 200m, 4m, 50m, "Gasto", null, isPhysicalCash: true);

        decimal physicalBalance = await service.GetCurrentBalanceLocalAsync(session.Id);

        Assert.Equal(1100m, physicalBalance);
    }

    [Fact]
    public async Task AddTransactionAsync_PhysicalExpense_WhenInsufficientCash_ThrowsInvalidOperationException()
    {
        var (service, context) = CreateService();
        var session = await service.OpenSessionAsync(200m, 50m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddTransactionAsync(
                sessionId: session.Id,
                type: CashTransactionType.Expense,
                source: CashTransactionSource.CashOut,
                amountLocal: 300m,
                amountUsd: 6m,
                exchangeRate: 50m,
                description: "Retiro de efectivo para gastos",
                referenceId: null,
                isPhysicalCash: true
            ));

        Assert.Contains("Saldo de efectivo en caja insuficiente", ex.Message);
    }

    [Fact]
    public async Task AddTransactionAsync_WithZeroOrNegativeAmount_ThrowsArgumentException()
    {
        var (service, context) = CreateService();
        var session = await service.OpenSessionAsync(500m, 50m);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddTransactionAsync(
                sessionId: session.Id,
                type: CashTransactionType.Income,
                source: CashTransactionSource.CashIn,
                amountLocal: -50m,
                amountUsd: -1m,
                exchangeRate: 50m,
                description: "Monto inválido"
            ));

        Assert.Contains("mayor a cero", ex.Message);
    }

    [Fact]
    public async Task CloseSessionAsync_SecondCloseWithoutActiveSession_ThrowsInvalidOperationException()
    {
        // 8.5-A2: Registro: el cierre de caja es único; un segundo cierre sin sesión activa debe fallar
        // (en PostgreSQL se garantiza además con advisory lock para el caso de cierres concurrentes).
        var (service, context) = CreateService();
        var session = await service.OpenSessionAsync(500m, 50m);

        var closed = await service.CloseSessionAsync(500m, 50m);
        Assert.Equal(CashDrawerStatus.Closed, closed.Status);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CloseSessionAsync(500m, 50m));
        Assert.Contains("sesión de caja activa", ex.Message);
    }

    [Fact]
    public async Task GetHistoryAsync_ProjectsInvoiceNumberFromSale_WithoutLoadingFullSaleEntity()
    {
        var (service, context) = CreateService();
        var session = await service.OpenSessionAsync(500m, 50m);

        var sale = new Sale
        {
            Id = 700,
            Status = SaleStatus.Completed,
            InvoiceNumber = 4242,
            Date = DateTime.UtcNow,
            AppliedRate = 50m
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        context.CashTransactions.Add(new CashTransaction
        {
            SessionId = session.Id,
            Type = CashTransactionType.Income,
            Source = CashTransactionSource.SalePayment,
            AmountUsd = 10m,
            AmountLocal = 500m,
            ExchangeRate = 50m,
            IsPhysicalCash = true,
            Description = "Pago en efectivo",
            TransactionTime = DateTime.UtcNow,
            SaleId = sale.Id
        });
        await context.SaveChangesAsync();

        var history = await service.GetHistoryAsync(10);

        var item = Assert.Single(history, t => t.SaleId == sale.Id);
        Assert.Equal(4242, item.InvoiceNumber);
        Assert.Equal(700, item.SaleId);
    }
}
