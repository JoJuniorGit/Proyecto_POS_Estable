using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Common;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Inventory.Module.Services;

/// <summary>
/// 8.211-T2a (S4): resolución de la tasa de un par para una fecha. USD/VES (en cualquier
/// dirección) se lee de <c>ExchangeRateHistory</c> —la tasa BCV es la fuente autoritativa y no se
/// cachea—; el resto de pares se carga manualmente para la fecha exacta, sin fallback, y el
/// inverso se deriva a 8 decimales. Este servicio no convierte montos ni conoce flujos de venta:
/// la integración con checkout/caja/recibos es F3.
/// </summary>
public class CurrencyRateService : ICurrencyRateService
{
    private const string UsdCode = "USD";
    private const string VesCode = "VES";
    private const decimal MaxManualRate = 1_000_000m;
    private const int ManualRateDecimals = 8;
    private static readonly TimeSpan ManualQuoteCacheDuration = TimeSpan.FromMinutes(5);

    private const string MalformedFromMessage =
        "El código de la moneda origen debe ser un código ISO 4217 de tres letras.";
    private const string MalformedToMessage =
        "El código de la moneda destino debe ser un código ISO 4217 de tres letras.";
    private const string SameCodeMessage =
        "El par de monedas debe tener códigos distintos.";
    private const string BcvPairManualMessage =
        "El par USD/VES se alimenta de la tasa BCV y no admite carga manual.";
    private const string InvalidRateMessage =
        "La tasa debe ser mayor a cero y menor o igual a 1.000.000.";
    private const string FutureDateMessage =
        "La fecha de la tasa no puede ser futura.";

    private readonly InventoryDbContext _context;
    private readonly IMemoryCache? _cache;

    public CurrencyRateService(InventoryDbContext context, IMemoryCache? cache = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _cache = cache;
    }

    public async Task<CurrencyRateQuote> GetRateAsync(
        string fromCurrencyCode,
        string toCurrencyCode,
        DateOnly? rateDate = null,
        CancellationToken cancellationToken = default)
    {
        var from = NormalizeAndValidate(fromCurrencyCode, MalformedFromMessage);
        var to = NormalizeAndValidate(toCurrencyCode, MalformedToMessage);
        var date = rateDate ?? TimeZoneHelper.GetVenezuelaDate();

        if (from == to)
        {
            return new CurrencyRateQuote(1m, CurrencyRateSource.Identity, date, from, to);
        }

        return IsBcvPair(from, to)
            ? await GetBcvQuoteAsync(from, to, date, cancellationToken)
            : await GetManualQuoteAsync(from, to, date, cancellationToken);
    }

    public async Task<CurrencyRateQuote> SetManualRateAsync(
        ManualCurrencyRateRequest request,
        int? updatedByUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var from = NormalizeAndValidate(request.FromCurrencyCode, MalformedFromMessage);
        var to = NormalizeAndValidate(request.ToCurrencyCode, MalformedToMessage);
        var date = request.RateDate ?? TimeZoneHelper.GetVenezuelaDate();

        if (from == to)
        {
            throw new ArgumentException(SameCodeMessage);
        }

        if (IsBcvPair(from, to))
        {
            throw new ArgumentException(BcvPairManualMessage);
        }

        if (request.Rate <= 0m || request.Rate > MaxManualRate)
        {
            throw new ArgumentException(InvalidRateMessage);
        }

        if (date > TimeZoneHelper.GetVenezuelaDate())
        {
            throw new ArgumentException(FutureDateMessage);
        }

        var roundedRate = Math.Round(request.Rate, ManualRateDecimals, MidpointRounding.AwayFromZero);

        // Un valor positivo por debajo de la precisión de la tabla (p. ej. 0.000000001) redondea a
        // cero y violaría CK_CurrencyPairRates_Rate_Positive como error de base. Se rechaza en el
        // dominio con el mismo mensaje de tasa inválida.
        if (roundedRate <= 0m)
        {
            throw new ArgumentException(InvalidRateMessage);
        }

        var row = await _context.CurrencyPairRates.FirstOrDefaultAsync(
            r => r.RateDate == date
                && r.FromCurrencyCode == from
                && r.ToCurrencyCode == to,
            cancellationToken);

        if (row is null)
        {
            row = new CurrencyPairRate
            {
                RateDate = date,
                FromCurrencyCode = from,
                ToCurrencyCode = to,
                Rate = roundedRate,
                UpdatedAtUtc = DateTime.UtcNow,
                UpdatedByUserId = updatedByUserId
            };
            _context.CurrencyPairRates.Add(row);
        }
        else
        {
            row.Rate = roundedRate;
            row.UpdatedAtUtc = DateTime.UtcNow;
            row.UpdatedByUserId = updatedByUserId;
        }

        await _context.SaveChangesAsync(cancellationToken);

        // El par directo y su inverso derivado comparten origen: ambos deben dejar de servirse
        // desde caché para que la siguiente lectura observe la tasa recién guardada.
        _cache?.Remove(BuildCacheKey(date, from, to));
        _cache?.Remove(BuildCacheKey(date, to, from));

        return new CurrencyRateQuote(roundedRate, CurrencyRateSource.Manual, date, from, to);
    }

    public async Task<IReadOnlyList<ManualCurrencyRateDto>> GetManualRatesAsync(
        DateOnly? rateDate = null,
        CancellationToken cancellationToken = default)
    {
        var date = rateDate ?? TimeZoneHelper.GetVenezuelaDate();

        return await _context.CurrencyPairRates
            .AsNoTracking()
            .Where(r => r.RateDate == date)
            .OrderBy(r => r.FromCurrencyCode)
            .ThenBy(r => r.ToCurrencyCode)
            .Select(r => new ManualCurrencyRateDto
            {
                FromCurrencyCode = r.FromCurrencyCode,
                ToCurrencyCode = r.ToCurrencyCode,
                Rate = r.Rate,
                RateDate = r.RateDate,
                UpdatedAtUtc = r.UpdatedAtUtc,
                UpdatedByUserId = r.UpdatedByUserId
            })
            .ToListAsync(cancellationToken);
    }

    private async Task<CurrencyRateQuote> GetBcvQuoteAsync(
        string from,
        string to,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var record = await _context.ExchangeRateHistory
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Date == date, cancellationToken);

        // Paridad con InventoryService.GetTodayExchangeRateAsync: un registro del día con tasa 0
        // no es utilizable y el fallback sigue siendo el último registro positivo <= fecha.
        if (record is null || record.Rate <= 0m)
        {
            record = await _context.ExchangeRateHistory
                .AsNoTracking()
                .Where(r => r.Date <= date && r.Rate > 0m)
                .OrderByDescending(r => r.Date)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (record is null || record.Rate <= 0m)
        {
            throw new InvalidOperationException(
                $"No hay tasa BCV registrada para la fecha {date:yyyy-MM-dd}.");
        }

        var bcvRate = PricingCalculator.RoundExchangeRateCeiling(record.Rate);
        if (from == UsdCode)
        {
            return new CurrencyRateQuote(bcvRate, CurrencyRateSource.Bcv, date, from, to);
        }

        var inverseRate = Math.Round(1m / bcvRate, ManualRateDecimals, MidpointRounding.AwayFromZero);
        return new CurrencyRateQuote(inverseRate, CurrencyRateSource.BcvInverse, date, from, to);
    }

    private async Task<CurrencyRateQuote> GetManualQuoteAsync(
        string from,
        string to,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildCacheKey(date, from, to);
        if (_cache is not null && _cache.TryGetValue(cacheKey, out CurrencyRateQuote? cached) && cached is not null)
        {
            return cached;
        }

        var exactRow = await _context.CurrencyPairRates
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.RateDate == date
                    && r.FromCurrencyCode == from
                    && r.ToCurrencyCode == to,
                cancellationToken);

        CurrencyRateQuote quote;
        if (exactRow is not null)
        {
            quote = new CurrencyRateQuote(exactRow.Rate, CurrencyRateSource.Manual, date, from, to);
        }
        else
        {
            var inverseRow = await _context.CurrencyPairRates
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.RateDate == date
                        && r.FromCurrencyCode == to
                        && r.ToCurrencyCode == from,
                    cancellationToken);

            if (inverseRow is null)
            {
                throw new InvalidOperationException(
                    $"No hay tasa manual configurada para {from}->{to} en la fecha {date:yyyy-MM-dd}.");
            }

            var inverseRate = Math.Round(1m / inverseRow.Rate, ManualRateDecimals, MidpointRounding.AwayFromZero);
            quote = new CurrencyRateQuote(inverseRate, CurrencyRateSource.ManualInverse, date, from, to);
        }

        // Los pares manuales son de lectura recurrente (catálogo/checkout futuro), pero la tasa se
        // reescribe a diario: expiración corta + invalidación explícita al guardar.
        _cache?.Set(cacheKey, quote, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ManualQuoteCacheDuration,
            Size = 1
        });

        return quote;
    }

    private static bool IsBcvPair(string from, string to)
        => (from == UsdCode && to == VesCode) || (from == VesCode && to == UsdCode);

    private static string NormalizeAndValidate(string? code, string malformedMessage)
    {
        var normalized = Iso4217.Normalize(code);
        if (normalized is null || !IsWellFormed(normalized))
        {
            throw new ArgumentException(malformedMessage);
        }

        if (!Iso4217.IsSupported(normalized))
        {
            throw new ArgumentException($"El código '{normalized}' no es un código ISO 4217 reconocido.");
        }

        return normalized;
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

    private static string BuildCacheKey(DateOnly date, string from, string to)
        => $"currency_pair_rate_{date:yyyy-MM-dd}_{from}_{to}";
}
