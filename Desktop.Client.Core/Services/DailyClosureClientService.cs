using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public class DailyClosureClientService : IDailyClosureClientService
{
    private readonly HttpClient _httpClient;

    public DailyClosureClientService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<ExpectedTotalDto>> GetExpectedTotalsAsync(DateTime dateUtc)
    {
        var response = await _httpClient.GetAsync($"api/dailyclosure/expected-totals?dateUtc={dateUtc:O}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<ExpectedTotalDto>>() ?? new();
    }

    public async Task<DailyClosureDto> CreateClosureAsync(CreateClosureRequest request)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/dailyclosure")
        {
            Content = JsonContent.Create(request)
        };
        // 8.149 (SRE-02): el backend exige Idempotency-Key; el retry del ResilienceHandler reenvia el
        // MISMO request, por lo que la clave se mantiene estable dentro del intento logico.
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await _httpClient.SendAsync(httpRequest);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DailyClosureDto>() ?? new();
    }
}
