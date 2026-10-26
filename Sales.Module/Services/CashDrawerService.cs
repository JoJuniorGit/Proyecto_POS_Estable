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
        var session = await _context.CashDrawerSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Status == CashDrawerStatus.Open, cancellationToken);
        return session == null ? null : MapSession(session);
    }

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
            .AsNoTracking()
            .OrderByDescending(s => s.ClosedAt ?? s.OpenedAt)
            .FirstOrDefaultAsync(cancellationToken);

        decimal carryOverBalance = 0;
        decimal carryOverBalanceUsd = 0;
        if (lastSession != null)
        {
            carryOverBalance = lastSession.ClosingBalanceLocal ?? await GetCurrentBalanceLocalAsync(lastSession.Id, cancellationToken);
            // La gaveta USD sobrevive al turno: sin conteo informado se transfiere el saldo
            // físico teórico por moneda, nunca el agregado local.
            carryOverBalanceUsd = lastSession.ClosingBalanceUsd ?? await GetPhysicalBalanceAsync(lastSession.Id, PettyCashCurrency.Usd, cancellationToken);
        }

        try
        {
            return await OpenSessionAsync(carryOverBalance, currentExchangeRate, cancellationToken, carryOverBalanceUsd);
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

    public async Task<CashDrawerSessionResponseDto> OpenSessionAsync(decimal openingBalanceLocal, decimal currentExchangeRate, CancellationToken cancellationToken = default, decimal openingBalanceUsd = 0m)
    {
        if (openingBalanceLocal < 0)
        {
            throw new ArgumentException("El saldo inicial de apertura de caja no puede ser negativo.", nameof(openingBalanceLocal));
        }

        if (openingBalanceUsd < 0m)
        {
            throw new ArgumentException("El saldo inicial de apertura de caja en USD no puede ser negativo.", nameof(openingBalanceUsd));
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
            OpeningBalanceUsd = openingBalanceUsd,
            OpeningExchangeRate = currentExchangeRate,
            Status = CashDrawerStatus.Open
        };

        bool isInMemory = _context.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true;
        var ambientTransaction = _context.Database.CurrentTransaction;
        bool ownsTransaction = !isInMemory && ambientTransaction == null;

        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? ownTransaction = null;
        if (ownsTransaction)
        {
            ownTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            if (_context.Database.IsNpgsql())
            {
                // SRE-02: Bloqueo consultivo en PostgreSQL para serializar la apertura concurrente de sesiones de caja
                // (tanto bajo transacción propia como bajo transacción ambiente). Provider-safe: se omite en SQLite/InMemory.
                await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(999888777)", cancellationToken);

                // Doble chequeo bajo el lock para evitar carrera
                if (await GetActiveSessionAsync(cancellationToken) != null)
                {
                    throw new InvalidOperationException("Ya existe una sesión de caja activa.");
                }
            }

            _context.CashDrawerSessions.Add(session);
            await _context.SaveChangesAsync(cancellationToken);

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

            if (ownTransaction != null)
            {
                await ownTransaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateException ex)
        {
            if (ownTransaction != null)
            {
                await ownTransaction.RollbackAsync(cancellationToken);
            }
            throw new InvalidOperationException("Ya existe una sesión de caja activa.", ex);
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

        return MapSession(session);
    }

    public async Task<CashDrawerSessionResponseDto> CloseSessionAsync(
        decimal actualClosingBalanceLocal,
        decimal currentExchangeRate,
        decimal? actualClosingBalanceUsd = null,
        CancellationToken cancellationToken = default)
    {
        if (actualClosingBalanceLocal < 0)
        {
            throw new ArgumentException("El saldo final de arqueo de caja no puede ser negativo.", nameof(actualClosingBalanceLocal));
        }

        // T2b (Paso 13 / 8.188, S2): arqueo fisico de la gaveta USD. Opcional para no romper las
        // llamadas previas (null = sin conteo USD, se conserva el comportamiento anterior).
        if (actualClosingBalanceUsd is < 0m)
        {
            throw new ArgumentException("El saldo final de arqueo de caja en USD no puede ser negativo.", nameof(actualClosingBalanceUsd));
        }

        if (currentExchangeRate <= 0)
        {
            throw new ArgumentException("La tasa de cambio para cierre de caja debe ser mayor a cero.", nameof(currentExchangeRate));
        }

        currentExchangeRate = PricingCalculator.RoundExchangeRateCeiling(currentExchangeRate);

        bool isInMemory = _context.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true;
        var ambientTransaction = _context.Database.CurrentTransaction;
        bool ownsTransaction = !isInMemory && ambientTransaction == null;

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
                // null conserva el comportamiento previo (sin conteo USD informado).
                session.ClosingBalanceUsd = actualClosingBalanceUsd;
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
        decimal carryOverBalanceUsd = await GetPhysicalBalanceAsync(activeSession.Id, PettyCashCurrency.Usd, cancellationToken);

        await CloseSessionAsync(
            carryOverBalance,
            currentExchangeRate,
            actualClosingBalanceUsd: carryOverBalanceUsd,
            cancellationToken: cancellationToken);
        await OpenSessionAsync(carryOverBalance, currentExchangeRate, cancellationToken, carryOverBalanceUsd);
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

        async Task<CashTransaction> ExecuteWithinTransactionAsync()
        {
            IDbContextTransaction? ownTransaction = null;
            if (ownsTransaction)
            {
                ownTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }

            try
            {
                if (source != CashTransactionSource.Closing)
                {
                    var targetSession = await _context.CashDrawerSessions
                        .AsNoTracking()
                        .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
                    if (targetSession != null && targetSession.Status != CashDrawerStatus.Open)
                    {
                        throw new InvalidOperationException("No se pueden registrar movimientos en una sesión de caja cerrada.");
                    }
                    if (targetSession == null && await _context.CashDrawerSessions.AsNoTracking().AnyAsync(cancellationToken))
                    {
                        throw new InvalidOperationException("La sesión de caja indicada no existe.");
                    }
                }

                if (isPhysicalCash && source != CashTransactionSource.Closing && !isInMemory)
                {
                    await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", new object[] { sessionId }, cancellationToken);
                }

                if (type == CashTransactionType.Expense && isPhysicalCash && source != CashTransactionSource.Closing)
                {
                    var currentBalance = await GetCurrentBalanceLocalAsync(sessionId, cancellationToken);
                    if (currentBalance < amountLocal)
                    {
                        throw new InvalidOperationException("Saldo de efectivo en caja insuficiente para realizar el egreso.");
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
            // 8.153 (SEC-06): sin exponer el efectivo disponible (arqueo ciego); el vuelto requerido
            // es dato del propio operador que completa la venta.
            throw new InvalidOperationException($"Saldo de efectivo en caja insuficiente para registrar el vuelto de la venta #{saleId}. Vuelto requerido: {MoneyFormat.N2(changeBsS)} Bs.S.");
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

        // El saldo local es estrictamente Bs.S: los movimientos de métodos USD no pueden
        // financiar un egreso local ni inflar el carry-over (gaveta física segregada).
        var currencyByMethodId = await LoadMethodCurrencyMapAsync(cancellationToken);
        var movements = await _context.CashTransactions
            .AsNoTracking()
            .Where(t => t.SessionId == sessionId
                     && t.IsPhysicalCash
                     && t.Source != CashTransactionSource.Opening)
            .Select(t => new { t.Type, t.AmountLocal, t.PaymentMethodId })
            .ToListAsync(cancellationToken);

        decimal net = 0m;
        foreach (var movement in movements)
        {
            string movementCurrency = movement.PaymentMethodId.HasValue
                && currencyByMethodId.TryGetValue(movement.PaymentMethodId.Value, out var methodCurrency)
                    ? methodCurrency
                    : PaymentMethodCurrencyResolver.LocalCurrency;

            if (!string.Equals(movementCurrency, PaymentMethodCurrencyResolver.LocalCurrency, StringComparison.Ordinal))
            {
                continue;
            }

            net += movement.Type == CashTransactionType.Expense ? -movement.AmountLocal : movement.AmountLocal;
        }

        return session.OpeningBalanceLocal + net;
    }

    /// <summary>
    /// Moneda canónica por método de pago, materializada en memoria porque
    /// <see cref="PaymentMethod.Currency"/> es una propiedad computada (NotMapped).
    /// </summary>
    private async Task<Dictionary<int, string>> LoadMethodCurrencyMapAsync(CancellationToken cancellationToken)
    {
        var methods = await _context.PaymentMethods
            .AsNoTracking()
            .Select(method => new { method.Id, method.Name })
            .ToListAsync(cancellationToken);

        var currencyByMethodId = new Dictionary<int, string>(methods.Count);
        foreach (var method in methods)
        {
            currencyByMethodId[method.Id] = PaymentMethodCurrencyResolver.Resolve(method.Name);
        }

        return currencyByMethodId;
    }

    /// <inheritdoc />
    public async Task<decimal> GetPhysicalBalanceAsync(int sessionId, PettyCashCurrency currency, CancellationToken cancellationToken = default)
    {
        var session = await _context.CashDrawerSessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new { s.OpeningBalanceLocal, s.OpeningBalanceUsd })
            .FirstOrDefaultAsync(cancellationToken);

        if (session == null) return 0m;

        var currencyByMethodId = await LoadMethodCurrencyMapAsync(cancellationToken);

        var movements = await _context.CashTransactions
            .AsNoTracking()
            .Where(t => t.SessionId == sessionId
                     && t.IsPhysicalCash
                     && t.Source != CashTransactionSource.Opening)
            .Select(t => new { t.Type, t.AmountLocal, t.AmountUsd, t.PaymentMethodId })
            .ToListAsync(cancellationToken);

        bool isUsd = currency == PettyCashCurrency.Usd;
        string expectedCurrency = isUsd ? PaymentMethodCurrencyResolver.Usd : PaymentMethodCurrencyResolver.LocalCurrency;

        decimal net = 0m;
        foreach (var movement in movements)
        {
            // Movimiento sin metodo de pago: bucket Bs.S (contrato de la gaveta local).
            string movementCurrency = movement.PaymentMethodId.HasValue
                && currencyByMethodId.TryGetValue(movement.PaymentMethodId.Value, out var methodCurrency)
                    ? methodCurrency
                    : PaymentMethodCurrencyResolver.LocalCurrency;

            if (!string.Equals(movementCurrency, expectedCurrency, StringComparison.Ordinal))
            {
                continue;
            }

            decimal amount = isUsd ? movement.AmountUsd : movement.AmountLocal;
            net += movement.Type == CashTransactionType.Expense ? -amount : amount;
        }

        return (isUsd ? session.OpeningBalanceUsd : session.OpeningBalanceLocal) + net;
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

    public async Task<Core.DTOs.PagedResultDto<CashTransactionResponseDto>> GetHistoryPagedAsync(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _context.CashTransactions
            .AsNoTracking()
            .Where(t => t.IsPhysicalCash);

        var totalCount = await query.CountAsync(cancellationToken);
        var hasMore = (page * pageSize) < totalCount;

        var items = await query
            .OrderByDescending(t => t.TransactionTime)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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

        return new Core.DTOs.PagedResultDto<CashTransactionResponseDto>(items, totalCount, hasMore);
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
        OpeningBalanceUsd = session.OpeningBalanceUsd,
        OpeningExchangeRate = session.OpeningExchangeRate,
        ClosingBalanceLocal = session.ClosingBalanceLocal,
        ClosingBalanceUsd = session.ClosingBalanceUsd,
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
