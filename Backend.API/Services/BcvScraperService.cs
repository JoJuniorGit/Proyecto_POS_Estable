using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Helpers;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Backend.API.Services;

public class BcvScraperService
{
    private const string BcvUrl = "https://www.bcv.org.ve/";
    private const string DolarApiCurrentOfficialUrl = "https://ve.dolarapi.com/v1/dolares";
    private const string DolarApiOfficialHistoryUrl = "https://ve.dolarapi.com/v1/historicos/dolares/oficial";

    private readonly HttpClient _httpClient;
    private readonly ILogger<BcvScraperService> _logger;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _fallbackTimeout;

    public BcvScraperService(HttpClient httpClient, ILogger<BcvScraperService> logger, IConfiguration? configuration = null)
    {
        _httpClient = httpClient;
        _logger = logger;

        int timeoutSeconds = 10;
        if (configuration != null && int.TryParse(configuration["BcvSettings:TimeoutSeconds"], out int configuredTimeout) && configuredTimeout > 0)
        {
            timeoutSeconds = configuredTimeout;
        }
        _timeout = TimeSpan.FromSeconds(timeoutSeconds);

        int fallbackTimeoutSeconds = 6;
        if (configuration != null && int.TryParse(configuration["BcvSettings:FallbackTimeoutSeconds"], out int configuredFallbackTimeout) && configuredFallbackTimeout > 0)
        {
            fallbackTimeoutSeconds = configuredFallbackTimeout;
        }
        _fallbackTimeout = TimeSpan.FromSeconds(fallbackTimeoutSeconds);

        _httpClient.Timeout = _timeout;

        // BCV often blocks requests without a browser-like User-Agent
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
    }

    /// <summary>
    /// Seam for deterministic tests. Production uses the current Venezuela calendar date.
    /// </summary>
    protected virtual DateOnly GetTodayVenezuelaDate() => TimeZoneHelper.GetVenezuelaDate();

    public virtual async Task<decimal?> GetOfficialUsdRateAsync(CancellationToken cancellationToken = default)
    {
        var bcvRate = await TryGetBcvHtmlRateAsync(cancellationToken);
        if (bcvRate.HasValue)
        {
            return NormalizeRate(bcvRate.Value, "BCV HTML");
        }

        var dolarApiRate = await TryGetDolarApiOfficialRateAsync(cancellationToken);
        if (dolarApiRate.HasValue)
        {
            return NormalizeRate(dolarApiRate.Value, "DolarApi official");
        }

        _logger.LogWarning("[BCV Scraper Audit] No official USD rate could be obtained from BCV HTML or DolarApi. The active system rate will be preserved.");
        return null;
    }

    private decimal NormalizeRate(decimal rawRate, string source)
    {
        var roundedRate = PricingCalculator.RoundExchangeRateCeiling(rawRate);
        _logger.LogInformation("Official USD rate resolved from {Source}: Raw={RawRate}, Rounded={RoundedRate}", source, rawRate, roundedRate);
        return roundedRate;
    }

    private async Task<decimal?> TryGetBcvHtmlRateAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        try
        {
            _logger.LogInformation("Attempting to fetch official BCV USD rate from bcv.org.ve (timeout: {Timeout}s)...", _timeout.TotalSeconds);
            using var response = await _httpClient.GetAsync(BcvUrl, cts.Token);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(cts.Token);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // The BCV page has a div with id="dolar", inside which the rate is in a strong tag.
            var node = doc.DocumentNode.SelectSingleNode("//div[@id='dolar']//strong");
            if (node == null)
            {
                _logger.LogWarning("BCV USD rate node (//div[@id='dolar']//strong) not found on the page. Falling back to DolarApi.");
                return null;
            }

            var rateText = node.InnerText.Trim().Replace(",", ".");
            if (decimal.TryParse(rateText, NumberStyles.Any, CultureInfo.InvariantCulture, out var rawRate) && IsValidRate(rawRate))
            {
                return rawRate;
            }

            _logger.LogWarning("Failed to parse a valid BCV rate from '{RateText}'. Falling back to DolarApi.", node.InnerText);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Timeout fetching official BCV rate after {Timeout}s. Falling back to DolarApi.", _timeout.TotalSeconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch official BCV rate via HTML. Falling back to DolarApi.");
            return null;
        }
    }

    private async Task<decimal?> TryGetDolarApiOfficialRateAsync(CancellationToken cancellationToken)
    {
        var today = GetTodayVenezuelaDate();
        var isWeekend = today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        var currentRate = await TryGetDolarApiCurrentOfficialAsync(cancellationToken);

        if (!isWeekend)
        {
            if (currentRate != null)
            {
                return currentRate.Rate;
            }

            return await TryGetDolarApiHistoricalLatestAsync(today, cancellationToken);
        }

        // On weekends DolarApi's current official quote is usually the previous Friday rate.
        // Prefer the next official date already published in the official history (Monday or later).
        if (currentRate?.Date is DateOnly currentDate && currentDate > today)
        {
            _logger.LogInformation(
                "Weekend detected: DolarApi current official already has the next official rate {Rate} for {Date}.",
                currentRate.Rate,
                currentDate);
            return currentRate.Rate;
        }

        var nextRate = await TryGetDolarApiHistoricalNextAsync(today, cancellationToken);
        if (nextRate != null)
        {
            return nextRate.Rate;
        }

        _logger.LogWarning(
            "Weekend detected and DolarApi has no next official rate after {Today}. Refusing to persist the previous weekday rate.",
            today);
        return null;
    }

    private async Task<DolarApiOfficialRate?> TryGetDolarApiCurrentOfficialAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var document = await GetJsonDocumentAsync(DolarApiCurrentOfficialUrl, "DolarApi current official", cancellationToken);

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("fuente", out var source)
                    || !string.Equals(source.GetString(), "oficial", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryGetDecimal(item, "promedio", out var rate) || !IsValidRate(rate))
                {
                    _logger.LogWarning("DolarApi current official returned an invalid rate: {Rate}.", rate);
                    continue;
                }

                var date = TryGetDate(item, "fechaActualizacion");
                _logger.LogInformation("DolarApi current official rate: Raw={Rate}, Date={Date}.", rate, date);
                return new DolarApiOfficialRate(rate, date);
            }

            _logger.LogWarning("DolarApi current endpoint did not return a valid official USD rate.");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch DolarApi current official rate.");
            return null;
        }
    }

    private async Task<decimal?> TryGetDolarApiHistoricalLatestAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var history = await TryGetDolarApiOfficialHistoryAsync(cancellationToken);
        if (history == null || history.Count == 0)
        {
            return null;
        }

        var latest = history
            .Where(entry => entry.Date <= today)
            .OrderByDescending(entry => entry.Date)
            .FirstOrDefault();

        if (latest == null)
        {
            _logger.LogWarning("DolarApi official history has no rate on or before {Today}.", today);
            return null;
        }

        _logger.LogInformation("DolarApi historical official rate: Raw={Rate}, Date={Date}.", latest.Rate, latest.Date);
        return latest.Rate;
    }

    private async Task<DolarApiOfficialRate?> TryGetDolarApiHistoricalNextAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var history = await TryGetDolarApiOfficialHistoryAsync(cancellationToken);
        if (history == null || history.Count == 0)
        {
            return null;
        }

        var next = history
            .Where(entry => entry.Date > today)
            .OrderBy(entry => entry.Date)
            .FirstOrDefault();

        if (next == null)
        {
            return null;
        }

        _logger.LogInformation("DolarApi historical next official rate: Raw={Rate}, Date={Date}.", next.Rate, next.Date);
        return next;
    }

    private async Task<List<DolarApiOfficialRate>?> TryGetDolarApiOfficialHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var document = await GetJsonDocumentAsync(DolarApiOfficialHistoryUrl, "DolarApi official history", cancellationToken);
            var history = new List<DolarApiOfficialRate>();

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!TryGetDecimal(item, "promedio", out var rate) || !IsValidRate(rate))
                {
                    continue;
                }

                var date = TryGetDate(item, "fecha");
                if (!date.HasValue)
                {
                    continue;
                }

                history.Add(new DolarApiOfficialRate(rate, date));
            }

            if (history.Count == 0)
            {
                _logger.LogWarning("DolarApi official history returned no valid rates.");
                return null;
            }

            return history;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch DolarApi official history.");
            return null;
        }
    }

    private async Task<JsonDocument> GetJsonDocumentAsync(string url, string provider, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_fallbackTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Timeout fetching {Provider} after {Timeout}s.", provider, _fallbackTimeout.TotalSeconds);
            throw new TimeoutException($"Tiempo de espera agotado ({_fallbackTimeout.TotalSeconds}s) al consultar {provider}.", ex);
        }
    }

    private static bool TryGetDecimal(JsonElement element, string propertyName, out decimal value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(property.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static DateOnly? TryGetDate(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var raw = property.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTimeOffset))
        {
            var venezuelaTime = TimeZoneInfo.ConvertTime(dateTimeOffset, TimeZoneHelper.GetVenezuelaTimeZone());
            return DateOnly.FromDateTime(venezuelaTime.Date);
        }

        return null;
    }

    private static bool IsValidRate(decimal rate) => rate > 0 && rate < 1_000_000m;

    private sealed record DolarApiOfficialRate(decimal Rate, DateOnly? Date);
}
