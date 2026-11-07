using Core.Common;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// 8.204 (extracción del God object de caja): cálculo de saldos físicos por moneda. La consulta
/// agrega por (método, tipo, moneda snapshot) en SQL y solo materializa una fila por combinación.
/// 8.211-T3c3b2: la moneda local es la primaria del perfil en modo Single y Bs.S en Dual; el
/// snapshot CurrencyCode de cada movimiento manda sobre la moneda actual del método.
/// 8.212-WU2: proyección por método físico para la sesión, con moneda congelada al abrir y
/// símbolo resuelto desde el perfil activo.
/// </summary>
internal sealed class CashDrawerBalanceService
{
    private readonly SalesDbContext _context;
    private readonly IMonetaryProfileService? _monetaryProfileService;

    public CashDrawerBalanceService(
        SalesDbContext context,
        IMonetaryProfileService? monetaryProfileService = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _monetaryProfileService = monetaryProfileService;
    }

    public async Task<decimal> GetCurrentBalanceLocalAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _context.CashDrawerSessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new { s.OpeningBalanceLocal })
            .FirstOrDefaultAsync(cancellationToken);

        if (session == null) return 0;

        // 8.202-H1 (S3): agregación server-side por (método, tipo, moneda). Solo se materializa
        // una fila por combinación, nunca el libro completo de la sesión, para acortar la
        // retención del advisory lock durante los chequeos de liquidez.
        var groups = await LoadPhysicalMovementGroupsAsync(sessionId, cancellationToken);

        if (groups.Count == 0)
        {
            return session.OpeningBalanceLocal;
        }

        var localCurrency = await ResolveLocalDrawerCurrencyAsync(cancellationToken);
        var currencyByMethodId = await LoadMethodCurrencyMapAsync(cancellationToken);

        decimal net = 0m;
        foreach (var group in groups)
        {
            string movementCurrency = ResolveMovementCurrency(
                group.CurrencyCode,
                group.PaymentMethodId,
                currencyByMethodId);

            if (!IsSameCurrency(movementCurrency, localCurrency))
            {
                continue;
            }

            net += group.Type == CashTransactionType.Expense ? -group.AmountLocal : group.AmountLocal;
        }

        return session.OpeningBalanceLocal + net;
    }

    public async Task<decimal> GetPhysicalBalanceAsync(
        int sessionId,
        PettyCashCurrency currency,
        CancellationToken cancellationToken = default)
    {
        var session = await _context.CashDrawerSessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new { s.OpeningBalanceLocal, s.OpeningBalanceUsd })
            .FirstOrDefaultAsync(cancellationToken);

        if (session == null) return 0m;

        // 8.202-H1 (S3): una sola proyección agrupada por (método, tipo, moneda) devuelve ambos
        // buckets; el saldo de la moneda pedida se resuelve sobre ese conjunto acotado.
        var groups = await LoadPhysicalMovementGroupsAsync(sessionId, cancellationToken);

        bool isUsd = currency == PettyCashCurrency.Usd;
        var localCurrency = await ResolveLocalDrawerCurrencyAsync(cancellationToken);
        string expectedCurrency = isUsd ? PaymentMethodCurrencyResolver.Usd : localCurrency;

        if (groups.Count == 0)
        {
            return isUsd ? session.OpeningBalanceUsd : session.OpeningBalanceLocal;
        }

        var currencyByMethodId = await LoadMethodCurrencyMapAsync(cancellationToken);

        decimal net = 0m;
        foreach (var group in groups)
        {
            string movementCurrency = ResolveMovementCurrency(
                group.CurrencyCode,
                group.PaymentMethodId,
                currencyByMethodId);

            if (!IsSameCurrency(movementCurrency, expectedCurrency))
            {
                continue;
            }

            decimal amount = isUsd ? group.AmountUsd : group.AmountLocal;
            net += group.Type == CashTransactionType.Expense ? -amount : amount;
        }

        return (isUsd ? session.OpeningBalanceUsd : session.OpeningBalanceLocal) + net;
    }

    /// <summary>
    /// 8.212-WU2 (S6): saldo físico por método capturado al abrir la sesión. Cada movimiento se
    /// atribuye al <c>PaymentMethodId</c> exacto y se valora con su snapshot de moneda: USD usa
    /// <c>AmountUsd</c> y cualquier moneda local usa <c>AmountLocal</c>. La moneda actual del
    /// método solo decide régimen/estado, nunca la atribución histórica.
    /// </summary>
    public async Task<IReadOnlyList<PhysicalMethodBalanceDto>> GetPhysicalMethodBalancesAsync(
        int sessionId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.CashDrawerSessionMethodBalances
            .AsNoTracking()
            .Where(row => row.CashDrawerSessionId == sessionId)
            .OrderBy(row => row.PaymentMethodId)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return Array.Empty<PhysicalMethodBalanceDto>();
        }

        // 8.212-WU2: un fallo de lectura del perfil no puede proyectar la gaveta contra un
        // régimen o símbolo adivinado; se falla cerrado igual que el resto de la caja.
        var profile = await ResolveProfileOrNullAsync(
            "No se pudo leer el perfil monetario para calcular los saldos físicos por método. Verifique la configuración monetaria.",
            cancellationToken);
        var localCurrency = profile is null
            ? PaymentMethodCurrencyResolver.LocalCurrency
            : ResolveLocalCurrency(profile);
        var allowedCurrencies = ResolveAllowedCurrencies(profile, localCurrency);

        var groups = await LoadPhysicalMovementGroupsAsync(sessionId, cancellationToken);
        var methods = await LoadMethodMapAsync(cancellationToken);
        var currencyByMethodId = BuildCurrencyMap(methods);

        var result = new List<PhysicalMethodBalanceDto>(rows.Count);
        foreach (var row in rows)
        {
            methods.TryGetValue(row.PaymentMethodId, out var method);
            decimal balance = ComputeMethodBalance(row, groups, currencyByMethodId);
            bool isActive = method is not null && method.IsActive && !method.IsDeleted;
            bool canDispense = isActive
                && balance > 0m
                && IsCurrencyAllowedForProfile(method, row, allowedCurrencies);

            result.Add(new PhysicalMethodBalanceDto
            {
                PaymentMethodId = row.PaymentMethodId,
                Name = method?.Name ?? row.PaymentMethodName,
                CurrencyCode = row.CurrencyCode,
                CurrencySymbol = ResolveCurrencySymbol(profile, row.CurrencyCode),
                CurrentBalance = balance,
                IsActive = isActive,
                CanDispense = canDispense
            });
        }

        return result;
    }

    /// <summary>
    /// 8.212-WU2: filas vivas (tracked) con su saldo para cerrar/arrastrar la sesión. Mantiene
    /// una única fórmula compartida con la lectura por método.
    /// </summary>
    public async Task<IReadOnlyList<(CashDrawerSessionMethodBalance Row, decimal Balance)>> GetMethodBalancesAsync(
        int sessionId,
        CancellationToken cancellationToken = default)
    {
        // Tracking intentional: CloseSessionAsync mutates ClosingAmount on these rows and saves
        // through the same context instance.
        var rows = await _context.CashDrawerSessionMethodBalances
            .Where(row => row.CashDrawerSessionId == sessionId)
            .OrderBy(row => row.PaymentMethodId)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return Array.Empty<(CashDrawerSessionMethodBalance Row, decimal Balance)>();
        }

        var groups = await LoadPhysicalMovementGroupsAsync(sessionId, cancellationToken);
        var currencyByMethodId = await LoadMethodCurrencyMapAsync(cancellationToken);

        var result = new List<(CashDrawerSessionMethodBalance Row, decimal Balance)>(rows.Count);
        foreach (var row in rows)
        {
            result.Add((row, ComputeMethodBalance(row, groups, currencyByMethodId)));
        }

        return result;
    }

    private static decimal ComputeMethodBalance(
        CashDrawerSessionMethodBalance row,
        IReadOnlyList<PhysicalMovementGroup> groups,
        IReadOnlyDictionary<int, string> currencyByMethodId)
    {
        decimal net = 0m;
        foreach (var group in groups)
        {
            if (group.PaymentMethodId != row.PaymentMethodId)
            {
                continue;
            }

            string movementCurrency = ResolveMovementCurrency(
                group.CurrencyCode,
                group.PaymentMethodId,
                currencyByMethodId);
            decimal amount = IsUsdCurrency(movementCurrency) ? group.AmountUsd : group.AmountLocal;
            net += group.Type == CashTransactionType.Expense ? -amount : amount;
        }

        return row.OpeningAmount + net;
    }

    private async Task<List<PhysicalMovementGroup>> LoadPhysicalMovementGroupsAsync(
        int sessionId,
        CancellationToken cancellationToken)
    {
        return await _context.CashTransactions
            .AsNoTracking()
            .Where(transaction => transaction.SessionId == sessionId
                && transaction.IsPhysicalCash
                && transaction.Source != CashTransactionSource.Opening)
            .GroupBy(transaction => new { transaction.PaymentMethodId, transaction.Type, transaction.CurrencyCode })
            .Select(group => new PhysicalMovementGroup
            {
                PaymentMethodId = group.Key.PaymentMethodId,
                Type = group.Key.Type,
                CurrencyCode = group.Key.CurrencyCode,
                AmountLocal = group.Sum(transaction => transaction.AmountLocal),
                AmountUsd = group.Sum(transaction => transaction.AmountUsd)
            })
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Moneda efectiva de un movimiento: el snapshot persistido manda; para filas legacy sin
    /// snapshot se conserva la atribución histórica por método y, sin método, la moneda local.
    /// </summary>
    private static string ResolveMovementCurrency(
        string? snapshotCurrency,
        int? paymentMethodId,
        IReadOnlyDictionary<int, string> currencyByMethodId)
    {
        if (!string.IsNullOrWhiteSpace(snapshotCurrency))
        {
            return snapshotCurrency;
        }

        if (paymentMethodId.HasValue
            && currencyByMethodId.TryGetValue(paymentMethodId.Value, out var methodCurrency))
        {
            return methodCurrency;
        }

        return PaymentMethodCurrencyResolver.LocalCurrency;
    }

    private static bool IsSameCurrency(string? left, string? right)
    {
        var leftIso = CurrencyCodeNormalizer.NormalizeToIso(left);
        var rightIso = CurrencyCodeNormalizer.NormalizeToIso(right);

        if (leftIso is not null && rightIso is not null)
        {
            return string.Equals(leftIso, rightIso, StringComparison.Ordinal);
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsdCurrency(string? currency)
        => string.Equals(
            CurrencyCodeNormalizer.NormalizeToIso(currency),
            PaymentMethodCurrencyResolver.Usd,
            StringComparison.Ordinal);

    /// <summary>
    /// 8.211-T3c3c1 (S4): local drawer currency from the active profile. Single uses the primary
    /// currency; Dual uses the normalized secondary (VES displays as Bs.S, other ISO as-is).
    /// </summary>
    internal static string ResolveLocalCurrency(MonetaryProfileDto profile)
    {
        if (profile.Mode == MonetaryMode.Single)
        {
            return profile.PrimaryCurrencyCode;
        }

        var secondary = CurrencyCodeNormalizer.NormalizeToIso(profile.SecondaryCurrencyCode);
        return secondary is null || string.Equals(secondary, "VES", StringComparison.Ordinal)
            ? PaymentMethodCurrencyResolver.LocalCurrency
            : secondary;
    }

    /// <summary>
    /// 8.211-T3c3b1/T3c3c1: monedas físicas válidas para el régimen activo. Un perfil nulo
    /// conserva el conjunto histórico USD + moneda local. Única fuente de verdad para apertura y
    /// proyección de saldos.
    /// </summary>
    internal static HashSet<string> ResolveAllowedCurrencies(
        MonetaryProfileDto? profile,
        string localDrawerCurrency)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? code)
        {
            var normalized = CurrencyCodeNormalizer.NormalizeToIso(code);
            if (normalized is not null)
            {
                allowed.Add(normalized);
            }
        }

        if (profile is null)
        {
            Add(localDrawerCurrency);
            Add(PaymentMethodCurrencyResolver.Usd);
        }
        else if (profile.Mode == MonetaryMode.Single)
        {
            Add(profile.PrimaryCurrencyCode);
        }
        else
        {
            Add(profile.PrimaryCurrencyCode);
            Add(profile.SecondaryCurrencyCode);
        }

        if (allowed.Count == 0)
        {
            Add(localDrawerCurrency);
            Add(PaymentMethodCurrencyResolver.Usd);
        }

        return allowed;
    }

    private static bool IsCurrencyAllowedForProfile(
        PaymentMethodSnapshot? method,
        CashDrawerSessionMethodBalance row,
        IReadOnlySet<string> allowedCurrencies)
    {
        string currentCurrency = method is null
            ? row.CurrencyCode
            : PaymentMethodCurrencyResolver.ResolveStoredOrName(method.Currency, method.Name);
        string? normalized = CurrencyCodeNormalizer.NormalizeToIso(currentCurrency);
        return normalized is not null && allowedCurrencies.Contains(normalized);
    }

    private static string ResolveCurrencySymbol(MonetaryProfileDto? profile, string currencyCode)
    {
        if (profile is null)
        {
            return currencyCode;
        }

        var normalized = CurrencyCodeNormalizer.NormalizeToIso(currencyCode);
        if (normalized is null)
        {
            return currencyCode;
        }

        if (MatchesCurrency(normalized, profile.PrimaryCurrencyCode))
        {
            return string.IsNullOrWhiteSpace(profile.PrimaryCurrencySymbol)
                ? currencyCode
                : profile.PrimaryCurrencySymbol;
        }

        if (profile.Mode == MonetaryMode.Dual
            && MatchesCurrency(normalized, profile.SecondaryCurrencyCode))
        {
            return string.IsNullOrWhiteSpace(profile.SecondaryCurrencySymbol)
                ? currencyCode
                : profile.SecondaryCurrencySymbol;
        }

        return currencyCode;
    }

    private static bool MatchesCurrency(string normalizedCode, string? candidate)
        => string.Equals(
            CurrencyCodeNormalizer.NormalizeToIso(candidate),
            normalizedCode,
            StringComparison.Ordinal);

    private async Task<string> ResolveLocalDrawerCurrencyAsync(CancellationToken cancellationToken)
    {
        var profile = await ResolveProfileOrNullAsync(
            "No se pudo leer el perfil monetario para calcular la caja. Verifique la configuración monetaria.",
            cancellationToken);
        return profile is null
            ? PaymentMethodCurrencyResolver.LocalCurrency
            : ResolveLocalCurrency(profile);
    }

    private async Task<MonetaryProfileDto?> ResolveProfileOrNullAsync(
        string failureMessage,
        CancellationToken cancellationToken)
    {
        if (_monetaryProfileService is null)
        {
            return null;
        }

        try
        {
            return await _monetaryProfileService.GetAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 8.211-T6-U1 / 8.212-WU2: balances must not be projected against a guessed profile.
            throw new InvalidOperationException(failureMessage, ex);
        }
    }

    /// <summary>
    /// Moneda canónica por método de pago. Usa la moneda persistida (8.201) y cae al nombre
    /// histórico solo para filas legadas sin Currency.
    /// </summary>
    private async Task<Dictionary<int, string>> LoadMethodCurrencyMapAsync(CancellationToken cancellationToken)
    {
        var methods = await LoadMethodMapAsync(cancellationToken);
        return BuildCurrencyMap(methods);
    }

    private async Task<Dictionary<int, PaymentMethodSnapshot>> LoadMethodMapAsync(CancellationToken cancellationToken)
    {
        var methods = await _context.PaymentMethods
            .AsNoTracking()
            .Select(method => new PaymentMethodSnapshot
            {
                Id = method.Id,
                Name = method.Name,
                Currency = method.Currency,
                IsActive = method.IsActive,
                IsDeleted = method.IsDeleted
            })
            .ToListAsync(cancellationToken);

        return methods.ToDictionary(method => method.Id);
    }

    private static Dictionary<int, string> BuildCurrencyMap(
        IReadOnlyDictionary<int, PaymentMethodSnapshot> methods)
        => methods.Values.ToDictionary(
            method => method.Id,
            method => PaymentMethodCurrencyResolver.ResolveStoredOrName(method.Currency, method.Name));

    private sealed class PhysicalMovementGroup
    {
        public int? PaymentMethodId { get; set; }
        public CashTransactionType Type { get; set; }
        public string? CurrencyCode { get; set; }
        public decimal AmountLocal { get; set; }
        public decimal AmountUsd { get; set; }
    }

    private sealed class PaymentMethodSnapshot
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Currency { get; set; }
        public bool IsActive { get; set; }
        public bool IsDeleted { get; set; }
    }
}
