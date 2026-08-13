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
        var response = await _httpClient.PostAsJsonAsync("api/dailyclosure", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DailyClosureDto>() ?? new();
    }
}
