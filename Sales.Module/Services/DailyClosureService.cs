using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public partial class DailyClosureService : IDailyClosureService
{
    // Filas legadas sin CreatedAt quedan por debajo de este piso y caen a la fecha de la venta.
    // Se usa un timestamp UTC explícito (no default(DateTime)) para que Npgsql pueda
    // parametrizarlo en timestamptz sin conflicto de Kind.
    private static readonly DateTime LegacyPaymentTimestampFloor =
        new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // 8.149 (SRE-02): mensaje exacto del conflicto de fecha duplicada (guard + índice único).
    public const string DuplicateClosureDateMessage = "Ya existe un cierre para esta fecha.";

    // 8.203 (auditoría R-05): esquema de advisory locks del cierre diario. Serializa el cierre
    // completo (guard de fecha + cálculo + persistencia + rollover de sesión) entre instancias.
    private const int ClosureAdvisoryLockClass = 5;
    private const int ClosureAdvisoryLockKey = 0;

    private readonly SalesDbContext _context;
    private readonly ITodayExchangeRateProvider _rateProvider;
    private readonly ICashDrawerService _cashDrawerService;
    private readonly ISystemSettingsService? _systemSettingsService;

    // 8.143: settings opcional para que los comprobantes de cierre honren CurrencyFormat; DI lo resuelve solo.
    public DailyClosureService(
        SalesDbContext context,
        ITodayExchangeRateProvider rateProvider,
        ICashDrawerService cashDrawerService,
        ISystemSettingsService? systemSettingsService = null)
    {
        _context = context;
        _rateProvider = rateProvider;
        _cashDrawerService = cashDrawerService;
        _systemSettingsService = systemSettingsService;
    }

    public async Task<List<ExpectedTotalDto>> GetExpectedTotalsByPaymentMethodAsync(DateTime dateUtc, CancellationToken cancellationToken = default)
    {
        var (startUtc, endUtc, _) = await ResolveClosureWindowAsync(dateUtc, cancellationToken);
        return await GetExpectedTotalsForWindowAsync(startUtc, endUtc, cancellationToken);
    }

    private async Task<List<ExpectedTotalDto>> GetExpectedTotalsForWindowAsync(
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken)
    {
        // Cobros de venta por la FECHA REAL del pago: incluye apartados (OnHold) y ventas
        // completadas por igual. Filas legadas sin CreatedAt caen a la fecha de la venta para
        // no perder el movimiento de la ventana.
        var expectedByMethod = await _context.SalePayments
            .AsNoTracking()
            .Where(sp => sp.Sale != null
                && ((sp.CreatedAt >= startUtc && sp.CreatedAt < endUtc)
                    || (sp.CreatedAt < LegacyPaymentTimestampFloor
                        && sp.Sale.Date >= startUtc
                        && sp.Sale.Date < endUtc)))
            .GroupBy(sp => sp.PaymentMethodId)
            .Select(g => new { PaymentMethodId = g.Key, TotalBsS = g.Sum(sp => sp.AmountBsS) })
            .ToDictionaryAsync(x => x.PaymentMethodId, x => x.TotalBsS, cancellationToken);

        // Abonos CxC por la fecha de recepción real (incluye métodos electrónicos).
        var creditPaymentTotals = await _context.ReceivablePayments
            .AsNoTracking()
            .Where(payment => payment.PaymentMethodId != null
                && payment.ReceivedAtUtc >= startUtc
                && payment.ReceivedAtUtc < endUtc)
            .GroupBy(payment => payment.PaymentMethodId!.Value)
            .Select(group => new { PaymentMethodId = group.Key, TotalBsS = group.Sum(payment => payment.AmountBsS) })
            .ToDictionaryAsync(x => x.PaymentMethodId, x => x.TotalBsS, cancellationToken);

        foreach (var creditPaymentTotal in creditPaymentTotals)
        {
            expectedByMethod[creditPaymentTotal.Key] =
                expectedByMethod.GetValueOrDefault(creditPaymentTotal.Key) + creditPaymentTotal.Value;
        }

        // Libro físico del turno. Solo aporta las fuentes que NO están ya representadas por los
        // cobros anteriores ni por la entidad de caja chica; de lo contrario el esperado se duplicaría:
        //   - SalePayment Income      -> ya sumado en SalePayments.
        //   - CreditPayment           -> ya sumado en ReceivablePayments.
        //   - PettyCash*              -> se resta una sola vez desde PettyCashExpenses (Active).
        // Aportan: vueltos (SalePayment Expense), CreditRefund, CashAdvance, CashIn/CashOut y
        // ManualAdjustment. Opening/Closing no son movimientos de turno.
        var ledgerMovements = await _context.CashTransactions
            .AsNoTracking()
            .Where(ct => ct.IsPhysicalCash
                && ct.Source != CashTransactionSource.Opening
                && ct.Source != CashTransactionSource.Closing
                && ct.TransactionTime >= startUtc
                && ct.TransactionTime < endUtc)
            .Select(ct => new { ct.Type, ct.Source, ct.AmountLocal, ct.PaymentMethodId })
            .ToListAsync(cancellationToken);

        var allMethods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(p => !p.IsDeleted)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync(cancellationToken);

        // Movimientos sin método: se atribuyen al primer efectivo (misma semántica histórica del
        // vuelto/reembolso sin atribución), nunca a un método no-cash.
        var firstCashMethodId = allMethods.FirstOrDefault(p => p.IsActive && p.IsCash)?.Id
            ?? allMethods.FirstOrDefault(p => p.IsCash)?.Id;

        decimal unattributedNetBsS = 0m;
        foreach (var movement in ledgerMovements)
        {
            if (movement.Source == CashTransactionSource.CreditPayment)
            {
                continue;
            }

            if (movement.Source == CashTransactionSource.SalePayment
                && movement.Type == CashTransactionType.Income)
            {
                continue;
            }

            if (movement.Source is CashTransactionSource.PettyCashExpense or CashTransactionSource.PettyCashReversal)
            {
                continue;
            }

            decimal signedAmount = movement.Type == CashTransactionType.Expense
                ? -movement.AmountLocal
                : movement.AmountLocal;

            if (movement.PaymentMethodId is int methodId)
            {
                expectedByMethod[methodId] = expectedByMethod.GetValueOrDefault(methodId) + signedAmount;
            }
            else
            {
                unattributedNetBsS += signedAmount;
            }
        }

        if (firstCashMethodId is int firstCashId && unattributedNetBsS != 0m)
        {
            expectedByMethod[firstCashId] = expectedByMethod.GetValueOrDefault(firstCashId) + unattributedNetBsS;
        }

        // Caja chica: cada gasto Active de la ventana se deduce UNA vez del método cash de su
        // moneda exacta (sin heurística del primer efectivo). Cancelados y su reversa netean cero
        // y no participan del esperado ni del renglón OPEX.
        var pettyCashExpenses = await _context.PettyCashExpenses
            .AsNoTracking()
            .Where(expense => expense.Status == PettyCashStatus.Active
                && expense.CreatedAtUtc >= startUtc
                && expense.CreatedAtUtc < endUtc)
            .Select(expense => new PettyCashExpenseAmount(expense.Currency, expense.AmountBsS))
            .ToListAsync(cancellationToken);

        foreach (var pettyCashDeduction in ResolvePettyCashDeductions(pettyCashExpenses, allMethods))
        {
            expectedByMethod[pettyCashDeduction.Key] =
                expectedByMethod.GetValueOrDefault(pettyCashDeduction.Key) - pettyCashDeduction.Value;
        }

        var relevantMethods = allMethods
            .Where(p => p.IsActive
                || expectedByMethod.ContainsKey(p.Id)
                || p.Id == firstCashMethodId)
            .ToList();

        return relevantMethods
            .Select(method => new ExpectedTotalDto
            {
                PaymentMethodId = method.Id,
                PaymentMethodName = method.Name,
                ExpectedAmountBsS = expectedByMethod.GetValueOrDefault(method.Id)
            })
            .ToList();
    }

    /// <summary>
    /// Atribución explícita de la caja chica por moneda. Si no existe un método de efectivo de la
    /// moneda del gasto se omite solo esa deducción (fail-safe) y se registra el aviso; entre
    /// métodos de la misma moneda se usa el de menor Id, el mismo criterio de PettyCashService.
    /// </summary>
    private static Dictionary<int, decimal> ResolvePettyCashDeductions(
        IReadOnlyList<PettyCashExpenseAmount> expenses,
        IReadOnlyList<PaymentMethod> methods)
    {
        var deductions = new Dictionary<int, decimal>();

        foreach (var currencyGroup in expenses.GroupBy(expense => expense.Currency))
        {
            string expectedCurrency = currencyGroup.Key == PettyCashCurrency.Usd
                ? PaymentMethodCurrencyResolver.Usd
                : PaymentMethodCurrencyResolver.LocalCurrency;

            PaymentMethod? target = methods
                .Where(method => method.IsCash
                    && string.Equals(
                        PaymentMethodCurrencyResolver.ResolveStoredOrName(method.Currency, method.Name),
                        expectedCurrency,
                        StringComparison.Ordinal))
                .OrderBy(method => method.Id)
                .FirstOrDefault();

            if (target is null)
            {
                Core.Logging.AppLogger.LogWarn(
                    $"[DailyClosureService] No existe un método de pago de efectivo {(currencyGroup.Key == PettyCashCurrency.Usd ? "USD" : "Bs.S")} para atribuir la caja chica del cierre; se omite la deducción de {currencyGroup.Sum(expense => expense.AmountBsS):0.00} Bs.S.",
                    "DailyClosure");
                continue;
            }

            deductions[target.Id] = currencyGroup.Sum(expense => expense.AmountBsS);
        }

        return deductions;
    }

    /// <summary>Proyección liviana del gasto para el cálculo del esperado del cierre.</summary>
    private sealed record PettyCashExpenseAmount(PettyCashCurrency Currency, decimal AmountBsS);

    private async Task<(DateTime StartUtc, DateTime EndExclusiveUtc, int? SessionId)> ResolveClosureWindowAsync(
        DateTime dateUtc,
        CancellationToken cancellationToken)
    {
        var lastClosure = await _context.DailyClosures
            .AsNoTracking()
            .OrderByDescending(dc => dc.ClosureDate)
            .FirstOrDefaultAsync(cancellationToken);

        var activeSession = await _context.CashDrawerSessions
            .AsNoTracking()
            .Where(s => s.Status == CashDrawerStatus.Open)
            .OrderByDescending(s => s.OpenedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var (startUtc, endUtc) = ClosureWindowResolver.Resolve(
            dateUtc,
            activeSession?.OpenedAt,
            lastClosure?.ClosureDate);

        // Quick win 8.188: la sesion abierta es la que el cierre cierra; su Id queda persistido
        // como ancla exacta del renglon OPEX al releer el cierre.
        return (startUtc, endUtc, activeSession?.Id);
    }

    public async Task<DailyClosureResponseDto?> GetClosureAsync(int id, CancellationToken cancellationToken = default)
    {
        var closure = await LoadClosureEntityAsync(id, cancellationToken);
        if (closure is null) return null;

        var pettyCashSummary = await BuildPersistedClosurePettyCashSummaryAsync(closure, cancellationToken);
        return ShiftReportMapper.MapClosure(closure, pettyCashSummary);
    }

    private async Task<DailyClosure?> LoadClosureEntityAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.DailyClosures
            .AsNoTracking()
            .AsSplitQuery()
            .Include(dc => dc.Details)
            .FirstOrDefaultAsync(dc => dc.Id == id, cancellationToken);
    }

    public async Task<DailyClosureResponseDto?> GetLatestClosureAsync(CancellationToken cancellationToken = default)
    {
        var closure = await _context.DailyClosures
            .AsNoTracking()
            .AsSplitQuery()
            .Include(dc => dc.Details)
            .OrderByDescending(dc => dc.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (closure is null) return null;

        var pettyCashSummary = await BuildPersistedClosurePettyCashSummaryAsync(closure, cancellationToken);
        return ShiftReportMapper.MapClosure(closure, pettyCashSummary);
    }

    /// <summary>
    /// T2b (S2): ventana del cierre ya persistido. Al releerlo no existe la sesion cerrada; se
    /// reconstruye con el cierre inmediatamente anterior como ancla (el rollover abre la sesion
    /// siguiente en ese mismo instante). La ventana siempre termina al final del dia local.
    /// Quick win 8.188: si el cierre conoce su sesion (SessionId), el renglon OPEX se calcula por
    /// identidad EXACTA de sesion (gastos Active del turno), sin depender de la ventana; los
    /// cierres legados (SessionId null) conservan el fallback de ventana.
    /// </summary>
    private async Task<PettyCashSummaryDto> BuildPersistedClosurePettyCashSummaryAsync(
        DailyClosure closure,
        CancellationToken cancellationToken)
    {
        if (closure.SessionId.HasValue)
        {
            var sessionExpenses = await _context.PettyCashExpenses
                .AsNoTracking()
                .Where(expense => expense.SessionId == closure.SessionId.Value
                    && expense.Status == PettyCashStatus.Active)
                .ToListAsync(cancellationToken);

            return ShiftReportMapper.MapPettyCashSummary(sessionExpenses);
        }

        DateTime? previousClosureDate = await _context.DailyClosures
            .AsNoTracking()
            .Where(candidate => candidate.Id != closure.Id
                && candidate.ClosureDate < closure.ClosureDate)
            .OrderByDescending(candidate => candidate.ClosureDate)
            .Select(candidate => (DateTime?)candidate.ClosureDate)
            .FirstOrDefaultAsync(cancellationToken);

        var (startUtc, endUtc) = ClosureWindowResolver.Resolve(
            closure.ClosureDate,
            activeSessionOpenedAtUtc: null,
            previousClosureDate);

        return await BuildPettyCashSummaryAsync(startUtc, endUtc, cancellationToken);
    }

    private async Task<PettyCashSummaryDto> BuildPettyCashSummaryAsync(
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken)
    {
        // Solo Active: los gastos anulados no forman parte del renglon OPEX (S2/S5).
        var expenses = await _context.PettyCashExpenses
            .AsNoTracking()
            .Where(expense => expense.Status == PettyCashStatus.Active
                && expense.CreatedAtUtc >= startUtc
                && expense.CreatedAtUtc < endUtc)
            .ToListAsync(cancellationToken);

        return ShiftReportMapper.MapPettyCashSummary(expenses);
    }

    public async Task<string?> GetCashierDisplayNameAsync(int userId, CancellationToken cancellationToken = default)
    {
        var user = await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user?.Name;
    }

    public async Task<CloseShiftResult> CreateClosureFromCommandAsync(
        CreateClosureCommand command,
        CancellationToken cancellationToken)
    {
        decimal exchangeRate = await _rateProvider.GetEffectiveTodayRateAsync(cancellationToken);
        if (exchangeRate <= 0)
        {
            throw new InvalidOperationException(
                "No se puede cerrar el turno: no existe una tasa BCV registrada para hoy. Registre la tasa del día antes de cerrar la caja.");
        }

        if (_context.Database.CurrentTransaction is not null)
        {
            return await ExecuteClosureCommandAsync(command, exchangeRate, cancellationToken);
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() =>
        {
            _context.ChangeTracker.Clear();
            return ExecuteClosureCommandAsync(command, exchangeRate, cancellationToken);
        });
    }

    private async Task<IDbContextTransaction?> OpenSerializableTransactionAsync(CancellationToken cancellationToken)
    {
        if (_context.Database.CurrentTransaction is not null)
        {
            return null;
        }

        if (_context.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true)
        {
            return null;
        }

        return await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    }

    private async Task<CloseShiftResult> ExecuteClosureCommandAsync(
        CreateClosureCommand command,
        decimal exchangeRate,
        CancellationToken cancellationToken)
    {
        // Residual 8.187-8.188: el conteo USD es opcional, pero si se informa debe ser un arqueo
        // fisico valido (mismo contrato que el cierre de sesion de caja).
        if (command.ActualClosingBalanceUsd is < 0m)
        {
            throw new ArgumentException(
                "El saldo final de arqueo de caja en USD no puede ser negativo.",
                nameof(command));
        }

        IDbContextTransaction? transaction = await OpenSerializableTransactionAsync(cancellationToken);

        try
        {
            // 8.203 (R-05): un lock de cierre global evita que dos fechas distintas intercalen el
            // rollover de la misma sesión (cerrar la sesión que abrió el otro cierre).
            if (_context.Database.IsNpgsql())
            {
                await _context.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock({0}, {1})",
                    new object[] { ClosureAdvisoryLockClass, ClosureAdvisoryLockKey },
                    cancellationToken);
            }

            // 8.149 (SRE-02): guard intra-transacción contra un segundo cierre para la misma
            // fecha; el índice único de ClosureDate es la defensa de último recurso ante carreras.
            bool closureDateTaken = await _context.DailyClosures
                .AsNoTracking()
                .AnyAsync(dc => dc.ClosureDate == command.ClosureDateUtc, cancellationToken);
            if (closureDateTaken)
            {
                throw new InvalidOperationException(DuplicateClosureDateMessage);
            }

            // T2b (S2): la ventana del OPEX es la misma que usa el esperado; se resuelve una sola
            // vez ANTES de persistir para no depender del estado posterior al guardado/rollover.
            // Quick win 8.188: la sesion abierta tambien viaja para anclar el cierre.
            var (pettyCashWindowStartUtc, pettyCashWindowEndUtc, pettyCashSessionId) =
                await ResolveClosureWindowAsync(command.ClosureDateUtc, cancellationToken);

            // Residual 8.187-8.188: el esperado USD se congela ANTES del rollover, desde el bucket
            // fisico USD de la sesion que este cierre clausura. Se reutiliza el calculo canonico
            // por moneda de la gaveta (PaymentMethodCurrencyResolver), sin heuristica de nombres.
            // Sin conteo informado, o sin sesion activa, los tres campos quedan null y se conserva
            // el contrato previo; nunca se inventa un esperado en cero.
            decimal? actualClosingBalanceUsd = command.ActualClosingBalanceUsd is { } actualUsd
                ? Math.Round(actualUsd, 4, MidpointRounding.AwayFromZero)
                : null;
            decimal? expectedClosingBalanceUsd = null;
            decimal? differenceUsd = null;
            if (actualClosingBalanceUsd.HasValue && pettyCashSessionId.HasValue)
            {
                decimal physicalUsd = await _cashDrawerService.GetPhysicalBalanceAsync(
                    pettyCashSessionId.Value,
                    PettyCashCurrency.Usd,
                    cancellationToken);
                expectedClosingBalanceUsd = Math.Round(physicalUsd, 4, MidpointRounding.AwayFromZero);
                differenceUsd = Math.Round(
                    actualClosingBalanceUsd.Value - expectedClosingBalanceUsd.Value,
                    4,
                    MidpointRounding.AwayFromZero);
            }

            var expectedTotals = await GetExpectedTotalsForWindowAsync(
                pettyCashWindowStartUtc,
                pettyCashWindowEndUtc,
                cancellationToken);
            var expectedById = expectedTotals.ToDictionary(e => e.PaymentMethodId);

            ValidateDeclaredMethods(command.Declarations, expectedById);

            var details = new List<ClosureDetail>();
            var reportDetails = new List<ShiftReportDetailResult>();
            BuildDeclaredDetails(command.Declarations, expectedById, exchangeRate, details, reportDetails);

            var existingMethodIds = details.Select(d => d.PaymentMethodId).ToHashSet();
            var methodEntities = await _context.PaymentMethods
                .AsNoTracking()
                .Where(p => !p.IsDeleted)
                .ToDictionaryAsync(p => p.Id, cancellationToken);

            MergeMissingMethodsWithReport(details, reportDetails, expectedTotals, existingMethodIds, methodEntities, exchangeRate);

            var resolvedUser = await ResolveUserDetailsAsync(command.UserId, cancellationToken);

            var dailyClosure = new DailyClosure
            {
                ClosureDate = command.ClosureDateUtc,
                UserId = resolvedUser.UserId,
                Observation = resolvedUser.Observation,
                ExchangeRate = exchangeRate,
                SessionId = pettyCashSessionId,
                ExpectedClosingBalanceUsd = expectedClosingBalanceUsd,
                ActualClosingBalanceUsd = actualClosingBalanceUsd,
                DifferenceUsd = differenceUsd,
                Details = details
            };

            RecalculateTotals(dailyClosure);

            var savedClosure = await PersistClosureCoreAsync(
                dailyClosure,
                pettyCashWindowStartUtc,
                pettyCashWindowEndUtc,
                cancellationToken);
            await _cashDrawerService.RolloverSessionAfterClosureAsync(exchangeRate, cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            // 8.7-B5: los comprobantes se escriben DESPUÉS del commit, fuera de la transacción Serializable.
            await WriteClosedClosureReceiptsAsync(savedClosure, cancellationToken);

            return new CloseShiftResult(
                savedClosure.Id,
                resolvedUser.CashierName,
                resolvedUser.CashierCedula,
                savedClosure.ClosureDate,
                exchangeRate,
                reportDetails,
                expectedClosingBalanceUsd,
                actualClosingBalanceUsd,
                differenceUsd);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task<(string UserId, string CashierName, string CashierCedula, string Observation)> ResolveUserDetailsAsync(
        string? inputUserId, CancellationToken cancellationToken)
    {
        string userId = inputUserId ?? "Cajero";
        string cashierName = "Cajero Activo";
        string cashierCedula = "V-00000000";
        string observation = "";

        if (int.TryParse(userId, out int userIdInt))
        {
            var user = await _context.Users.AsNoTracking()
                .Where(u => u.Id == userIdInt)
                .Select(u => new { u.Id, u.Name, u.Cedula, u.Username })
                .FirstOrDefaultAsync(cancellationToken);
            if (user != null)
            {
                cashierName = user.Name;
                cashierCedula = user.Cedula ?? user.Username ?? "V-00000000";
                userId = user.Id.ToString();
                observation = cashierCedula;
            }
        }

        return (userId, cashierName, cashierCedula, observation);
    }

    private async Task<DailyClosureResponseDto> PersistClosureCoreAsync(
        DailyClosure closure,
        DateTime pettyCashWindowStartUtc,
        DateTime pettyCashWindowEndUtc,
        CancellationToken cancellationToken)
    {
        _context.DailyClosures.Add(closure);
        await _context.SaveChangesAsync(cancellationToken);

        var persisted = (await LoadClosureEntityAsync(closure.Id, cancellationToken))!;
        var pettyCashSummary = await BuildPettyCashSummaryAsync(
            pettyCashWindowStartUtc,
            pettyCashWindowEndUtc,
            cancellationToken);

        return ShiftReportMapper.MapClosure(persisted, pettyCashSummary);
    }
}
