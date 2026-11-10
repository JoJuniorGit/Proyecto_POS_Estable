using System;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

/// <summary>8.211-T2b (S5): amount kind that selects the destination cash or digital precision.</summary>
public enum MonetaryAmountKind
{
    Digital = 0,
    Cash = 1
}

/// <summary>
/// 8.211-T2b (S5): central monetary conversion. Same-currency conversion is an exact identity and
/// never resolves a rate; cross-currency conversion applies the pair quote and rounds to the
/// destination currency precision.
/// </summary>
public interface IMonetaryConversionService
{
    Task<decimal> ConvertAsync(
        decimal amount,
        string fromCurrencyCode,
        string toCurrencyCode,
        MonetaryAmountKind kind = MonetaryAmountKind.Digital,
        DateOnly? rateDate = null,
        CancellationToken cancellationToken = default);
}
