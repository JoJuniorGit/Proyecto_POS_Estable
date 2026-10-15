using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public class SettingsService : ISettingsService
{
    private readonly HttpClient _httpClient;

    public SettingsService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> GetTimeZoneAsync()
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<TimeZoneResponse>("api/settings/timezone");
            return response?.Id ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task SetTimeZoneAsync(string timeZoneId)
    {
        var request = new { Id = timeZoneId };
        using var response = await _httpClient.PostAsJsonAsync("api/settings/timezone", request);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> GetCurrencyFormatAsync()
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<CurrencyFormatResponse>("api/settings/currency-format");
            return response?.Format ?? "Venezuelan";
        }
        catch
        {
            return "Venezuelan";
        }
    }

    public async Task SetCurrencyFormatAsync(string format)
    {
        var request = new { Format = format };
        using var response = await _httpClient.PutAsJsonAsync("api/settings/currency-format", request);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> GetAllowNegativeStockAsync()
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<AllowNegativeStockResponse>("api/settings/allow-negative-stock");
            return response?.Allowed ?? false;
        }
        catch
        {
            return false;
        }
    }

    public async Task SetAllowNegativeStockAsync(bool allowed)
    {
        var request = new { Allowed = allowed };
        using var response = await _httpClient.PutAsJsonAsync("api/settings/allow-negative-stock", request);
        response.EnsureSuccessStatusCode();
    }

    public async Task RestartSystemAsync()
    {
        using var response = await _httpClient.PostAsync("api/administration/restart", null);
        response.EnsureSuccessStatusCode();
    }

    private class TimeZoneResponse
    {
        public string Id { get; set; } = string.Empty;
    }

    private class CurrencyFormatResponse
    {
        public string Format { get; set; } = "Venezuelan";
    }

    private class AllowNegativeStockResponse
    {
        public bool Allowed { get; set; }
    }
}
