using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Receipts;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// T2a (Paso 13 / 8.188, S4): política pura de autorización jerárquica del gasto de caja chica.
/// Cashier (o rol desconocido/Driver) siempre requiere supervisor; Manager registra directo hasta
/// el umbral y por encima exige Admin; Admin opera directo/auto-autoriza incluso sobre el umbral
/// (sin deadlock con un único Admin). El umbral se parsea con InvariantCulture y cae al default
/// seguro de 50.00 USD ante ausencia o valor ilegible.
/// </summary>
public static class PettyCashAuthorizationPolicy
{
    public const decimal DefaultThresholdUsd = 50.00m;
    public const string ThresholdSettingKey = "PettyCash:AuthorizationThresholdUsd";

    public static decimal ResolveThresholdUsd(string? rawValue)
    {
        if (!string.IsNullOrWhiteSpace(rawValue)
            && decimal.TryParse(rawValue.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)
            && parsed >= 0m)
        {
            return parsed;
        }

        return DefaultThresholdUsd;
    }

    public static bool RequiresAuthorization(decimal amountUsd, UserRole? actingRole, decimal thresholdUsd)
    {
        if (actingRole == UserRole.Admin)
        {
            return false;
        }

        if (actingRole == UserRole.Manager)
        {
            return amountUsd > thresholdUsd;
        }

        return true;
    }

    /// <summary>S5: la anulación exige autorización para Manager y Cashier; solo Admin anula directo.</summary>
    public static bool RequiresCancellationAuthorization(UserRole? actingRole) => actingRole != UserRole.Admin;
}

/// <summary>
/// T2a (Paso 13 / 8.188, S1/S2/S3/S5): servicio de caja chica. El registro congela la tasa BCV
/// server-side con valoración bimonetaria, exige liquidez física de la moneda del gasto, resuelve
/// el método de pago de efectivo de esa gaveta y crea el gasto + su movimiento Expense en UNA
/// transacción bajo advisory lock de la sesión (con replay por IdempotencyKey). La anulación es
/// lógica y compensa con un Income que copia exactamente AmountBsS, AmountUsd y HistoricalRate
/// del gasto (jamás relee la tasa): efecto neto 0 en ambas gavetas.
/// </summary>
public class PettyCashService : IPettyCashService
{
    private readonly SalesDbContext _context;
    private readonly ICashDrawerService _cashDrawerService;
    private readonly IInventoryService _inventoryService;
    private readonly ISystemSettingsService? _settingsService;
    private readonly ILogger<PettyCashService>? _logger;

    public PettyCashService(
        SalesDbContext context,
        ICashDrawerService cashDrawerService,
        IInventoryService inventoryService,
        ISystemSettingsService? settingsService = null,
        ILogger<PettyCashService>? logger = null)
    {
        _context = context;
        _cashDrawerService = cashDrawerService;
        _inventoryService = inventoryService;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PettyCashExpenseResultDto> RegisterAsync(
        PettyCashExpenseRequest request,
        int? actingUserId = null,
        UserRole? actingRole = null,
        int? authorizedByUserId = null,
        int? authorizationRequestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Amount <= 0m)
        {
            throw new ArgumentException("El monto del gasto de caja chica debe ser mayor a cero.", nameof(request));
        }

        if (!Enum.IsDefined(request.Currency))
        {
            throw new ArgumentException("La moneda del gasto de caja chica no es válida.", nameof(request));
        }

        if (!Enum.IsDefined(request.Category))
        {
            throw new ArgumentException("La categoría del gasto de caja chica no es válida.", nameof(request));
        }

        string concept = NormalizeRequired(request.Concept, 500, "El concepto del gasto de caja chica es obligatorio y no puede exceder 500 caracteres.");
        string beneficiary = NormalizeRequired(request.Beneficiary, 150, "El beneficiario o proveedor del gasto de caja chica es obligatorio y no puede exceder 150 caracteres.");
        string referenceNumber = NormalizeRequired(request.ReferenceNumber, 100, "El número de referencia del comprobante es obligatorio y no puede exceder 100 caracteres.");
        string? idempotencyKey = NormalizeOptional(request.IdempotencyKey, 100, "La clave de idempotencia del gasto excede la longitud máxima permitida (100 caracteres).");

        if (actingUserId is null)
        {
            throw new InvalidOperationException("Se requiere un usuario autenticado para registrar el gasto de caja chica.");
        }

        decimal exchangeRate = await ResolveTodayRateAsync(cancellationToken);
        (decimal amountBsS, decimal amountUsd) = ComputeDualValuation(request.Currency, request.Amount, exchangeRate);
        decimal thresholdUsd = await ResolveThresholdAsync(cancellationToken);
        bool requiresAuthorization = PettyCashAuthorizationPolicy.RequiresAuthorization(amountUsd, actingRole, thresholdUsd);

        int retryAttempt = 0;
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Reintento transitorio: el tracker se limpia para re-leer el estado fresco (mismo
            // contrato del cierre de venta a crédito y del registro de abonos).
            if (retryAttempt++ > 0 || _context.Database.IsRelational())
            {
                _context.ChangeTracker.Clear();
            }

            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                : null;

            try
            {
                // El replay se resuelve antes de crear sesión o validar liquidez: un gasto ya
                // persistido debe replicarse tal cual, sin efectos nuevos.
                var replay = await FindExistingExpenseAsync(idempotencyKey, cancellationToken);
                if (replay is not null)
                {
                    return replay;
                }

                var session = await _cashDrawerService.GetOrCreateActiveSessionAsync(exchangeRate, cancellationToken);

                // Advisory lock de gaveta: serializa registros y anulaciones concurrentes del
                // mismo turno para que liquidez, autorización y correlativo se resuelvan sobre
                // estado estable. Provider-safe: se omite en proveedores no-Npgsql (tests).
                if (_context.Database.IsNpgsql())
                {
                    await _context.Database.ExecuteSqlRawAsync(
                        "SELECT pg_advisory_xact_lock({0})",
                        new object[] { session.Id },
                        cancellationToken);
                }

                int cashMethodId = await ResolveCashPaymentMethodIdAsync(request.Currency, cancellationToken);

                decimal physicalBalance = await _cashDrawerService.GetPhysicalBalanceAsync(session.Id, request.Currency, cancellationToken);
                decimal requiredAmount = request.Currency == PettyCashCurrency.Usd ? amountUsd : amountBsS;
                if (physicalBalance < requiredAmount)
                {
                    // 8.153 (SEC-06): sin exponer el efectivo disponible (arqueo ciego); el monto
                    // requerido es dato del propio operador que asienta el gasto.
                    string currencyName = request.Currency == PettyCashCurrency.Usd ? "USD" : "Bs.S";
                    throw new InvalidOperationException(
                        $"Saldo de efectivo físico en {currencyName} insuficiente en la gaveta para registrar el gasto de caja chica. Monto requerido: {MoneyFormat.N2(requiredAmount)} {currencyName}.");
                }

                int authorizerUserId;
                if (requiresAuthorization)
                {
                    authorizerUserId = await ResolveAuthorizerAsync(
                        authorizationRequestId, authorizedByUserId, actingUserId.Value,
                        session.Id, request, amountBsS, amountUsd, cancellationToken);
                }
                else
                {
                    // Admin/Manager dentro del umbral: auto-autorización registrada para auditoría.
                    authorizerUserId = actingUserId.Value;
                }

                long voucherNumber = await ResolveNextVoucherNumberAsync(cancellationToken);

                var expense = new PettyCashExpense
                {
                    VoucherNumber = voucherNumber,
                    SessionId = session.Id,
                    Category = request.Category,
                    Currency = request.Currency,
                    AmountBsS = amountBsS,
                    AmountUsd = amountUsd,
                    HistoricalRate = exchangeRate,
                    Concept = concept,
                    Beneficiary = beneficiary,
                    ReferenceNumber = referenceNumber,
                    CreatedByUserId = actingUserId.Value,
                    CreatedAtUtc = DateTime.UtcNow,
                    AuthorizedByUserId = authorizerUserId,
                    AuthorizationRequestId = requiresAuthorization ? authorizationRequestId : null,
                    IdempotencyKey = idempotencyKey,
                    Status = PettyCashStatus.Active
                };

                var movement = new CashTransaction
                {
                    SessionId = session.Id,
                    TransactionTime = DateTime.UtcNow,
                    Type = CashTransactionType.Expense,
                    Source = CashTransactionSource.PettyCashExpense,
                    AmountUsd = amountUsd,
                    ExchangeRate = exchangeRate,
                    AmountLocal = amountBsS,
                    Description = $"Gasto de caja chica {FormatVoucher(voucherNumber)}: {concept}",
                    IsPhysicalCash = true,
                    PaymentMethodId = cashMethodId,
                    PettyCashExpense = expense
                };

                _context.PettyCashExpenses.Add(expense);
                _context.CashTransactions.Add(movement);
                await _context.SaveChangesAsync(cancellationToken);

                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                _logger?.LogInformation(
                    "[PettyCashService] Gasto de caja chica #{ExpenseId} (vale {Voucher}): {Amount} {Currency} / {AmountUsd} USD a tasa {Rate}; sesión #{SessionId}, autorizado por usuario #{AuthorizerId}.",
                    expense.Id, FormatVoucher(voucherNumber), request.Amount, request.Currency, amountUsd, exchangeRate, session.Id, authorizerUserId);

                return MapExpense(expense, movement.Id, isReplay: false);
            }
            catch
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }

                throw;
            }
        });
    }

    /// <inheritdoc />
    public async Task<PettyCashExpenseResultDto> CancelAsync(
        int expenseId,
        string? reason,
        int? actingUserId = null,
        UserRole? actingRole = null,
        int? authorizedByUserId = null,
        CancellationToken cancellationToken = default)
    {
        string cancellationReason = NormalizeRequired(reason, 500, "El motivo de la anulación del gasto es obligatorio y no puede exceder 500 caracteres.");

        if (actingUserId is null)
        {
            throw new InvalidOperationException("Se requiere un usuario autenticado para anular el gasto de caja chica.");
        }

        int retryAttempt = 0;
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (retryAttempt++ > 0 || _context.Database.IsRelational())
            {
                _context.ChangeTracker.Clear();
            }

            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                : null;

            try
            {
                // Pre-lectura liviana para conocer la sesión a bloquear; el estado definitivo se
                // re-lee DESPUÉS del lock (rastreado) para no decidir sobre filas obsoletas.
                int sessionId = await _context.PettyCashExpenses
                    .AsNoTracking()
                    .Where(expense => expense.Id == expenseId)
                    .Select(expense => (int?)expense.SessionId)
                    .FirstOrDefaultAsync(cancellationToken)
                    ?? throw new KeyNotFoundException($"El gasto de caja chica #{expenseId} no existe.");

                if (_context.Database.IsNpgsql())
                {
                    await _context.Database.ExecuteSqlRawAsync(
                        "SELECT pg_advisory_xact_lock({0})",
                        new object[] { sessionId },
                        cancellationToken);
                }

                var expense = await _context.PettyCashExpenses
                    .Include(candidate => candidate.Session)
                    .FirstOrDefaultAsync(candidate => candidate.Id == expenseId, cancellationToken)
                    ?? throw new KeyNotFoundException($"El gasto de caja chica #{expenseId} no existe.");

                if (expense.Status != PettyCashStatus.Active)
                {
                    throw new InvalidOperationException($"El gasto de caja chica #{expenseId} ya fue anulado; no se puede anular dos veces.");
                }

                if (expense.Session.Status != CashDrawerStatus.Open)
                {
                    throw new InvalidOperationException($"No se puede anular el gasto de caja chica #{expenseId}: la sesión de caja #{expense.SessionId} ya está cerrada.");
                }

                if (PettyCashAuthorizationPolicy.RequiresCancellationAuthorization(actingRole) && authorizedByUserId is null)
                {
                    throw BuildAuthorizationRequired(expense.SessionId, expense.Currency, expense.AmountBsS, expense.AmountUsd, expense.Category);
                }

                // La reversa copia EXACTAMENTE los snapshots del gasto (S5): jamás se relee la
                // tasa BCV vigente al momento de anular. El método de pago acompaña al movimiento
                // original para regresar el efectivo a la misma gaveta.
                int? paymentMethodId = await ResolveReversalPaymentMethodIdAsync(expense, cancellationToken);

                expense.Status = PettyCashStatus.Cancelled;
                expense.CancelledAtUtc = DateTime.UtcNow;
                expense.CancelledByUserId = actingUserId.Value;
                expense.CancellationReason = cancellationReason;

                var reversal = new CashTransaction
                {
                    SessionId = expense.SessionId,
                    TransactionTime = DateTime.UtcNow,
                    Type = CashTransactionType.Income,
                    Source = CashTransactionSource.PettyCashReversal,
                    AmountUsd = expense.AmountUsd,
                    ExchangeRate = expense.HistoricalRate,
                    AmountLocal = expense.AmountBsS,
                    Description = $"Reversa de gasto de caja chica {FormatVoucher(expense.VoucherNumber)}: {expense.Concept}",
                    IsPhysicalCash = true,
                    PaymentMethodId = paymentMethodId,
                    PettyCashExpenseId = expense.Id
                };

                _context.CashTransactions.Add(reversal);
                await _context.SaveChangesAsync(cancellationToken);

                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                _logger?.LogInformation(
                    "[PettyCashService] Gasto de caja chica #{ExpenseId} ({Voucher}) anulado por usuario #{CancelledByUserId}; reversa compensatoria #{ReversalId} con snapshots congelados.",
                    expense.Id, FormatVoucher(expense.VoucherNumber), actingUserId.Value, reversal.Id);

                return MapExpense(expense, reversal.Id, isReplay: false);
            }
            catch
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }

                throw;
            }
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PettyCashExpenseResultDto>> GetSessionExpensesAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        var expenses = await _context.PettyCashExpenses
            .AsNoTracking()
            .AsSplitQuery()
            .Where(expense => expense.SessionId == sessionId)
            .Include(expense => expense.CashTransactions)
            .OrderBy(expense => expense.CreatedAtUtc)
            .ThenBy(expense => expense.Id)
            .ToListAsync(cancellationToken);

        return expenses
            .Select(expense => MapExpense(
                expense,
                expense.CashTransactions
                    .Where(movement => movement.Source is CashTransactionSource.PettyCashExpense or CashTransactionSource.PettyCashReversal)
                    .OrderByDescending(movement => movement.Id)
                    .Select(movement => (int?)movement.Id)
                    .FirstOrDefault(),
                isReplay: false))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<PettyCashVoucherData?> GetVoucherDataAsync(int expenseId, CancellationToken cancellationToken = default)
    {
        var expense = await _context.PettyCashExpenses
            .AsNoTracking()
            .AsSplitQuery()
            .Include(candidate => candidate.Session)
            .FirstOrDefaultAsync(candidate => candidate.Id == expenseId, cancellationToken);

        if (expense is null)
        {
            return null;
        }

        // Los nombres no se snapshotean en el gasto (solo las FKs de auditoria): el vale los
        // resuelve al imprimir, con "N/A" cuando no hubo autorizador y "N/D" si la fila falta.
        var userIds = new[] { expense.CreatedByUserId, expense.AuthorizedByUserId, expense.CancelledByUserId }
            .Where(userId => userId.HasValue)
            .Select(userId => userId!.Value)
            .Distinct()
            .ToList();

        var userNames = await _context.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .Select(user => new { user.Id, user.Name })
            .ToDictionaryAsync(user => user.Id, user => user.Name, cancellationToken);

        var context = PettyCashVoucherContext.CreateFrom(
            expense,
            userNames.GetValueOrDefault(expense.CreatedByUserId),
            expense.AuthorizedByUserId.HasValue ? userNames.GetValueOrDefault(expense.AuthorizedByUserId.Value) : null,
            expense.CancelledByUserId.HasValue ? userNames.GetValueOrDefault(expense.CancelledByUserId.Value) : null);

        return new PettyCashVoucherData(context, expense.CreatedByUserId);
    }

    /// <summary>
    /// Valoración bimonetaria (S1): el nominal es la divisa física; el contravalor usa la
    /// convención fiscal del POS (Bs.S 2dp, USD/tasa 4dp, AwayFromZero).
    /// </summary>
    private static (decimal AmountBsS, decimal AmountUsd) ComputeDualValuation(PettyCashCurrency currency, decimal nominalAmount, decimal exchangeRate)
    {
        if (currency == PettyCashCurrency.Usd)
        {
            return (
                Math.Round(nominalAmount * exchangeRate, 2, MidpointRounding.AwayFromZero),
                Math.Round(nominalAmount, 4, MidpointRounding.AwayFromZero));
        }

        return (
            Math.Round(nominalAmount, 2, MidpointRounding.AwayFromZero),
            Math.Round(nominalAmount / exchangeRate, 4, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Tasa BCV server-side del día desde la fuente cacheada (nunca un valor del cliente), con el
    /// techo de 2dp de la convención de caja. Sin tasa registrada el registro falla cerrado.
    /// </summary>
    private async Task<decimal> ResolveTodayRateAsync(CancellationToken cancellationToken)
    {
        decimal officialRate;
        try
        {
            officialRate = await _inventoryService.GetTodayExchangeRateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[PettyCashService] Error obteniendo la tasa BCV del día para el gasto de caja chica.");
            throw new InvalidOperationException("No se pudo obtener la tasa BCV del día para registrar el gasto de caja chica. Verifique la tasa del día e intente nuevamente.");
        }

        if (officialRate <= 0m)
        {
            throw new InvalidOperationException("No se puede registrar el gasto de caja chica: no existe una tasa BCV registrada para hoy. Registre la tasa del día antes de asentar el egreso.");
        }

        return PricingCalculator.RoundExchangeRateCeiling(officialRate);
    }

    /// <summary>
    /// Umbral de autorización desde SystemSettings; un valor ausente/ilegible/negativo cae al
    /// fallback seguro de 50.00 USD (nunca desactiva la jerarquía en silencio).
    /// </summary>
    private async Task<decimal> ResolveThresholdAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_settingsService is null)
        {
            return PettyCashAuthorizationPolicy.DefaultThresholdUsd;
        }

        try
        {
            string? rawValue = await _settingsService.GetSettingAsync(PettyCashAuthorizationPolicy.ThresholdSettingKey);
            return PettyCashAuthorizationPolicy.ResolveThresholdUsd(rawValue);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[PettyCashService] No se pudo leer {SettingKey}; se usa el umbral por defecto de 50.00 USD.", PettyCashAuthorizationPolicy.ThresholdSettingKey);
            return PettyCashAuthorizationPolicy.DefaultThresholdUsd;
        }
    }

    /// <summary>
    /// Valida la autorización presentada por el controlador (token ya consumido). La solicitud
    /// debe pertenecer al ejecutor, ser de la acción de caja chica, estar aprobada y consumida;
    /// cualquier desvío falla cerrado con la señal de autorización requerida.
    /// </summary>
    private async Task<int> ResolveAuthorizerAsync(
        int? authorizationRequestId,
        int? authorizedByUserId,
        int actingUserId,
        int sessionId,
        PettyCashExpenseRequest request,
        decimal amountBsS,
        decimal amountUsd,
        CancellationToken cancellationToken)
    {
        PettyCashAuthorizationRequiredException BuildSignal() =>
            BuildAuthorizationRequired(sessionId, request.Currency, amountBsS, amountUsd, request.Category);

        if (authorizationRequestId is null || authorizedByUserId is null)
        {
            throw BuildSignal();
        }

        var grant = await _context.AuthorizationRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == authorizationRequestId.Value, cancellationToken);

        if (grant is null
            || grant.ActionType != AuthorizationActionType.PettyCashExpense
            || grant.RequestedByUserId != actingUserId
            || grant.Status != AuthorizationStatus.Approved
            || grant.ConsumedAt is null
            || grant.ResolvedByUserId is null)
        {
            throw BuildSignal();
        }

        return grant.ResolvedByUserId.Value;
    }

    /// <summary>
    /// Replay idempotente (S3): una clave ya consumida devuelve el gasto existente tal como quedó
    /// persistido, sin nuevo movimiento ni segunda validación de liquidez/autorización.
    /// </summary>
    private async Task<PettyCashExpenseResultDto?> FindExistingExpenseAsync(string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (idempotencyKey is null)
        {
            return null;
        }

        var existing = await _context.PettyCashExpenses
            .AsNoTracking()
            .AsSplitQuery()
            .Include(expense => expense.CashTransactions)
            .FirstOrDefaultAsync(expense => expense.IdempotencyKey == idempotencyKey, cancellationToken);

        if (existing is null)
        {
            return null;
        }

        int? movementId = existing.CashTransactions
            .Where(movement => movement.Source == CashTransactionSource.PettyCashExpense)
            .OrderBy(movement => movement.Id)
            .Select(movement => (int?)movement.Id)
            .FirstOrDefault();

        return MapExpense(existing, movementId, isReplay: true);
    }

    /// <summary>
    /// Método de pago de efectivo de la gaveta de la moneda (S2): la atribución es explícita por
    /// moneda canónica del método; sin método configurado el egreso no puede asentarse.
    /// </summary>
    private async Task<int> ResolveCashPaymentMethodIdAsync(PettyCashCurrency currency, CancellationToken cancellationToken)
    {
        var cashMethods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(method => method.IsCash && method.IsActive && !method.IsDeleted)
            .OrderBy(method => method.Id)
            .Select(method => new { method.Id, method.Name })
            .ToListAsync(cancellationToken);

        string expectedCurrency = currency == PettyCashCurrency.Usd
            ? PaymentMethodCurrencyResolver.Usd
            : PaymentMethodCurrencyResolver.LocalCurrency;

        var match = cashMethods.FirstOrDefault(method => string.Equals(
            PaymentMethodCurrencyResolver.Resolve(method.Name), expectedCurrency, StringComparison.Ordinal));

        if (match is null)
        {
            throw new InvalidOperationException(currency == PettyCashCurrency.Usd
                ? "No existe un método de pago de efectivo en USD configurado; registre el método antes de egresar dólares de la gaveta."
                : "No existe un método de pago de efectivo en Bs.S configurado; registre el método antes de egresar efectivo de la gaveta.");
        }

        return match.Id;
    }

    /// <summary>
    /// Correlativo del vale: `nextval` de la secuencia dedicada en Npgsql (posterior a la
    /// migración 8.188-T1) y `max + 1` en los proveedores de test, donde la secuencia no existe.
    /// </summary>
    private async Task<long> ResolveNextVoucherNumberAsync(CancellationToken cancellationToken)
    {
        if (_context.Database.IsNpgsql())
        {
            return await _context.Database
                .SqlQueryRaw<long>("SELECT nextval('petty_cash_voucher_seq') AS \"Value\"")
                .SingleAsync(cancellationToken);
        }

        long? lastVoucher = await _context.PettyCashExpenses
            .Select(expense => (long?)expense.VoucherNumber)
            .MaxAsync(cancellationToken);

        return (lastVoucher ?? 0L) + 1L;
    }

    /// <summary>
    /// Método de pago de la reversa: acompaña al movimiento Expense original para devolver el
    /// efectivo a la misma gaveta; sin movimiento original (dato anómalo) se re-resuelve por moneda.
    /// </summary>
    private async Task<int?> ResolveReversalPaymentMethodIdAsync(PettyCashExpense expense, CancellationToken cancellationToken)
    {
        int? originalMethodId = await _context.CashTransactions
            .AsNoTracking()
            .Where(movement => movement.PettyCashExpenseId == expense.Id && movement.Source == CashTransactionSource.PettyCashExpense)
            .OrderBy(movement => movement.Id)
            .Select(movement => movement.PaymentMethodId)
            .FirstOrDefaultAsync(cancellationToken);

        if (originalMethodId.HasValue)
        {
            return originalMethodId;
        }

        return await ResolveCashPaymentMethodIdAsync(expense.Currency, cancellationToken);
    }

    private static PettyCashAuthorizationRequiredException BuildAuthorizationRequired(
        int sessionId,
        PettyCashCurrency currency,
        decimal amountBsS,
        decimal amountUsd,
        PettyCashCategory category)
        => new(
            sessionId,
            (int)currency,
            amountBsS,
            amountUsd,
            (int)category,
            "El gasto de caja chica requiere autorización de un supervisor antes de asentarse.");

    private static string NormalizeRequired(string? value, int maxLength, string message)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maxLength)
        {
            throw new ArgumentException(message);
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maxLength, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException(message);
        }

        return normalized;
    }

    private static string FormatVoucher(long voucherNumber) => $"VCC-{voucherNumber:D6}";

    private static PettyCashExpenseResultDto MapExpense(PettyCashExpense expense, int? cashTransactionId, bool isReplay) => new()
    {
        Id = expense.Id,
        VoucherNumber = expense.VoucherNumber,
        SessionId = expense.SessionId,
        Category = expense.Category,
        Currency = expense.Currency,
        AmountBsS = expense.AmountBsS,
        AmountUsd = expense.AmountUsd,
        HistoricalRate = expense.HistoricalRate,
        Concept = expense.Concept,
        Beneficiary = expense.Beneficiary,
        ReferenceNumber = expense.ReferenceNumber,
        CreatedByUserId = expense.CreatedByUserId,
        CreatedAtUtc = expense.CreatedAtUtc,
        AuthorizedByUserId = expense.AuthorizedByUserId,
        AuthorizationRequestId = expense.AuthorizationRequestId,
        Status = expense.Status,
        CancelledAtUtc = expense.CancelledAtUtc,
        CancelledByUserId = expense.CancelledByUserId,
        CancellationReason = expense.CancellationReason,
        CashTransactionId = cashTransactionId,
        IsReplay = isReplay
    };
}
