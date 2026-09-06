using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Backend.API.Services;

public class BcvScraperService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BcvScraperService> _logger;
    private readonly TimeSpan _timeout;

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
        _httpClient.Timeout = _timeout;
        
        // BCV often blocks requests without a browser-like User-Agent
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
    }

    public virtual async Task<decimal?> GetOfficialUsdRateAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        try
        {
            _logger.LogInformation("Attempting to fetch official BCV USD rate from bcv.org.ve (timeout: {Timeout}s)...", _timeout.TotalSeconds);
            var response = await _httpClient.GetAsync("https://www.bcv.org.ve/", cts.Token);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(cts.Token);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // The BCV page has a div with id="dolar", inside which the rate is in a strong tag.
            // Based on structure: <div id="dolar"> ... <div class="centrado"><strong>36,45670000</strong></div> </div>
            var node = doc.DocumentNode.SelectSingleNode("//div[@id='dolar']//strong");
            if (node == null)
            {
                _logger.LogWarning("BCV USD rate node (//div[@id='dolar']//strong) not found on the page.");
                throw new InvalidOperationException("No se encontró el elemento contenedor de la tasa oficial en el portal del BCV.");
            }

            var rateText = node.InnerText.Trim().Replace(",", ".");
            if (decimal.TryParse(rateText, NumberStyles.Any, CultureInfo.InvariantCulture, out var rawRate))
            {
                // Round to two decimal places by taking ceiling: 3.111 -> 3.12
                var roundedRate = Math.Ceiling(rawRate * 100m) / 100m;
                _logger.LogInformation("Successfully extracted BCV USD rate: Raw={RawRate}, Rounded={RoundedRate}", rawRate, roundedRate);
                return roundedRate;
            }

            _logger.LogWarning("Failed to parse extracted BCV rate text to decimal: '{RateText}'", node.InnerText);
            throw new InvalidOperationException($"El formato numérico devuelto por el BCV no es válido: '{node.InnerText.Trim()}'.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Timeout fetching official BCV rate after {Timeout}s.", _timeout.TotalSeconds);
            throw new TimeoutException($"Tiempo de espera agotado ({_timeout.TotalSeconds}s) al consultar el portal del BCV.", ex);
        }
    }
}
