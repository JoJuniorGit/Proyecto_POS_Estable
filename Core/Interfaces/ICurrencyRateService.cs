using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;

namespace Core.Interfaces;

/// <summary>8.211-T2a (S4): origen de la tasa resuelta para un par de monedas.</summary>
public enum CurrencyRateSource
{
    Identity,
    Bcv,
    BcvInverse,
    Manual,
    ManualInverse
}

/// <summary>
/// 8.211-T2a (S4): cotización de un par para una fecha. La tasa pertenece explícitamente al par
/// <see cref="FromCurrencyCode"/>/<see cref="ToCurrencyCode"/>; nunca se deduce de un único valor global.
/// </summary>
public sealed record CurrencyRateQuote(
    decimal Rate,
    CurrencyRateSource Source,
    DateOnly RateDate,
    string FromCurrencyCode,
    string ToCurrencyCode);

/// <summary>
/// 8.211-T2a (S4): resolución y carga de tasas por par. USD/VES se alimenta de BCV
/// (<c>ExchangeRateHistory</c>); los demás pares se cargan manualmente por día.
/// </summary>
public interface ICurrencyRateService
{
    Task<CurrencyRateQuote> GetRateAsync(
        string fromCurrencyCode,
        string toCurrencyCode,
        DateOnly? rateDate = null,
        CancellationToken cancellationToken = default);

    Task<CurrencyRateQuote> SetManualRateAsync(
        ManualCurrencyRateRequest request,
        int? updatedByUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ManualCurrencyRateDto>> GetManualRatesAsync(
        DateOnly? rateDate = null,
        CancellationToken cancellationToken = default);
}
