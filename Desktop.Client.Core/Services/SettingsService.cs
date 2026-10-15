using Core.DTOs;
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

    // 8.184-T3 (S4): molde allow-negative-stock — lectura al abrir cada superficie; ante error o
    // respuesta vacía rigen los defaults del sistema (ExactMath / ExactMath / piso activo).
    public async Task<PricingPoliciesDto> GetPricingPoliciesAsync()
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<PricingPoliciesDto>("api/settings/pricing-policies");
            return response ?? new PricingPoliciesDto();
        }
        catch
        {
            return new PricingPoliciesDto();
        }
    }

    // 8.184-T4 (S2): PUT del bloque completo (regla USD, regla de efectivo Bs.S y toggle de piso)
    // para el auto-guardado de Ajustes; EnsureSuccessStatusCode deja visible el fallo al ViewModel.
    public async Task SetPricingPoliciesAsync(PricingPoliciesDto policies)
    {
        using var response = await _httpClient.PutAsJsonAsync("api/settings/pricing-policies", policies);
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
