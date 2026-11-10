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
    // 8.211-T3c4 (S7): optional profile services appended so historical direct compositions keep
    // working. Null preserves the legacy Dual USD/VES behavior (Bs.S drawer, BCV rate).
    private readonly IMonetaryProfileService? _monetaryProfileService;
    private readonly ICurrencyRateService? _currencyRateService;

    public PettyCashService(
        SalesDbContext context,
        ICashDrawerService cashDrawerService,
        IInventoryService inventoryService,
        ISystemSettingsService? settingsService = null,
        ILogger<PettyCashService>? logger = null,
        IMonetaryProfileService? monetaryProfileService = null,
        ICurrencyRateService? currencyRateService = null)
    {
        _context = context;
        _cashDrawerService = cashDrawerService;
        _inventoryService = inventoryService;
        _settingsService = settingsService;
        _logger = logger;
        _monetaryProfileService = monetaryProfileService;
        _currencyRateService = currencyRateService;
    }

    /// <summary>
    /// Moneda local de la gaveta y banderas del perfil activo. <c>PettyCashCurrency.BsS</c> es el
    /// efectivo local del régimen (COP en Dual USD/COP, Bs.S en Dual USD/VES o Single VES) y
    /// <c>Usd</c> solo es válido cuando USD pertenece al perfil.
    /// </summary>
    private sealed record PettyCashDrawerContext(
        MonetaryProfileDto? Profile,
        string LocalCurrency,
        bool IncludesUsd,
        string ProfileLabel);

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
                // El replay se resuelve antes de crear sesión, leer la tasa o validar liquidez: un
                // gasto ya persistido se replica tal cual, sin depender de la tasa BCV vigente
                // (el snapshot original es la única autoridad).
                var replay = await FindExistingExpenseAsync(idempotencyKey, cancellationToken);
                if (replay is not null)
                {
                    return replay;
                }

                var drawerContext = await ResolveProfileContextAsync("registrar el gasto de caja chica", cancellationToken);
                if (request.Currency == PettyCashCurrency.Usd && !drawerContext.IncludesUsd)
                {
                    throw new InvalidOperationException(
                        $"USD no forma parte del perfil monetario actual ({drawerContext.ProfileLabel}); registre el gasto de caja chica en efectivo en {drawerContext.LocalCurrency}.");
                }

                var resolvedRate = await ResolveTodayRateAsync(drawerContext, cancellationToken);
                // 8.211-T3c4 (S7/F3c3d): la valuacion usa la tasa de calculo (8dp en par manual) y
                // los snapshots persistidos usan el tope de las columnas 18,4 (4dp).
                (decimal amountBsS, decimal amountUsd) = ComputeDualValuation(request.Currency, request.Amount, resolvedRate.CalculationRate);
                decimal thresholdUsd = await ResolveThresholdAsync(cancellationToken);
                bool requiresAuthorization = PettyCashAuthorizationPolicy.RequiresAuthorization(amountUsd, actingRole, thresholdUsd);

                var session = await _cashDrawerService.GetOrCreateActiveSessionAsync(resolvedRate.SnapshotRate, cancellationToken);

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

                int cashMethodId = await ResolveCashPaymentMethodIdAsync(request.Currency, drawerContext, cancellationToken);
                string drawerCurrencyCode = ResolveDrawerCurrencyCode(request.Currency, drawerContext);

                decimal physicalBalance = await _cashDrawerService.GetPhysicalBalanceAsync(session.Id, request.Currency, cancellationToken);
                decimal requiredAmount = request.Currency == PettyCashCurrency.Usd ? amountUsd : amountBsS;
                if (physicalBalance < requiredAmount)
                {
                    // 8.153 (SEC-06): sin exponer el efectivo disponible (arqueo ciego); el monto
                    // requerido es dato del propio operador que asienta el gasto.
                    throw new InvalidOperationException(
                        $"Saldo de efectivo físico en {drawerCurrencyCode} insuficiente en la gaveta para registrar el gasto de caja chica. Monto requerido: {MoneyFormat.N2(requiredAmount)} {drawerCurrencyCode}.");
                }

                int? authorizerUserId;
                if (requiresAuthorization)
                {
                    int resolvedAuthorizerUserId = await ResolveAuthorizerAsync(
                        authorizationRequestId, authorizedByUserId, actingUserId.Value,
                        session.Id, request, amountBsS, amountUsd, cancellationToken);

                    // Quick win 8.188: se vincula SOLO un supervisor distinto del ejecutor (misma
                    // regla H3 de la anulacion); una auto-autorizacion jamas se registra como aprobador.
                    authorizerUserId = resolvedAuthorizerUserId != actingUserId.Value
                        ? resolvedAuthorizerUserId
                        : null;
                }
                else
                {
                    // Admin/Manager dentro del umbral: no hay supervisor distinto que auditar; el
                    // gasto queda sin autorizador y el vale muestra "N/A" (antes se auto-vinculaba
                    // el ejecutor y el vale mostraba su propio nombre).
                    authorizerUserId = null;
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
                    HistoricalRate = resolvedRate.SnapshotRate,
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
                    ExchangeRate = resolvedRate.SnapshotRate,
                    AmountLocal = amountBsS,
                    Description = $"Gasto de caja chica {FormatVoucher(voucherNumber)}: {concept}",
                    IsPhysicalCash = true,
                    PaymentMethodId = cashMethodId,
                    PettyCashExpense = expense,
                    // 8.211-T3c4 (S6/S7): la gaveta fisica del gasto es la moneda local real del
                    // perfil (COP en Dual USD/COP, Bs.S en Dual USD/VES), nunca un hardcode Bs.S.
                    CurrencyCode = drawerCurrencyCode
                };

                _context.PettyCashExpenses.Add(expense);
                _context.CashTransactions.Add(movement);
                await _context.SaveChangesAsync(cancellationToken);

                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                string authorizerLog = authorizerUserId is int authorizerId
                    ? $"usuario #{authorizerId}"
                    : "sin supervisor distinto";
                _logger?.LogInformation(
                    "[PettyCashService] Gasto de caja chica #{ExpenseId} (vale {Voucher}): {Amount} {Currency} / {AmountUsd} USD a tasa {Rate}; sesión #{SessionId}, {Authorizer}.",
                    expense.Id, FormatVoucher(voucherNumber), request.Amount, drawerCurrencyCode, amountUsd, resolvedRate.SnapshotRate, session.Id, authorizerLog);

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
                // tasa vigente al momento de anular. El método y el código de moneda acompañan al
                // movimiento original para regresar el efectivo a la misma gaveta.
                (int? paymentMethodId, string reversalCurrencyCode) =
                    await ResolveReversalRouteAsync(expense, cancellationToken);

                expense.Status = PettyCashStatus.Cancelled;
                expense.CancelledAtUtc = DateTime.UtcNow;
                expense.CancelledByUserId = actingUserId.Value;

                // H3 (cierre 8.188): se vincula al supervisor aprobador SOLO si es distinto del
                // ejecutor; la anulacion directa de Admin (sin token) o una auto-autorizacion
                // quedan en null, porque no hay un supervisor distinto que auditar.
                expense.CancellationAuthorizedByUserId =
                    authorizedByUserId.HasValue && authorizedByUserId.Value != actingUserId.Value
                        ? authorizedByUserId
                        : null;

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
                    PettyCashExpenseId = expense.Id,
                    // 8.211-T3c4 (S6/S7): la reversa regresa a la gaveta original con su codigo
                    // real (COP/EUR/Bs.S), tomado del snapshot del egreso.
                    CurrencyCode = reversalCurrencyCode
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
            .Include(candidate => candidate.CashTransactions)
            .FirstOrDefaultAsync(candidate => candidate.Id == expenseId, cancellationToken);

        if (expense is null)
        {
            return null;
        }

        // Los nombres no se snapshotean en el gasto (solo las FKs de auditoria): el vale los
        // resuelve al imprimir, con "N/A" cuando no hubo autorizador, "N/D" si la fila falta y
        // la linea "Autorizo anulacion" solo cuando hay supervisor aprobador distinto (H3).
        var userIds = new[] { expense.CreatedByUserId, expense.AuthorizedByUserId, expense.CancelledByUserId, expense.CancellationAuthorizedByUserId }
            .Where(userId => userId.HasValue)
            .Select(userId => userId!.Value)
            .Distinct()
            .ToList();

        var userNames = await _context.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .Select(user => new { user.Id, user.Name })
            .ToDictionaryAsync(user => user.Id, user => user.Name, cancellationToken);

        // 8.211-T4a (S10/F4a): la moneda real del vale es la del movimiento Expense persistido
        // (mejor que inferirla); el perfil aporta los símbolos y la moneda local de la gaveta.
        string? persistedCurrencyCode = expense.CashTransactions
            .Where(movement => movement.Source == CashTransactionSource.PettyCashExpense)
            .OrderByDescending(movement => movement.Id)
            .Select(movement => movement.CurrencyCode)
            .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));

        MonetaryProfileDto? profile = null;
        if (_monetaryProfileService is not null)
        {
            try
            {
                profile = await _monetaryProfileService.GetAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // El vale es un documento de consulta: una lectura fallida de settings no debe
                // impedir su descarga. Sin perfil rigen los defaults por código.
                _logger?.LogWarning(ex, "[PettyCashService] No se pudo leer el perfil monetario para el vale #{ExpenseId}; se usan las etiquetas por código.", expenseId);
            }
        }

        var context = PettyCashVoucherContext.CreateFrom(
            expense,
            userNames.GetValueOrDefault(expense.CreatedByUserId),
            expense.AuthorizedByUserId.HasValue ? userNames.GetValueOrDefault(expense.AuthorizedByUserId.Value) : null,
            expense.CancelledByUserId.HasValue ? userNames.GetValueOrDefault(expense.CancelledByUserId.Value) : null,
            expense.CancellationAuthorizedByUserId.HasValue ? userNames.GetValueOrDefault(expense.CancellationAuthorizedByUserId.Value) : null,
            profile,
            persistedCurrencyCode);

        return new PettyCashVoucherData(context, expense.CreatedByUserId);
    }

    /// <summary>
    /// Valoración bimonetaria (S1/S7): el nominal es la divisa física; el contravalor usa la
    /// convención fiscal del POS (moneda local 2dp, USD/tasa 4dp, AwayFromZero) y la tasa de
    /// cálculo del perfil (8dp en par manual).
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
    /// Perfil activo resuelto UNA vez por operación. La lectura falla cerrado: nunca se degrada en
    /// silencio a Bs.S un perfil no-VES al escribir caja chica.
    /// </summary>
    private async Task<PettyCashDrawerContext> ResolveProfileContextAsync(
        string contextLabel,
        CancellationToken cancellationToken)
    {
        if (_monetaryProfileService is null)
        {
            return new PettyCashDrawerContext(
                Profile: null,
                LocalCurrency: PaymentMethodCurrencyResolver.LocalCurrency,
                IncludesUsd: true,
                ProfileLabel: "Dual USD/VES");
        }

        MonetaryProfileDto profile;
        try
        {
            profile = await _monetaryProfileService.GetAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"No se pudo leer el perfil monetario para {contextLabel}. Verifique la configuración monetaria.",
                ex);
        }

        return new PettyCashDrawerContext(
            profile,
            MonetaryRateResolver.ResolveLocalCurrency(profile),
            ProfileIncludesUsd(profile),
            DescribeProfile(profile));
    }

    private static bool ProfileIncludesUsd(MonetaryProfileDto profile)
    {
        if (MonetaryRateResolver.IsSameCurrency(profile.PrimaryCurrencyCode, PaymentMethodCurrencyResolver.Usd))
        {
            return true;
        }

        return profile.Mode == MonetaryMode.Dual
            && MonetaryRateResolver.IsSameCurrency(profile.SecondaryCurrencyCode, PaymentMethodCurrencyResolver.Usd);
    }

    private static string DescribeProfile(MonetaryProfileDto profile)
        => profile.Mode == MonetaryMode.Single
            ? $"Single {profile.PrimaryCurrencyCode}"
            : $"Dual {profile.PrimaryCurrencyCode}/{profile.SecondaryCurrencyCode}";

    private static string ResolveDrawerCurrencyCode(PettyCashCurrency currency, PettyCashDrawerContext context)
        => currency == PettyCashCurrency.Usd
            ? PaymentMethodCurrencyResolver.Usd
            : context.LocalCurrency;

    /// <summary>
    /// Tasa del gasto resuelta por el perfil activo (Single 1; Dual USD/VES BCV; Dual USD/{COP,
    /// MXN,EUR} manual 8dp) y separada en cálculo 8dp / snapshot 4dp. Sin tasa válida el registro
    /// falla cerrado (nunca un nominal del cliente).
    /// </summary>
    private async Task<MonetaryRateResolver.ResolvedMonetaryRate> ResolveTodayRateAsync(
        PettyCashDrawerContext context,
        CancellationToken cancellationToken)
    {
        var resolved = await MonetaryRateResolver.ResolveTodayRateForProfileAsync(
            context.Profile,
            _currencyRateService,
            async ct =>
            {
                try
                {
                    return await _inventoryService.GetTodayExchangeRateAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[PettyCashService] Error obteniendo la tasa BCV del día para el gasto de caja chica.");
                    throw new InvalidOperationException(
                        "No se pudo obtener la tasa BCV del día para registrar el gasto de caja chica. Verifique la tasa del día e intente nuevamente.",
                        ex);
                }
            },
            "el gasto de caja chica",
            cancellationToken);

        if (resolved.CalculationRate <= 0m)
        {
            string message = string.Equals(resolved.Source, MonetaryRateResolver.BcvSource, StringComparison.Ordinal)
                ? "No se puede registrar el gasto de caja chica: no existe una tasa BCV registrada para hoy. Registre la tasa del día antes de asentar el egreso."
                : "No se puede registrar el gasto de caja chica: no existe una tasa de cambio válida para el perfil monetario actual. Verifique la configuración antes de asentar el egreso.";
            throw new InvalidOperationException(message);
        }

        return resolved;
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
    /// Método de pago de efectivo de la gaveta real (S2/S7): <c>BsS</c> resuelve la moneda local
    /// del perfil (COP en Dual USD/COP, Bs.S en Dual USD/VES) y <c>Usd</c> solo se admite cuando
    /// USD pertenece al perfil. La comparación usa ISO canónico para tolerar VES/Bs.S persistidos.
    /// </summary>
    private async Task<int> ResolveCashPaymentMethodIdAsync(
        PettyCashCurrency currency,
        PettyCashDrawerContext context,
        CancellationToken cancellationToken)
    {
        bool isUsd = currency == PettyCashCurrency.Usd;
        if (isUsd && !context.IncludesUsd)
        {
            throw new InvalidOperationException(
                $"USD no forma parte del perfil monetario actual ({context.ProfileLabel}); el efectivo en dólares no está disponible en esta gaveta.");
        }

        var cashMethods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(method => method.IsCash && method.IsActive && !method.IsDeleted)
            .OrderBy(method => method.Id)
            .Select(method => new { method.Id, method.Name, method.Currency })
            .ToListAsync(cancellationToken);

        string expectedCurrency = isUsd
            ? PaymentMethodCurrencyResolver.Usd
            : context.LocalCurrency;

        var match = cashMethods.FirstOrDefault(method => MonetaryRateResolver.IsSameCurrency(
            PaymentMethodCurrencyResolver.ResolveStoredOrName(method.Currency, method.Name),
            expectedCurrency));

        if (match is null)
        {
            throw new InvalidOperationException(isUsd
                ? "No existe un método de pago de efectivo en USD configurado; registre el método antes de egresar dólares de la gaveta."
                : $"No existe un método de pago de efectivo en {expectedCurrency} configurado; registre el método antes de egresar efectivo de la gaveta.");
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
    /// Ruta de la reversa: método y código de moneda del movimiento Expense original para devolver
    /// el efectivo a la MISMA gaveta con su snapshot real. Sin movimiento original (dato anómalo) se
    /// re-resuelve por moneda local del perfil; una fila legacy sin CurrencyCode se completa con la
    /// moneda actual del gasto.
    /// </summary>
    private async Task<(int? MethodId, string CurrencyCode)> ResolveReversalRouteAsync(
        PettyCashExpense expense,
        CancellationToken cancellationToken)
    {
        var original = await _context.CashTransactions
            .AsNoTracking()
            .Where(movement => movement.PettyCashExpenseId == expense.Id && movement.Source == CashTransactionSource.PettyCashExpense)
            .OrderBy(movement => movement.Id)
            .Select(movement => new { movement.PaymentMethodId, movement.CurrencyCode })
            .FirstOrDefaultAsync(cancellationToken);

        if (original is not null && !string.IsNullOrWhiteSpace(original.CurrencyCode))
        {
            return (original.PaymentMethodId, original.CurrencyCode);
        }

        var context = await ResolveProfileContextAsync("anular el gasto de caja chica", cancellationToken);
        string currencyCode = ResolveDrawerCurrencyCode(expense.Currency, context);

        if (original is not null)
        {
            return (original.PaymentMethodId, currencyCode);
        }

        int methodId = await ResolveCashPaymentMethodIdAsync(expense.Currency, context, cancellationToken);
        return (methodId, currencyCode);
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
