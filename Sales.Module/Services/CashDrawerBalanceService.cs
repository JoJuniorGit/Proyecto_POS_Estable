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
/// agrega por (método, tipo) en SQL y solo materializa una fila por combinación; la atribución de
/// moneda se resuelve con <see cref="PaymentMethodCurrencyResolver"/>.
/// </summary>
internal sealed class CashDrawerBalanceService
{
    private readonly SalesDbContext _context;

    public CashDrawerBalanceService(SalesDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
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
