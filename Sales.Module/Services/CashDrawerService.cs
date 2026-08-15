using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public class CashDrawerService : ICashDrawerService
{
    private readonly SalesDbContext _context;
    private readonly IServiceProvider? _serviceProvider;

    public CashDrawerService(SalesDbContext context, IServiceProvider? serviceProvider = null)
    {
        _context = context;
        _serviceProvider = serviceProvider;
    }

    private ISalesService? GetSalesService()
    {
        return _serviceProvider?.GetService(typeof(ISalesService)) as ISalesService;
    }

    public async Task<CashDrawerSession?> GetActiveSessionAsync()
    {
        var session = await _context.CashDrawerSessions
            .Include(s => s.Transactions)
                .ThenInclude(t => t.Sale)
            .FirstOrDefaultAsync(s => s.Status == CashDrawerStatus.Open);
            
        return session;
    }

    public async Task<CashDrawerSession> GetOrCreateActiveSessionAsync(decimal currentExchangeRate)
    {
        var session = await GetActiveSessionAsync();
        if (session != null) return session;

        var lastSession = await _context.CashDrawerSessions
            .Include(s => s.Transactions)
            .OrderByDescending(s => s.ClosedAt ?? s.OpenedAt)
            .FirstOrDefaultAsync();

        decimal carryOverBalance = 0;
        if (lastSession != null)
        {
            carryOverBalance = lastSession.ClosingBalanceLocal ?? await GetCurrentBalanceLocalAsync(lastSession.Id);
        }

        return await OpenSessionAsync(carryOverBalance, currentExchangeRate);
    }

    public async Task<CashDrawerSession> OpenSessionAsync(decimal openingBalanceLocal, decimal currentExchangeRate)
    {
        if (await GetActiveSessionAsync() != null)
        {
            throw new InvalidOperationException("There is already an active cash drawer session.");
        }

        var session = new CashDrawerSession
        {
            OpenedAt = DateTime.UtcNow,
            OpeningBalanceLocal = openingBalanceLocal,
            OpeningExchangeRate = currentExchangeRate,
            Status = CashDrawerStatus.Open
        };

        _context.CashDrawerSessions.Add(session);
        await _context.SaveChangesAsync();

        await AddTransactionAsync(
            session.Id,
            CashTransactionType.Income,
            CashTransactionSource.Opening,
            openingBalanceLocal,
            currentExchangeRate > 0 ? openingBalanceLocal / currentExchangeRate : 0,
            currentExchangeRate,
            "Monto de apertura de caja"
        );

        return session;
    }

    public async Task<CashDrawerSession> CloseSessionAsync(decimal actualClosingBalanceLocal, decimal currentExchangeRate)
    {
        var session = await GetActiveSessionAsync();
        if (session == null)
        {
            throw new InvalidOperationException("No active cash drawer session to close.");
        }

        session.ClosedAt = DateTime.UtcNow;
        session.ClosingBalanceLocal = actualClosingBalanceLocal;
        session.ClosingExchangeRate = currentExchangeRate;
        session.Status = CashDrawerStatus.Closed;

        await AddTransactionAsync(
            session.Id,
            CashTransactionType.Expense,
            CashTransactionSource.Closing,
            actualClosingBalanceLocal,
            currentExchangeRate > 0 ? actualClosingBalanceLocal / currentExchangeRate : 0,
            currentExchangeRate,
            "Cierre de caja"
        );

        await _context.SaveChangesAsync();
        return session;
    }

    public async Task RolloverSessionAfterClosureAsync(decimal currentExchangeRate)
    {
        var activeSession = await GetActiveSessionAsync();
        if (activeSession == null) return;

        // Conservar el saldo esperado en caja: se arrastra el saldo teórico (apertura + ingresos - egresos)
        // de la sesión que se cierra, sin depender de los montos declarados del arqueo (que solo quedan
        // registrados en el cierre para su auditoría).
        decimal carryOverBalance = await GetCurrentBalanceLocalAsync(activeSession.Id);

        await CloseSessionAsync(carryOverBalance, currentExchangeRate);
        await OpenSessionAsync(carryOverBalance, currentExchangeRate);
    }

    public async Task<CashTransaction> AddTransactionAsync(
        int sessionId,
        CashTransactionType type,
        CashTransactionSource source,
        decimal amountLocal,
        decimal amountUsd,
        decimal exchangeRate,
        string description,
        int? referenceId = null,
        bool isPhysicalCash = true)
    {
        var transaction = new CashTransaction
        {
            SessionId = sessionId,
            TransactionTime = DateTime.UtcNow,
            Type = type,
            Source = source,
            AmountUsd = amountUsd,
            AmountLocal = amountLocal,
            ExchangeRate = exchangeRate,
            Description = description,
            SaleId = referenceId,
            IsPhysicalCash = isPhysicalCash
        };

        _context.CashTransactions.Add(transaction);
        await _context.SaveChangesAsync();

        return transaction;
    }

    public async Task<decimal> GetCurrentBalanceLocalAsync(int sessionId)
    {
        var session = await _context.CashDrawerSessions
            .Include(s => s.Transactions)
            .FirstOrDefaultAsync(s => s.Id == sessionId);

        if (session == null) return 0;

        var physicalIncomes = session.Transactions
            .Where(t => t.Source != CashTransactionSource.Opening && t.Type == CashTransactionType.Income && t.IsPhysicalCash)
            .Sum(t => t.AmountLocal);

        var physicalExpenses = session.Transactions
            .Where(t => t.Type == CashTransactionType.Expense && t.IsPhysicalCash)
            .Sum(t => t.AmountLocal);

        return session.OpeningBalanceLocal + physicalIncomes - physicalExpenses;
    }

    public async Task<System.Collections.Generic.List<CashTransaction>> GetHistoryAsync(int limit = 300)
    {
        return await _context.CashTransactions
            .Include(t => t.Sale)
            .Where(t => t.IsPhysicalCash)
            .OrderByDescending(t => t.TransactionTime)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<CashAdvanceResultDto> ProcessCashAdvanceAsync(
        int sessionId,
        decimal requestedAmountLocal,
        int paymentMethodId,
        string paymentMethodName,
        bool isTransfer,
        decimal exchangeRate,
        int? cashierId = null,
        string? userName = null)
    {
        if (requestedAmountLocal <= 0)
        {
            throw new ArgumentException("El monto del adelanto debe ser mayor a cero.", nameof(requestedAmountLocal));
        }

        // Validación de integridad: el efectivo entregado solo acepta montos enteros (sin centavos).
        if (requestedAmountLocal % 1 != 0)
        {
            throw new InvalidOperationException("El monto de efectivo a entregar debe ser un número entero sin decimales.");
        }

        var availableCash = await GetCurrentBalanceLocalAsync(sessionId);
        var roundedRequested = Math.Round(requestedAmountLocal, 2, MidpointRounding.AwayFromZero);

        if (availableCash < roundedRequested)
        {
            throw new InvalidOperationException($"Saldo de efectivo en caja insuficiente. Disponible: {availableCash:N2} Bs.S, Requerido: {roundedRequested:N2} Bs.S.");
        }

        IDbContextTransaction? dbTransaction = null;
        if (_context.Database.ProviderName != null && !_context.Database.ProviderName.Contains("InMemory"))
        {
            dbTransaction = await _context.Database.BeginTransactionAsync();
        }

        try
        {
            string activeUserName = !string.IsNullOrWhiteSpace(userName) ? userName : "Usuario";
            decimal commissionPercentage = isTransfer ? 7.0m : 10.0m;
            decimal commissionAmountLocal = Math.Round(roundedRequested * (commissionPercentage / 100.0m), 2, MidpointRounding.AwayFromZero);
            decimal totalChargedLocal = roundedRequested + commissionAmountLocal;

            // 1. Egreso físico de caja con la descripción requerida: "Adelanto de Efectivo - {Metodo} {comision}% {usuario}"
            var expenseTx = await AddTransactionAsync(
                sessionId: sessionId,
                type: CashTransactionType.Expense,
                source: CashTransactionSource.CashAdvance,
                amountLocal: roundedRequested,
                amountUsd: exchangeRate > 0 ? roundedRequested / exchangeRate : 0,
                exchangeRate: exchangeRate,
                description: $"Adelanto de Efectivo - {paymentMethodName} {commissionPercentage:0}% {activeUserName}",
                isPhysicalCash: true
            );

            // 2. Ingreso contable por comisión (no físico, IsPhysicalCash = false)
            var incomeTx = await AddTransactionAsync(
                sessionId: sessionId,
                type: CashTransactionType.Income,
                source: CashTransactionSource.CashAdvance,
                amountLocal: commissionAmountLocal,
                amountUsd: exchangeRate > 0 ? commissionAmountLocal / exchangeRate : 0,
                exchangeRate: exchangeRate,
                description: $"Comisión Adelanto ({commissionPercentage:0}% {paymentMethodName}) - {activeUserName}",
                isPhysicalCash: false
            );

            Sale? createdSale = null;
            var salesService = GetSalesService();
            if (salesService != null)
            {
                createdSale = await salesService.CreateCashAdvanceSaleAsync(
                    requestedAmountLocal: roundedRequested,
                    commissionAmountLocal: commissionAmountLocal,
                    paymentMethodId: paymentMethodId,
                    paymentMethodName: paymentMethodName,
                    isTransfer: isTransfer,
                    exchangeRate: exchangeRate,
                    cashierId: cashierId,
                    userName: activeUserName,
                    existingTransaction: dbTransaction
                );
            }
            else
            {
                var tempSalesService = new SalesService(_context, null!, null!, this, null!);
                createdSale = await tempSalesService.CreateCashAdvanceSaleAsync(
                    requestedAmountLocal: roundedRequested,
                    commissionAmountLocal: commissionAmountLocal,
                    paymentMethodId: paymentMethodId,
                    paymentMethodName: paymentMethodName,
                    isTransfer: isTransfer,
                    exchangeRate: exchangeRate,
                    cashierId: cashierId,
                    userName: activeUserName,
                    existingTransaction: dbTransaction
                );
            }

            if (dbTransaction != null)
            {
                await dbTransaction.CommitAsync();
            }

            return new CashAdvanceResultDto
            {
                ExpenseTransaction = expenseTx,
                IncomeTransaction = incomeTx,
                RequestedAmountLocal = roundedRequested,
                CommissionAmountLocal = commissionAmountLocal,
                TotalChargedLocal = totalChargedLocal,
                CommissionPercentage = commissionPercentage,
                RelatedSaleId = createdSale?.Id,
                InvoiceNumber = createdSale?.InvoiceNumber
            };
        }
        catch
        {
            if (dbTransaction != null)
            {
                await dbTransaction.RollbackAsync();
            }
            throw;
        }
    }
}
