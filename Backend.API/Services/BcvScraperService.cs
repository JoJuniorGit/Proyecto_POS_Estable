using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Backend.API.Services;

public class BcvScraperService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BcvScraperService> _logger;

    public BcvScraperService(HttpClient httpClient, ILogger<BcvScraperService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        
        // BCV often blocks requests without a browser-like User-Agent
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
    }

    public async Task<decimal?> GetOfficialUsdRateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Attempting to fetch official BCV USD rate from bcv.org.ve...");
            var response = await _httpClient.GetAsync("https://www.bcv.org.ve/", cancellationToken);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // The BCV page has a div with id="dolar", inside which the rate is in a strong tag.
            // Based on structure: <div id="dolar"> ... <div class="centrado"><strong>36,45670000</strong></div> </div>
            var node = doc.DocumentNode.SelectSingleNode("//div[@id='dolar']//strong");
            if (node == null)
            {
                _logger.LogWarning("BCV USD rate node (//div[@id='dolar']//strong) not found on the page.");
                return null;
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
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while fetching the official BCV rate.");
            return null;
        }
    }
}
