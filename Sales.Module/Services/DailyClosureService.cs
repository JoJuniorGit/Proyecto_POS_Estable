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
    private const int UnattributedChangeMethodId = 0;

    // T3b2-fix (S7): el reembolso físico sin método se atribuye al primer método de efectivo,
    // igual que el vuelto sin método.
    private const int UnattributedRefundMethodId = 0;

    // 8.149 (SRE-02): mensaje exacto del conflicto de fecha duplicada (guard + índice único).
    public const string DuplicateClosureDateMessage = "Ya existe un cierre para esta fecha.";

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
        var salesTotals = await _context.SalePayments
            .AsNoTracking()
            .Where(sp => sp.Sale != null
                && sp.Sale.Status == SaleStatus.Completed
                && sp.Sale.Date >= startUtc
                && sp.Sale.Date < endUtc)
            .GroupBy(sp => sp.PaymentMethodId)
            .Select(g => new { PaymentMethodId = g.Key, TotalBsS = g.Sum(sp => sp.AmountBsS) })
            .ToDictionaryAsync(x => x.PaymentMethodId, x => x.TotalBsS, cancellationToken);

        // T3a (F2b, S3/S6): los abonos CxC cobrados dentro de la MISMA ventana del cierre computan
        // en lo esperado del método (el efectivo del abono ya entró a la gaveta como Income con
        // origen CreditPayment). La semántica de ventas/vueltos queda intacta: aquí solo se suman
        // abonos y el ajuste de vueltos (Source=SalePayment) no los toca.
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
            salesTotals.TryGetValue(creditPaymentTotal.Key, out decimal saleTotal);
            salesTotals[creditPaymentTotal.Key] = saleTotal + creditPaymentTotal.Value;
        }

        var changeTotals = await _context.CashTransactions
            .AsNoTracking()
            .Where(ct => ct.Type == CashTransactionType.Expense
                && ct.Source == CashTransactionSource.SalePayment
                && ct.IsPhysicalCash
                && ct.TransactionTime >= startUtc
                && ct.TransactionTime < endUtc)
            .GroupBy(ct => ct.PaymentMethodId)
            .Select(g => new { PaymentMethodId = g.Key ?? UnattributedChangeMethodId, TotalBsS = g.Sum(ct => ct.AmountLocal) })
            .ToDictionaryAsync(x => x.PaymentMethodId, x => x.TotalBsS, cancellationToken);

        // T3b2-fix (S7): los reembolsos por devolución de crédito (Expense, origen CreditRefund)
        // sacan de la gaveta el dinero que había entrado por el pago inicial o los abonos; se
        // descuentan del esperado del método con la MISMA semántica del vuelto: solo efectivo
        // físico y el reembolso sin método se atribuye al primer método de efectivo.
        var refundTotals = await _context.CashTransactions
            .AsNoTracking()
            .Where(ct => ct.Type == CashTransactionType.Expense
                && ct.Source == CashTransactionSource.CreditRefund
                && ct.IsPhysicalCash
                && ct.TransactionTime >= startUtc
                && ct.TransactionTime < endUtc)
            .GroupBy(ct => ct.PaymentMethodId)
            .Select(g => new { PaymentMethodId = g.Key ?? UnattributedRefundMethodId, TotalBsS = g.Sum(ct => ct.AmountLocal) })
            .ToDictionaryAsync(x => x.PaymentMethodId, x => x.TotalBsS, cancellationToken);

        // T2b (Paso 13 / 8.188, S2): caja chica. Cada gasto Active de la ventana se deduce del
        // metodo de efectivo de SU moneda (jamas del "primer efectivo" si no coincide). Se usa
        // AmountBsS porque todo el esperado del cierre esta expresado en Bs.S. Los gastos
        // Cancelled no se restan: su par Expense+Reversal ya netea 0 en la gaveta y la reversa
        // tampoco se suma aqui.
        var pettyCashExpenses = await _context.PettyCashExpenses
            .AsNoTracking()
            .Where(expense => expense.Status == PettyCashStatus.Active
                && expense.CreatedAtUtc >= startUtc
                && expense.CreatedAtUtc < endUtc)
            .Select(expense => new PettyCashExpenseAmount(expense.Currency, expense.AmountBsS))
            .ToListAsync(cancellationToken);

        var allMethods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(p => !p.IsDeleted)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync(cancellationToken);

        var relevantMethods = allMethods
            .Where(p => p.IsActive || salesTotals.ContainsKey(p.Id))
            .ToList();

        var firstCashMethodId = relevantMethods.FirstOrDefault(p => p.IsCash)?.Id;

        var pettyCashDeductions = ResolvePettyCashDeductions(pettyCashExpenses, relevantMethods);

        var result = new List<ExpectedTotalDto>();
        foreach (var method in relevantMethods)
        {
            salesTotals.TryGetValue(method.Id, out decimal expected);

            if (method.IsCash)
            {
                changeTotals.TryGetValue(method.Id, out decimal change);
                if (method.Id == firstCashMethodId)
                {
                    changeTotals.TryGetValue(UnattributedChangeMethodId, out decimal unattributedChange);
                    change += unattributedChange;
                }
                expected -= change;

                refundTotals.TryGetValue(method.Id, out decimal refund);
                if (method.Id == firstCashMethodId)
                {
                    refundTotals.TryGetValue(UnattributedRefundMethodId, out decimal unattributedRefund);
                    refund += unattributedRefund;
                }
                expected -= refund;

                pettyCashDeductions.TryGetValue(method.Id, out decimal pettyCash);
                expected -= pettyCash;
            }

            result.Add(new ExpectedTotalDto
            {
                PaymentMethodId = method.Id,
                PaymentMethodName = method.Name,
                ExpectedAmountBsS = expected
            });
        }

        return result;
    }

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

    /// <summary>
    /// T2b (S2): atribucion explicita de la caja chica por moneda. Si no existe un metodo de
    /// efectivo de la moneda del gasto, se registra el aviso y se omite solo esa deduccion
    /// (fail-safe) sin tocar los demas metodos. Entre metodos de efectivo de la misma moneda
    /// se usa el de menor Id, el mismo criterio con el que PettyCashService resuelve la gaveta.
    /// </summary>
    private static Dictionary<int, decimal> ResolvePettyCashDeductions(
        IReadOnlyList<PettyCashExpenseAmount> expenses,
        IReadOnlyList<PaymentMethod> relevantMethods)
    {
        var deductions = new Dictionary<int, decimal>();

        foreach (var currencyGroup in expenses.GroupBy(expense => expense.Currency))
        {
            PaymentMethod? target = ResolveCashMethodForCurrency(relevantMethods, currencyGroup.Key);
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

    private static PaymentMethod? ResolveCashMethodForCurrency(
        IReadOnlyList<PaymentMethod> methods,
        PettyCashCurrency currency)
    {
        string expectedCurrency = currency == PettyCashCurrency.Usd
            ? PaymentMethodCurrencyResolver.Usd
            : PaymentMethodCurrencyResolver.LocalCurrency;

        return methods
            .Where(method => method.IsCash
                && string.Equals(
                    PaymentMethodCurrencyResolver.Resolve(method.Name),
                    expectedCurrency,
                    StringComparison.Ordinal))
            .OrderBy(method => method.Id)
            .FirstOrDefault();
    }

    /// <summary>Proyeccion liviana del gasto para el calculo del esperado del cierre.</summary>
    private sealed record PettyCashExpenseAmount(PettyCashCurrency Currency, decimal AmountBsS);

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
            var user = await _context.Users.FindAsync(new object[] { userIdInt }, cancellationToken);
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
