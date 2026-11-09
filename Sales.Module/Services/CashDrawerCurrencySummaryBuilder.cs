using Core.Common;
using Core.DTOs;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Sales.Module.Services;

/// <summary>
/// S1/S2: resumen de caja por moneda activa del régimen. La moneda local va primero; los
/// ingresos excluyen apertura y reversas de caja chica; los egresos incluyen salidas físicas
/// netas de reversas y excluyen el cierre. Los montos se toman del snapshot de la transacción,
/// nunca de una conversión con la tasa actual.
/// </summary>
public static class CashDrawerCurrencySummaryBuilder
{
    public static IReadOnlyList<CurrencySessionSummaryDto> Build(
        CashDrawerSession session,
        MonetaryProfileDto? profile)
    {
        ArgumentNullException.ThrowIfNull(session);

        var localCode = profile is null
            ? CurrencyCodeNormalizer.NormalizeToIso(PaymentMethodCurrencyResolver.LocalCurrency)!
            : CurrencyCodeNormalizer.NormalizeToIso(CashDrawerBalanceService.ResolveLocalCurrency(profile))!;
        var primaryCode = CurrencyCodeNormalizer.NormalizeToIso(profile?.PrimaryCurrencyCode)
            ?? PaymentMethodCurrencyResolver.Usd;
        var secondaryCode = profile?.Mode == MonetaryMode.Dual
            ? CurrencyCodeNormalizer.NormalizeToIso(profile.SecondaryCurrencyCode)
            : null;

        var codes = new List<string>(3);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AddCode(localCode);
        AddCode(primaryCode);
        AddCode(secondaryCode);

        var transactions = session.Transactions;
        var result = new List<CurrencySessionSummaryDto>(codes.Count);

        foreach (var code in codes)
        {
            var opening = string.Equals(code, localCode, StringComparison.Ordinal)
                ? session.OpeningBalanceLocal
                : string.Equals(code, primaryCode, StringComparison.Ordinal)
                    ? session.OpeningBalanceUsd
                    : 0m;

            var inflows = transactions
                .Where(transaction => transaction.IsPhysicalCash
                    && transaction.Type == CashTransactionType.Income
                    && transaction.Source != CashTransactionSource.Opening
                    && transaction.Source != CashTransactionSource.PettyCashReversal)
                .Sum(transaction => AmountFor(transaction, code, localCode, primaryCode));

            var expenses = transactions
                .Where(transaction => transaction.IsPhysicalCash
                    && transaction.Type == CashTransactionType.Expense
                    && transaction.Source != CashTransactionSource.Closing)
                .Sum(transaction => AmountFor(transaction, code, localCode, primaryCode));

            var reversals = transactions
                .Where(transaction => transaction.IsPhysicalCash
                    && transaction.Source == CashTransactionSource.PettyCashReversal)
                .Sum(transaction => AmountFor(transaction, code, localCode, primaryCode));

            var outflows = expenses - reversals;

            result.Add(new CurrencySessionSummaryDto
            {
                CurrencyCode = code,
                CurrencySymbol = ResolveSymbol(profile, code),
                OpeningBalance = opening,
                TotalPhysicalInflows = inflows,
                TotalPhysicalOutflows = outflows,
                ExpectedCashBalance = opening + inflows - outflows,
                IsLocal = string.Equals(code, localCode, StringComparison.Ordinal)
            });
        }

        return result;

        void AddCode(string? code)
        {
            var normalized = CurrencyCodeNormalizer.NormalizeToIso(code);
            if (normalized is null || !seen.Add(normalized))
            {
                return;
            }

            codes.Add(normalized);
        }
    }

    private static decimal AmountFor(
        CashTransaction transaction,
        string currencyCode,
        string localCode,
        string primaryCode)
    {
        var transactionCode = CurrencyCodeNormalizer.NormalizeToIso(transaction.CurrencyCode)
            ?? localCode;
        if (!string.Equals(transactionCode, currencyCode, StringComparison.Ordinal))
        {
            return 0m;
        }

        if (string.Equals(currencyCode, localCode, StringComparison.Ordinal))
        {
            return transaction.AmountLocal;
        }

        if (string.Equals(currencyCode, primaryCode, StringComparison.Ordinal))
        {
            if (transaction.AmountUsd != 0m)
            {
                return transaction.AmountUsd;
            }

            return transaction.ExchangeRate > 0m
                ? transaction.AmountLocal / transaction.ExchangeRate
                : 0m;
        }

        return 0m;
    }

    private static string ResolveSymbol(MonetaryProfileDto? profile, string normalizedCode)
    {
        if (profile is null)
        {
            return string.Equals(normalizedCode, "VES", StringComparison.Ordinal)
                ? PaymentMethodCurrencyResolver.LocalCurrency
                : normalizedCode;
        }

        if (Matches(normalizedCode, profile.PrimaryCurrencyCode))
        {
            return string.IsNullOrWhiteSpace(profile.PrimaryCurrencySymbol)
                ? normalizedCode
                : profile.PrimaryCurrencySymbol;
        }

        if (profile.Mode == MonetaryMode.Dual
            && Matches(normalizedCode, profile.SecondaryCurrencyCode))
        {
            return string.IsNullOrWhiteSpace(profile.SecondaryCurrencySymbol)
                ? normalizedCode
                : profile.SecondaryCurrencySymbol;
        }

        return normalizedCode;
    }

    private static bool Matches(string normalizedCode, string? candidate)
        => string.Equals(
            CurrencyCodeNormalizer.NormalizeToIso(candidate),
            normalizedCode,
            StringComparison.Ordinal);
}
