using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Core.Entities;
using Core.Helpers;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
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

    /// <inheritdoc />
    public async Task<CashDrawerSessionResponseDto> LockOpenSessionForWriteAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        // 8.202-H1 (S2): el lock debe adquirirse ANTES de leer el estado. La relectura posterior
        // garantiza que un cierre que ganó la carrera no deje pasar una escritura sobre la sesión
        // ya cerrada (TOCTOU del chequeo previo al lock).
        if (_context.Database.IsNpgsql())
        {
            if (_context.Database.CurrentTransaction is null)
            {
                throw new InvalidOperationException(
                    "La sesión de caja debe bloquearse dentro de una transacción activa para que el advisory lock sea efectivo.");
            }

            await _context.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock({0})",
                new object[] { sessionId },
                cancellationToken);
        }

        var session = await _context.CashDrawerSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            ?? throw new InvalidOperationException("La sesión de caja indicada no existe.");

        if (session.Status != CashDrawerStatus.Open)
        {
            throw new InvalidOperationException("No se pueden registrar movimientos en una sesión de caja cerrada.");
        }

        return MapSession(session);
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
        CancellationToken cancellationToken = default,
        CashTransactionIdempotency? idempotency = null)
    {
        if (amountLocal <= 0 && source != CashTransactionSource.Closing && source != CashTransactionSource.Opening)
        {
            throw new ArgumentException("El monto de la transacción debe ser mayor a cero.", nameof(amountLocal));
        }

        if (idempotency is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(idempotency.Key);
            ArgumentException.ThrowIfNullOrWhiteSpace(idempotency.RequestPath);
            if (idempotency.PayloadHash.Length == 0)
            {
                throw new ArgumentException("El hash del payload idempotente es obligatorio.", nameof(idempotency));
            }
        }

        bool isInMemory = _context.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true;
        var ambientTransaction = _context.Database.CurrentTransaction;
        bool ownsTransaction = !isInMemory && ambientTransaction == null;

        if (idempotency is not null && !ownsTransaction && !isInMemory)
        {
            throw new InvalidOperationException(
                "La idempotencia transaccional del movimiento manual requiere que el servicio controle su propia transacción.");
        }

        async Task<CashTransactionResponseDto> ExecuteWithinTransactionAsync()
        {
            IDbContextTransaction? ownTransaction = null;
            if (ownsTransaction)
            {
                ownTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }

            try
            {
                // 8.202-H1 (S1): el lock se toma ANTES de validar el estado de la sesión. Si un
                // cierre gana la carrera mientras esperamos, la relectura posterior lo detecta y
                // aborta la escritura en lugar de persistir sobre una sesión ya cerrada.
                if (isPhysicalCash && source != CashTransactionSource.Closing && _context.Database.IsNpgsql())
                {
                    if (_context.Database.CurrentTransaction is null)
                    {
                        throw new InvalidOperationException(
                            "La transacción de caja debe ejecutarse dentro de una transacción activa para que el advisory lock sea efectivo.");
                    }

                    await _context.Database.ExecuteSqlRawAsync(
                        "SELECT pg_advisory_xact_lock({0})",
                        new object[] { sessionId },
                        cancellationToken);
                }

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

                // 8.202-H2 (S4): replay bajo el lock de sesión. El chequeo del controlador ocurrió
                // antes del lock; esta relectura cierra la ventana entre dos peticiones concurrentes
                // con la MISMA clave sin ejecutar el efecto una segunda vez.
                if (idempotency is not null)
                {
                    var replay = await TryReadIdempotentTransactionAsync(idempotency, cancellationToken);
                    if (replay is not null)
                    {
                        if (ownTransaction is not null)
                        {
                            await ownTransaction.RollbackAsync(CancellationToken.None);
                        }
                        return replay;
                    }
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

                var dto = MapTransaction(transaction);

                if (idempotency is not null)
                {
                    // Misma transacción que el movimiento: si el insert choca con una clave
                    // concurrente, el rollback revierte también el CashTransaction.
                    await PersistIdempotentTransactionAsync(idempotency, dto, cancellationToken);
                }

                if (ownTransaction != null)
                {
                    await ownTransaction.CommitAsync(cancellationToken);
                }

                return dto;
            }
            catch (DbUpdateException ex) when (idempotency is not null && IsIdempotentRequestUniqueViolation(ex))
            {
                // La clave la ganó otra transacción: se revierte este movimiento y se replica la
                // respuesta ya confirmada. PostgreSQL bloquea el insert hasta que la otra tx
                // commitea, por lo que al llegar acá el ganador ya es visible.
                if (ownTransaction is not null)
                {
                    try
                    {
                        await ownTransaction.RollbackAsync(CancellationToken.None);
                    }
                    catch
                    {
                        // La conexión puede haberse perdido; el dispose cierra el intento.
                    }
                }

                _context.ChangeTracker.Clear();
                var replay = await TryReadIdempotentTransactionAsync(idempotency, CancellationToken.None);
                if (replay is not null)
                {
                    return replay;
                }

                throw;
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
            var dto = await _context.Database.CreateExecutionStrategy().ExecuteAsync<object?, CashTransactionResponseDto?>(
                state: null,
                operation: async (_, _, _) => (CashTransactionResponseDto?)await ExecuteWithinTransactionAsync(),
                verifySucceeded: async (_, _, verifyToken) =>
                {
                    bool exists = await IdempotentTransactionExistsAsync(idempotency, verifyToken);
                    return new ExecutionResult<CashTransactionResponseDto?>(exists, default);
                },
                cancellationToken: cancellationToken);

            if (dto is not null)
            {
                return dto;
            }

            // verifySucceeded=true: el commit sí ocurrió aunque la conexión se cortó. Se recupera
            // la respuesta persistida en lugar de repetir el movimiento.
            _context.ChangeTracker.Clear();
            dto = await TryReadIdempotentTransactionAsync(idempotency!, cancellationToken);
            return dto ?? throw new InvalidOperationException("No se pudo confirmar el resultado de la transacción de caja.");
        }

        return await ExecuteWithinTransactionAsync();
    }

    private async Task<CashTransactionResponseDto?> TryReadIdempotentTransactionAsync(
        CashTransactionIdempotency idempotency,
        CancellationToken cancellationToken)
    {
        var existing = await _context.IdempotentRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(
                record => record.Key == idempotency.Key && record.RequestPath == idempotency.RequestPath,
                cancellationToken);

        if (existing is null || existing.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return null;
        }

        if (existing.UserId != idempotency.UserId
            || existing.PayloadHash.Length != idempotency.PayloadHash.Length
            || !CryptographicOperations.FixedTimeEquals(existing.PayloadHash, idempotency.PayloadHash)
            || string.IsNullOrWhiteSpace(existing.ResponseBody))
        {
            return null;
        }

        return JsonSerializer.Deserialize<CashTransactionResponseDto>(existing.ResponseBody);
    }

    private async Task PersistIdempotentTransactionAsync(
        CashTransactionIdempotency idempotency,
        CashTransactionResponseDto dto,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        _context.IdempotentRequests.Add(new IdempotentRequest
        {
            Key = idempotency.Key,
            RequestPath = idempotency.RequestPath,
            PayloadHash = idempotency.PayloadHash,
            StatusCode = 200,
            ResponseBody = idempotency.SerializeResponse(dto),
            UserId = idempotency.UserId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(24)
        });

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> IdempotentTransactionExistsAsync(
        CashTransactionIdempotency? idempotency,
        CancellationToken cancellationToken)
    {
        if (idempotency is null)
        {
            return false;
        }

        return await _context.IdempotentRequests
            .AsNoTracking()
            .AnyAsync(
                record => record.Key == idempotency.Key && record.RequestPath == idempotency.RequestPath,
                cancellationToken);
    }

    private static bool IsIdempotentRequestUniqueViolation(DbUpdateException ex)
    {
        if (ex.InnerException is Npgsql.PostgresException postgresException)
        {
            return postgresException.SqlState == "23505"
                && (string.IsNullOrEmpty(postgresException.ConstraintName)
                    || postgresException.ConstraintName.Contains("IdempotentRequests", StringComparison.Ordinal));
        }

        return ex.Message.Contains("IdempotentRequests", StringComparison.Ordinal)
            || ex.InnerException?.Message.Contains("IdempotentRequests", StringComparison.Ordinal) == true;
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

            // 8.203 (auditoría R-04): no alcanza con tomar el lock; se revalida Open dentro de
            // la región bloqueada para que un cierre concurrente no reciba un vuelto tardío.
            await LockOpenSessionForWriteAsync(sessionId, cancellationToken);
        }
        else
        {
            var sessionStatus = await _context.CashDrawerSessions
                .AsNoTracking()
                .Where(session => session.Id == sessionId)
                .Select(session => (CashDrawerStatus?)session.Status)
                .FirstOrDefaultAsync(cancellationToken);
            if (sessionStatus != CashDrawerStatus.Open)
            {
                throw new InvalidOperationException("No se pueden registrar movimientos en una sesión de caja cerrada.");
            }
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

        // 8.202-H1 (S3): agregación server-side por (método, tipo). Solo se materializa una fila
        // por combinación, nunca el libro completo de la sesión, para acortar la retención del
        // advisory lock durante los chequeos de liquidez.
        var groups = await _context.CashTransactions
            .AsNoTracking()
            .Where(t => t.SessionId == sessionId
                     && t.IsPhysicalCash
                     && t.Source != CashTransactionSource.Opening)
            .GroupBy(t => new { t.PaymentMethodId, t.Type })
            .Select(group => new
            {
                group.Key.PaymentMethodId,
                group.Key.Type,
                AmountLocal = group.Sum(t => t.AmountLocal)
            })
            .ToListAsync(cancellationToken);

        if (groups.Count == 0)
        {
            return session.OpeningBalanceLocal;
        }

        // El saldo local es estrictamente Bs.S: los movimientos de métodos USD no pueden
        // financiar un egreso local ni inflar el carry-over (gaveta física segregada).
        var currencyByMethodId = await LoadMethodCurrencyMapAsync(cancellationToken);

        decimal net = 0m;
        foreach (var group in groups)
        {
            string movementCurrency = group.PaymentMethodId.HasValue
                && currencyByMethodId.TryGetValue(group.PaymentMethodId.Value, out var methodCurrency)
                    ? methodCurrency
                    : PaymentMethodCurrencyResolver.LocalCurrency;

            if (!string.Equals(movementCurrency, PaymentMethodCurrencyResolver.LocalCurrency, StringComparison.Ordinal))
            {
                continue;
            }

            net += group.Type == CashTransactionType.Expense ? -group.AmountLocal : group.AmountLocal;
        }

        return session.OpeningBalanceLocal + net;
    }

    /// <summary>
    /// Moneda canónica por método de pago. Usa la moneda persistida (8.201) y cae al nombre
    /// histórico solo para filas legadas sin Currency.
    /// </summary>
    private async Task<Dictionary<int, string>> LoadMethodCurrencyMapAsync(CancellationToken cancellationToken)
    {
        var methods = await _context.PaymentMethods
            .AsNoTracking()
            .Select(method => new { method.Id, method.Name, method.Currency })
            .ToListAsync(cancellationToken);

        var currencyByMethodId = new Dictionary<int, string>(methods.Count);
        foreach (var method in methods)
        {
            currencyByMethodId[method.Id] =
                PaymentMethodCurrencyResolver.ResolveStoredOrName(method.Currency, method.Name);
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

        // 8.202-H1 (S3): una sola proyección agrupada por (método, tipo) devuelve ambos buckets;
        // el saldo de la moneda pedida se resuelve sobre ese conjunto acotado.
        var groups = await _context.CashTransactions
            .AsNoTracking()
            .Where(t => t.SessionId == sessionId
                     && t.IsPhysicalCash
                     && t.Source != CashTransactionSource.Opening)
            .GroupBy(t => new { t.PaymentMethodId, t.Type })
            .Select(group => new
            {
                group.Key.PaymentMethodId,
                group.Key.Type,
                AmountLocal = group.Sum(t => t.AmountLocal),
                AmountUsd = group.Sum(t => t.AmountUsd)
            })
            .ToListAsync(cancellationToken);

        bool isUsd = currency == PettyCashCurrency.Usd;
        if (groups.Count == 0)
        {
            return isUsd ? session.OpeningBalanceUsd : session.OpeningBalanceLocal;
        }

        var currencyByMethodId = await LoadMethodCurrencyMapAsync(cancellationToken);
        string expectedCurrency = isUsd ? PaymentMethodCurrencyResolver.Usd : PaymentMethodCurrencyResolver.LocalCurrency;

        decimal net = 0m;
        foreach (var group in groups)
        {
            // Movimiento sin metodo de pago: bucket Bs.S (contrato de la gaveta local).
            string movementCurrency = group.PaymentMethodId.HasValue
                && currencyByMethodId.TryGetValue(group.PaymentMethodId.Value, out var methodCurrency)
                    ? methodCurrency
                    : PaymentMethodCurrencyResolver.LocalCurrency;

            if (!string.Equals(movementCurrency, expectedCurrency, StringComparison.Ordinal))
            {
                continue;
            }

            decimal amount = isUsd ? group.AmountUsd : group.AmountLocal;
            net += group.Type == CashTransactionType.Expense ? -amount : amount;
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
