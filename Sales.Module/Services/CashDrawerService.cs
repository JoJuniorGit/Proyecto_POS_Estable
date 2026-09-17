using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Core.Helpers;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public class CashDrawerService : ICashDrawerService
{
    private readonly SalesDbContext _context;

    public CashDrawerService(SalesDbContext context)
    {
        _context = context;
    }

    public async Task<CashDrawerSessionResponseDto?> GetActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        var session = await LoadActiveSessionEntityAsync(cancellationToken);
        return session == null ? null : MapSession(session);
    }

    // 8.5-M1: Sin Include de Transactions — liviano para accesos internos (cierre, apertura, rollover).
    private async Task<CashDrawerSession?> LoadActiveSessionEntityAsync(CancellationToken cancellationToken = default)
    {
        return await _context.CashDrawerSessions
            .FirstOrDefaultAsync(s => s.Status == CashDrawerStatus.Open, cancellationToken);
    }

    public async Task<CashDrawerSessionResponseDto?> GetActiveSessionWithTransactionsAsync(CancellationToken cancellationToken = default)
    {
        var session = await _context.CashDrawerSessions
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.Transactions)
                .ThenInclude(t => t.Sale)
            .FirstOrDefaultAsync(s => s.Status == CashDrawerStatus.Open, cancellationToken);

        if (session == null) return null;

        var transactions = session.Transactions
            .Where(t => t.IsPhysicalCash)
            .OrderByDescending(t => t.TransactionTime)
            .Select(MapTransaction)
            .ToList();

        return MapSession(session) with { Transactions = transactions };
    }

    public async Task<CashDrawerSessionResponseDto> GetOrCreateActiveSessionAsync(decimal currentExchangeRate, CancellationToken cancellationToken = default)
    {
        var session = await LoadActiveSessionEntityAsync(cancellationToken);
        if (session != null) return MapSession(session);

        var lastSession = await _context.CashDrawerSessions
            .OrderByDescending(s => s.ClosedAt ?? s.OpenedAt)
            .FirstOrDefaultAsync(cancellationToken);

        decimal carryOverBalance = 0;
        if (lastSession != null)
        {
            carryOverBalance = lastSession.ClosingBalanceLocal ?? await GetCurrentBalanceLocalAsync(lastSession.Id, cancellationToken);
        }

        try
        {
            return await OpenSessionAsync(carryOverBalance, currentExchangeRate, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            var concurrentSession = await LoadActiveSessionEntityAsync(cancellationToken);
            if (concurrentSession != null)
            {
                return MapSession(concurrentSession);
            }
            throw;
        }
    }

    public async Task<CashDrawerSessionResponseDto> OpenSessionAsync(decimal openingBalanceLocal, decimal currentExchangeRate, CancellationToken cancellationToken = default)
    {
        if (openingBalanceLocal < 0)
        {
            throw new ArgumentException("El saldo inicial de apertura de caja no puede ser negativo.", nameof(openingBalanceLocal));
        }

        if (currentExchangeRate <= 0)
        {
            throw new ArgumentException("La tasa de cambio para apertura de caja debe ser mayor a cero.", nameof(currentExchangeRate));
        }

        currentExchangeRate = PricingCalculator.RoundExchangeRateCeiling(currentExchangeRate);

        if (await GetActiveSessionAsync(cancellationToken) != null)
        {
            throw new InvalidOperationException("Ya existe una sesión de caja activa.");
        }

        var session = new CashDrawerSession
        {
            OpenedAt = DateTime.UtcNow,
            OpeningBalanceLocal = openingBalanceLocal,
            OpeningExchangeRate = currentExchangeRate,
            Status = CashDrawerStatus.Open
        };

        try
        {
            _context.CashDrawerSessions.Add(session);
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException("Ya existe una sesión de caja activa.", ex);
        }

        await AddTransactionAsync(
            session.Id,
            CashTransactionType.Income,
            CashTransactionSource.Opening,
            openingBalanceLocal,
            currentExchangeRate > 0 ? openingBalanceLocal / currentExchangeRate : 0,
            currentExchangeRate,
            "Monto de apertura de caja",
            cancellationToken: cancellationToken
        );

        return MapSession(session);
    }

    public async Task<CashDrawerSessionResponseDto> CloseSessionAsync(decimal actualClosingBalanceLocal, decimal currentExchangeRate, CancellationToken cancellationToken = default)
    {
        if (actualClosingBalanceLocal < 0)
        {
            throw new ArgumentException("El saldo final de arqueo de caja no puede ser negativo.", nameof(actualClosingBalanceLocal));
        }

        if (currentExchangeRate <= 0)
        {
            throw new ArgumentException("La tasa de cambio para cierre de caja debe ser mayor a cero.", nameof(currentExchangeRate));
        }

        currentExchangeRate = PricingCalculator.RoundExchangeRateCeiling(currentExchangeRate);

        bool isInMemory = _context.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true;
        var ambientTransaction = _context.Database.CurrentTransaction;
        bool ownsTransaction = !isInMemory && ambientTransaction == null;

        // 8.9-B4: misma estrategia condicional que AddTransactionAsync (reintento standalone,
        // sin anidar cuando la transacción la aporta una capa externa).
        async Task<CashDrawerSession> ExecuteWithinTransactionAsync()
        {
            IDbContextTransaction? ownTransaction = null;
            if (ownsTransaction)
            {
                ownTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }

            try
            {
                var session = await LoadActiveSessionEntityAsync(cancellationToken);
                if (session == null)
                {
                    throw new InvalidOperationException("No hay una sesión de caja activa para cerrar.");
                }

                // 8.5-A2: Serializar cierres concurrentes. El advisory lock garantiza que el segundo cierre
                // re-lea la sesión ya como Closed y NO registre un egreso Closing duplicado. La re-lectura se hace
                // CON tracking (la consulta refresca la instancia ya trackeada desde la primera lectura) para que
                // el flip a Status=Closed y los balances persistan en SaveChanges de la misma transacción.
                if (!isInMemory)
                {
                    await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", new object[] { session.Id }, cancellationToken);
                    session = await _context.CashDrawerSessions
                        .FirstOrDefaultAsync(s => s.Id == session.Id && s.Status == CashDrawerStatus.Open, cancellationToken);
                    if (session == null)
                    {
                        throw new InvalidOperationException("No hay una sesión de caja activa para cerrar.");
                    }
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
                    "Cierre de caja",
                    cancellationToken: cancellationToken
                );

                await _context.SaveChangesAsync(cancellationToken);

                if (ownTransaction != null)
                {
                    await ownTransaction.CommitAsync(cancellationToken);
                }

                return session;
            }
            catch
            {
                if (ownTransaction != null)
                {
                    await ownTransaction.RollbackAsync(cancellationToken);
                }
                throw;
            }
            finally
            {
                if (ownTransaction != null)
                {
                    await ownTransaction.DisposeAsync();
                }
            }
        }

        if (ownsTransaction)
        {
            var closedSession = await _context.Database.CreateExecutionStrategy().ExecuteAsync<object?, CashDrawerSession>(
                state: null,
                operation: (_, _, _) => ExecuteWithinTransactionAsync(),
                verifySucceeded: null,
                cancellationToken: cancellationToken);
            return MapSession(closedSession);
        }

        return MapSession(await ExecuteWithinTransactionAsync());
    }

    public async Task RolloverSessionAfterClosureAsync(decimal currentExchangeRate, CancellationToken cancellationToken = default)
    {
        var activeSession = await LoadActiveSessionEntityAsync(cancellationToken);
        if (activeSession == null) return;

        decimal carryOverBalance = await GetCurrentBalanceLocalAsync(activeSession.Id, cancellationToken);

        await CloseSessionAsync(carryOverBalance, currentExchangeRate, cancellationToken);
        await OpenSessionAsync(carryOverBalance, currentExchangeRate, cancellationToken);
    }

    public async Task<CashTransactionResponseDto> AddTransactionAsync(
        int sessionId,
        CashTransactionType type,
        CashTransactionSource source,
        decimal amountLocal,
        decimal amountUsd,
        decimal exchangeRate,
        string description,
        int? referenceId = null,
        bool isPhysicalCash = true,
        int? paymentMethodId = null,
        CancellationToken cancellationToken = default)
    {
        if (amountLocal <= 0 && source != CashTransactionSource.Closing && source != CashTransactionSource.Opening)
        {
            throw new ArgumentException("El monto de la transacción debe ser mayor a cero.", nameof(amountLocal));
        }

        bool isInMemory = _context.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true;
        var ambientTransaction = _context.Database.CurrentTransaction;
        bool ownsTransaction = !isInMemory && ambientTransaction == null;

        // 8.9-B4: si no hay transacción ambiente (llamada directa/standalone) la operación corre
        // bajo execution strategy para reintentar el bloque completo ante fallos transitorios.
        // Cuando existe transacción compartida (p.ej. el cobro de venta), el reintento lo aporta
        // la capa externa y aquí NO se abre otra transacción (evita anidar estrategias).
        async Task<CashTransaction> ExecuteWithinTransactionAsync()
        {
            IDbContextTransaction? ownTransaction = null;
            if (ownsTransaction)
            {
                ownTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }

            try
            {
                if (type == CashTransactionType.Expense && isPhysicalCash && source != CashTransactionSource.Closing)
                {
                    if (!isInMemory)
                    {
                        await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", new object[] { sessionId }, cancellationToken);
                    }

                    var currentBalance = await GetCurrentBalanceLocalAsync(sessionId, cancellationToken);
                    if (currentBalance < amountLocal)
                    {
                        throw new InvalidOperationException($"Saldo de efectivo en caja insuficiente para realizar el egreso. Disponible: {currentBalance:N2} Bs.S, Requerido: {amountLocal:N2} Bs.S.");
                    }
                }

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
                    IsPhysicalCash = isPhysicalCash,
                    PaymentMethodId = paymentMethodId
                };

                _context.CashTransactions.Add(transaction);
                await _context.SaveChangesAsync(cancellationToken);

                if (ownTransaction != null)
                {
                    await ownTransaction.CommitAsync(cancellationToken);
                }

                return transaction;
            }
            catch
            {
                if (ownTransaction != null)
                {
                    await ownTransaction.RollbackAsync(cancellationToken);
                }
                throw;
            }
            finally
            {
                if (ownTransaction != null)
                {
                    await ownTransaction.DisposeAsync();
                }
            }
        }

        if (ownsTransaction)
        {
            var transaction = await _context.Database.CreateExecutionStrategy().ExecuteAsync<object?, CashTransaction>(
                state: null,
                operation: (_, _, _) => ExecuteWithinTransactionAsync(),
                verifySucceeded: null,
                cancellationToken: cancellationToken);
            return MapTransaction(transaction);
        }

        return MapTransaction(await ExecuteWithinTransactionAsync());
    }

    public async Task<CashTransactionResponseDto> RecordSaleChangeAsync(
        int sessionId,
        decimal changeUsd,
        decimal changeBsS,
        decimal exchangeRate,
        string description,
        int saleId,
        int? cashPaymentMethodId,
        decimal pendingCashIncomeBsS = 0m,
        CancellationToken cancellationToken = default)
    {
        if (changeBsS <= 0m)
        {
            throw new ArgumentException("El vuelto debe ser mayor a cero.", nameof(changeBsS));
        }
        if (exchangeRate <= 0m)
        {
            throw new ArgumentException("La tasa de cambio debe ser mayor a cero.", nameof(exchangeRate));
        }

        bool isInMemory = _context.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true;

        if (!isInMemory)
        {
            if (_context.Database.CurrentTransaction == null)
            {
                throw new InvalidOperationException("RecordSaleChangeAsync debe ejecutarse dentro de la transacción compartida del cobro para que el advisory lock sea efectivo.");
            }
            await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", new object[] { sessionId }, cancellationToken);
        }

        var currentBalanceDb = await GetCurrentBalanceLocalAsync(sessionId, cancellationToken);
        decimal available = currentBalanceDb + pendingCashIncomeBsS;
        if (available < changeBsS)
        {
            throw new InvalidOperationException($"Saldo de efectivo en caja insuficiente para registrar el vuelto de la venta #{saleId}. Disponible: {available:N2} Bs.S, Vuelto requerido: {changeBsS:N2} Bs.S.");
        }

        var changeTx = new CashTransaction
        {
            SessionId = sessionId,
            Type = CashTransactionType.Expense,
            Source = CashTransactionSource.SalePayment,
            AmountUsd = Math.Round(changeUsd, 2, MidpointRounding.AwayFromZero),
            AmountLocal = Math.Round(changeBsS, 2, MidpointRounding.AwayFromZero),
            ExchangeRate = exchangeRate,
            IsPhysicalCash = true,
            SaleId = saleId,
            PaymentMethodId = cashPaymentMethodId,
            Description = description,
            TransactionTime = DateTime.UtcNow
        };

        _context.CashTransactions.Add(changeTx);
        return MapTransaction(changeTx);
    }

    public async Task<decimal> GetCurrentBalanceLocalAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _context.CashDrawerSessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new { s.OpeningBalanceLocal })
            .FirstOrDefaultAsync(cancellationToken);

        if (session == null) return 0;

        // 8.5-M1: Una sola consulta agregada con CASE condicional (ingresos físicos - egresos físicos),
        // en lugar de 3 round-trips separados. El movimiento de apertura se excluye porque su saldo ya
        // está contabilizado en OpeningBalanceLocal (evita doble conteo).
        var netCash = await _context.CashTransactions
            .AsNoTracking()
            .Where(t => t.SessionId == sessionId
                     && t.IsPhysicalCash
                     && t.Source != CashTransactionSource.Opening)
            .SumAsync(t => t.Type == CashTransactionType.Expense
                ? -(decimal?)t.AmountLocal
                : (decimal?)t.AmountLocal, cancellationToken) ?? 0m;

        return session.OpeningBalanceLocal + netCash;
    }

    public async Task<System.Collections.Generic.List<CashTransactionResponseDto>> GetHistoryAsync(int limit = 300, CancellationToken cancellationToken = default)
    {
        return await _context.CashTransactions
            .AsNoTracking()
            .Where(t => t.IsPhysicalCash)
            .OrderByDescending(t => t.TransactionTime)
            .Take(limit)
            .Select(t => new CashTransactionResponseDto
            {
                Id = t.Id,
                SessionId = t.SessionId,
                TransactionTime = t.TransactionTime,
                Type = t.Type,
                Source = t.Source,
                AmountUsd = t.AmountUsd,
                ExchangeRate = t.ExchangeRate,
                AmountLocal = t.AmountLocal,
                Description = t.Description,
                ReferenceId = t.ReferenceId,
                SaleId = t.SaleId,
                InvoiceNumber = t.Sale != null ? t.Sale.InvoiceNumber : null,
                IsPhysicalCash = t.IsPhysicalCash,
                PaymentMethodId = t.PaymentMethodId
            })
            .ToListAsync(cancellationToken);
    }

    private static CashDrawerSessionResponseDto MapSession(CashDrawerSession session) => new()
    {
        Id = session.Id,
        OpenedAt = session.OpenedAt,
        OpenedAtLocal = session.OpenedAtLocal,
        ClosedAt = session.ClosedAt,
        ClosedAtLocal = session.ClosedAtLocal,
        Status = session.Status,
        OpeningBalanceLocal = session.OpeningBalanceLocal,
        OpeningExchangeRate = session.OpeningExchangeRate,
        ClosingBalanceLocal = session.ClosingBalanceLocal,
        ClosingExchangeRate = session.ClosingExchangeRate,
        Transactions = session.Transactions.Select(MapTransaction).ToList()
    };

    private static CashTransactionResponseDto MapTransaction(CashTransaction transaction) => new()
    {
        Id = transaction.Id,
        SessionId = transaction.SessionId,
        TransactionTime = transaction.TransactionTime,
        TransactionTimeLocal = transaction.TransactionTimeLocal,
        Type = transaction.Type,
        Source = transaction.Source,
        AmountUsd = transaction.AmountUsd,
        ExchangeRate = transaction.ExchangeRate,
        AmountLocal = transaction.AmountLocal,
        Description = transaction.Description,
        ReferenceId = transaction.ReferenceId,
        SaleId = transaction.SaleId,
        InvoiceNumber = transaction.Sale?.InvoiceNumber,
        IsPhysicalCash = transaction.IsPhysicalCash,
        PaymentMethodId = transaction.PaymentMethodId
    };

}
