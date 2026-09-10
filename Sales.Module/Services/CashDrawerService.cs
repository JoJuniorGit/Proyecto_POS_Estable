using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Core.Logging;
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

    // 8.5-A6: Sin anti-patrón de construir SalesService con null!. El ISalesService se resuelve
    // perezosamente desde el ServiceProvider para no crear un ciclo Scoped con ISalesService,
    // que a su vez depende de ICashDrawerService (CashDrawerService) por constructor.
    public CashDrawerService(SalesDbContext context, IServiceProvider? serviceProvider = null)
    {
        _context = context;
        _serviceProvider = serviceProvider;
    }

    private ISalesService? GetSalesService()
    {
        return _serviceProvider?.GetService(typeof(ISalesService)) as ISalesService;
    }

    private Core.Interfaces.ISystemSettingsService? GetSettingsService()
    {
        return _serviceProvider?.GetService(typeof(Core.Interfaces.ISystemSettingsService)) as Core.Interfaces.ISystemSettingsService;
    }

    private async Task<decimal> GetCommissionPercentageAsync(bool isTransfer)
    {
        var key = isTransfer
            ? Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct
            : Core.Constants.SettingKeys.CashAdvanceCashCommissionPct;
        var fallback = isTransfer ? 7.0m : 10.0m;

        var settingsService = GetSettingsService();
        var value = settingsService != null
            ? await settingsService.GetSettingAsync(key)
            : null;

        return decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var percentage) && percentage > 0
            ? percentage
            : fallback;
    }

    public async Task<CashDrawerSession?> GetActiveSessionAsync()
    {
        // 8.5-M1: Sin Include de Transactions — liviano para accesos internos (cierre, apertura, rollover).
        return await _context.CashDrawerSessions
            .FirstOrDefaultAsync(s => s.Status == CashDrawerStatus.Open);
    }

    public async Task<CashDrawerSession?> GetActiveSessionWithTransactionsAsync()
    {
        return await _context.CashDrawerSessions
            .Include(s => s.Transactions)
                .ThenInclude(t => t.Sale)
            .FirstOrDefaultAsync(s => s.Status == CashDrawerStatus.Open);
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

        try
        {
            return await OpenSessionAsync(carryOverBalance, currentExchangeRate);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            var concurrentSession = await GetActiveSessionAsync();
            if (concurrentSession != null)
            {
                return concurrentSession;
            }
            throw;
        }
    }

    public async Task<CashDrawerSession> OpenSessionAsync(decimal openingBalanceLocal, decimal currentExchangeRate)
    {
        if (openingBalanceLocal < 0)
        {
            throw new ArgumentException("El saldo inicial de apertura de caja no puede ser negativo.", nameof(openingBalanceLocal));
        }

        if (currentExchangeRate <= 0)
        {
            throw new ArgumentException("La tasa de cambio para apertura de caja debe ser mayor a cero.", nameof(currentExchangeRate));
        }

        if (await GetActiveSessionAsync() != null)
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
            await _context.SaveChangesAsync();
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
            "Monto de apertura de caja"
        );

        return session;
    }

    public async Task<CashDrawerSession> CloseSessionAsync(decimal actualClosingBalanceLocal, decimal currentExchangeRate)
    {
        if (actualClosingBalanceLocal < 0)
        {
            throw new ArgumentException("El saldo final de arqueo de caja no puede ser negativo.", nameof(actualClosingBalanceLocal));
        }

        if (currentExchangeRate <= 0)
        {
            throw new ArgumentException("La tasa de cambio para cierre de caja debe ser mayor a cero.", nameof(currentExchangeRate));
        }

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
                ownTransaction = await _context.Database.BeginTransactionAsync();
            }

            try
            {
                var session = await GetActiveSessionAsync();
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
                    await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", session.Id);
                    session = await _context.CashDrawerSessions
                        .FirstOrDefaultAsync(s => s.Id == session.Id && s.Status == CashDrawerStatus.Open);
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
                    "Cierre de caja"
                );

                await _context.SaveChangesAsync();

                if (ownTransaction != null)
                {
                    await ownTransaction.CommitAsync();
                }

                return session;
            }
            catch
            {
                if (ownTransaction != null)
                {
                    await ownTransaction.RollbackAsync();
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
            return await _context.Database.CreateExecutionStrategy().ExecuteAsync(ExecuteWithinTransactionAsync);
        }

        return await ExecuteWithinTransactionAsync();
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
        bool isPhysicalCash = true,
        int? paymentMethodId = null)
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
                ownTransaction = await _context.Database.BeginTransactionAsync();
            }

            try
            {
                // H-API-4 & H-API-17: Validar que los egresos físicos no sobregiren el saldo real de la caja.
                // El chequeo de saldo y el INSERT ocurren en la MISMA transacción y se serializan con un
                // advisory lock por sesión para eliminar el TOCTOU (doble egreso concurrente, 8.5-A1).
                if (type == CashTransactionType.Expense && isPhysicalCash && source != CashTransactionSource.Closing)
                {
                    if (!isInMemory)
                    {
                        await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", sessionId);
                    }

                    var currentBalance = await GetCurrentBalanceLocalAsync(sessionId);
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
                await _context.SaveChangesAsync();

                if (ownTransaction != null)
                {
                    await ownTransaction.CommitAsync();
                }

                return transaction;
            }
            catch
            {
                if (ownTransaction != null)
                {
                    await ownTransaction.RollbackAsync();
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
            return await _context.Database.CreateExecutionStrategy().ExecuteAsync(ExecuteWithinTransactionAsync);
        }

        return await ExecuteWithinTransactionAsync();
    }

    /// <summary>
    /// Registra el vuelto de una venta como egreso físico (8.6-C1). Usa el advisory lock de la sesión y
    /// valida que el saldo disponible (saldo en BD + ingresos cash pendientes del tracker aún no persistidos)
    /// soporte el vuelto ANTES de insertarlo. Debe ejecutarse dentro de la transacción compartida del cobro.
    /// </summary>
    public async Task<CashTransaction> RecordSaleChangeAsync(
        int sessionId,
        decimal changeUsd,
        decimal changeBsS,
        decimal exchangeRate,
        string description,
        int saleId,
        int? cashPaymentMethodId,
        decimal pendingCashIncomeBsS = 0m)
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
            await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", sessionId);
        }

        // Saldo real en BD (persistido) + ingresos cash de la venta aún en el tracker (no persistidos).
        // El vuelto es un egreso físico y NO puede llevar la caja a saldo negativo (8.6-C1).
        var currentBalanceDb = await GetCurrentBalanceLocalAsync(sessionId);
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
        // Sin SaveChangesAsync: el cobro persiste todo junto dentro de su transacción compartida.
        return changeTx;
    }

    public async Task<decimal> GetCurrentBalanceLocalAsync(int sessionId)
    {
        var session = await _context.CashDrawerSessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new { s.OpeningBalanceLocal })
            .FirstOrDefaultAsync();

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
                : (decimal?)t.AmountLocal) ?? 0m;

        return session.OpeningBalanceLocal + netCash;
    }

    public async Task<System.Collections.Generic.List<CashTransaction>> GetHistoryAsync(int limit = 300)
    {
        return await _context.CashTransactions
            .AsNoTracking()
            .Where(t => t.IsPhysicalCash)
            .OrderByDescending(t => t.TransactionTime)
            .Take(limit)
            .Select(t => new CashTransaction
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
                Sale = t.Sale != null ? new Sale { Id = t.Sale.Id, InvoiceNumber = t.Sale.InvoiceNumber } : null,
                IsPhysicalCash = t.IsPhysicalCash,
                PaymentMethodId = t.PaymentMethodId
            })
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
            throw new ArgumentException("El monto de efectivo a entregar debe ser un número entero sin decimales.", nameof(requestedAmountLocal));
        }

        var availableCash = await GetCurrentBalanceLocalAsync(sessionId);
        var roundedRequested = Math.Round(requestedAmountLocal, 2, MidpointRounding.AwayFromZero);

        if (availableCash < roundedRequested)
        {
            throw new InvalidOperationException($"Saldo de efectivo en caja insuficiente. Disponible: {availableCash:N2} Bs.S, Requerido: {roundedRequested:N2} Bs.S.");
        }

        // 8.9-B4: adelanto de efectivo = venta contable (SalesDbContext) + movimientos de caja en la MISMA
        // transacción cross-DB. Se ejecuta bajo execution strategy para reintentar el bloque completo
        // (ambas bases) ante fallos transitorios, evitando escrituras a medias.
        return await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
        IDbContextTransaction? dbTransaction = null;
        if (_context.Database.ProviderName != null && !_context.Database.ProviderName.Contains("InMemory"))
        {
            dbTransaction = await _context.Database.BeginTransactionAsync();
        }

        try
        {
            string activeUserName = !string.IsNullOrWhiteSpace(userName) ? userName : "Usuario";
            decimal commissionPercentage = await GetCommissionPercentageAsync(isTransfer);
            decimal commissionAmountLocal = Math.Round(roundedRequested * (commissionPercentage / 100.0m), 2, MidpointRounding.AwayFromZero);
            decimal totalChargedLocal = roundedRequested + commissionAmountLocal;

            // 8.5-A5 (residual): se genera PRIMERO la venta contable, que ancla la tasa a la BCV del día
            // (misma política CompleteSale/HoldSale). La tasa anclada rige también para las transacciones
            // de caja asociadas, evitando tasas divergentes entre la venta y los movimientos de caja.
            decimal anchoredRate = exchangeRate;
            Sale? createdSale = null;
            var salesService = GetSalesService();
            if (salesService == null)
            {
                // 8.5-A6: Eliminado el anti-patrón de construir un SalesService con null!.
                // Sin el servicio DI no se genera la venta contable; en producción ISalesService
                // siempre está registrado en el contenedor.
                AppLogger.LogWarn("[CASH] ProcessCashAdvanceAsync no pudo obtener ISalesService del contenedor DI; no se generó la venta contable.");
            }
            else
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
                if (createdSale != null && createdSale.AppliedRate > 0m)
                {
                    anchoredRate = createdSale.AppliedRate;
                }
            }

            // 1. Egreso físico de caja con la descripción requerida: "Adelanto de Efectivo - {Metodo} {comision}% {usuario}"
            var expenseTx = await AddTransactionAsync(
                sessionId: sessionId,
                type: CashTransactionType.Expense,
                source: CashTransactionSource.CashAdvance,
                amountLocal: roundedRequested,
                amountUsd: anchoredRate > 0 ? roundedRequested / anchoredRate : 0,
                exchangeRate: anchoredRate,
                description: $"Adelanto de Efectivo - {paymentMethodName} {commissionPercentage:0}% {activeUserName}",
                isPhysicalCash: true,
                paymentMethodId: paymentMethodId
            );

            // 2. Ingreso contable por comisión (no físico, IsPhysicalCash = false)
            var incomeTx = await AddTransactionAsync(
                sessionId: sessionId,
                type: CashTransactionType.Income,
                source: CashTransactionSource.CashAdvance,
                amountLocal: commissionAmountLocal,
                amountUsd: anchoredRate > 0 ? commissionAmountLocal / anchoredRate : 0,
                exchangeRate: anchoredRate,
                description: $"Comisión Adelanto ({commissionPercentage:0}% {paymentMethodName}) - {activeUserName}",
                isPhysicalCash: false,
                paymentMethodId: paymentMethodId
            );

            if (dbTransaction != null)
            {
                await dbTransaction.CommitAsync();
                await dbTransaction.DisposeAsync();
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
                await dbTransaction.DisposeAsync();
            }
            throw;
        }
        });
    }
}
