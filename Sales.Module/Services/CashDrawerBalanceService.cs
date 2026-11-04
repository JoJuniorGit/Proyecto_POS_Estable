using Core.Common;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
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
        var groups = await _context.CashTransactions
            .AsNoTracking()
            .Where(t => t.SessionId == sessionId
                     && t.IsPhysicalCash
                     && t.Source != CashTransactionSource.Opening)
            .GroupBy(t => new { t.PaymentMethodId, t.Type, t.CurrencyCode })
            .Select(group => new
            {
                group.Key.PaymentMethodId,
                group.Key.Type,
                group.Key.CurrencyCode,
                AmountLocal = group.Sum(t => t.AmountLocal)
            })
            .ToListAsync(cancellationToken);

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
        var groups = await _context.CashTransactions
            .AsNoTracking()
            .Where(t => t.SessionId == sessionId
                     && t.IsPhysicalCash
                     && t.Source != CashTransactionSource.Opening)
            .GroupBy(t => new { t.PaymentMethodId, t.Type, t.CurrencyCode })
            .Select(group => new
            {
                group.Key.PaymentMethodId,
                group.Key.Type,
                group.Key.CurrencyCode,
                AmountLocal = group.Sum(t => t.AmountLocal),
                AmountUsd = group.Sum(t => t.AmountUsd)
            })
            .ToListAsync(cancellationToken);

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

    /// <summary>
    /// 8.211-T3c3c1 (S4): local drawer currency from the active profile. Single uses the primary
    /// currency; Dual uses the normalized secondary (VES displays as Bs.S, other ISO as-is).
    /// </summary>
    internal static string ResolveLocalCurrency(Core.DTOs.MonetaryProfileDto profile)
    {
        if (profile.Mode == Core.DTOs.MonetaryMode.Single)
        {
            return profile.PrimaryCurrencyCode;
        }

        var secondary = CurrencyCodeNormalizer.NormalizeToIso(profile.SecondaryCurrencyCode);
        return secondary is null || string.Equals(secondary, "VES", StringComparison.Ordinal)
            ? PaymentMethodCurrencyResolver.LocalCurrency
            : secondary;
    }

    private async Task<string> ResolveLocalDrawerCurrencyAsync(CancellationToken cancellationToken)
    {
        if (_monetaryProfileService is null)
        {
            return PaymentMethodCurrencyResolver.LocalCurrency;
        }

        try
        {
            var profile = await _monetaryProfileService.GetAsync(cancellationToken);
            return ResolveLocalCurrency(profile);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 8.211-T6-U1: balances must not be projected against a guessed drawer currency.
            throw new InvalidOperationException(
                "No se pudo leer el perfil monetario para calcular la caja. Verifique la configuración monetaria.",
                ex);
        }
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
}
