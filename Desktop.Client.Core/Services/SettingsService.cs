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
        await _httpClient.PostAsJsonAsync("api/settings/timezone", request);
    }
    
    private class TimeZoneResponse
    {
        public string Id { get; set; } = string.Empty;
    }
}
