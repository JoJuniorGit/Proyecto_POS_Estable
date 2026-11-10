using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Common;
using Core.Interfaces;

namespace Inventory.Module.Services;

/// <summary>
/// 8.211-T2b (S5): conversion over <see cref="ICurrencyRateService"/>. It does not resolve rates
/// itself and does not touch checkout, sales, cash, receipts, or clients.
/// </summary>
public class MonetaryConversionService : IMonetaryConversionService
{
    private const string MalformedFromMessage =
        "El código de la moneda origen debe ser un código ISO 4217 de tres letras.";
    private const string MalformedToMessage =
        "El código de la moneda destino debe ser un código ISO 4217 de tres letras.";

    private readonly ICurrencyRateService _rateService;

    public MonetaryConversionService(ICurrencyRateService rateService)
    {
        _rateService = rateService ?? throw new ArgumentNullException(nameof(rateService));
    }

    public async Task<decimal> ConvertAsync(
        decimal amount,
        string fromCurrencyCode,
        string toCurrencyCode,
        MonetaryAmountKind kind = MonetaryAmountKind.Digital,
        DateOnly? rateDate = null,
        CancellationToken cancellationToken = default)
    {
        var from = NormalizeAndValidate(fromCurrencyCode, MalformedFromMessage);
        var to = NormalizeAndValidate(toCurrencyCode, MalformedToMessage);

        // The identity invariant is intentional: a same-currency amount is returned untouched,
        // with all its decimal places, before any rate lookup or rounding.
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return amount;
        }

        var quote = await _rateService.GetRateAsync(from, to, rateDate, cancellationToken);
        var precision = CurrencyPrecisionCatalog.For(to);
        var decimals = kind == MonetaryAmountKind.Cash
            ? precision.CashDecimals
            : precision.DigitalDecimals;

        return Math.Round(amount * quote.Rate, decimals, MidpointRounding.AwayFromZero);
    }

    private static string NormalizeAndValidate(string? code, string malformedMessage)
    {
        var normalized = Iso4217.Normalize(code);
        if (normalized is null)
        {
            throw new ArgumentException(malformedMessage);
        }

        var isoCode = CurrencyCodeNormalizer.NormalizeToIso(normalized);
        if (isoCode is not null)
        {
            return isoCode;
        }

        if (!IsWellFormed(normalized))
        {
            throw new ArgumentException(malformedMessage);
        }

        throw new ArgumentException($"El código '{normalized}' no es un código ISO 4217 reconocido.");
    }

    private static bool IsWellFormed(string normalizedCode)
    {
        if (normalizedCode.Length != 3)
        {
            return false;
        }

        foreach (var character in normalizedCode)
        {
            if (character is < 'A' or > 'Z')
            {
                return false;
            }
        }

        return true;
    }
}
