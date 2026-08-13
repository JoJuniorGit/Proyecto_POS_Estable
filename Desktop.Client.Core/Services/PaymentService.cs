using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public class PaymentService : IPaymentService
{
    private readonly HttpClient _httpClient;
    private IEnumerable<PaymentMethodDto>? _activeMethodsCache;

    public PaymentService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IEnumerable<PaymentMethodDto>> GetActiveMethodsAsync()
    {
        // Use local cache to mitigate network latency as requested
        if (_activeMethodsCache != null)
        {
            return _activeMethodsCache;
        }

        var response = await _httpClient.GetAsync("api/PaymentMethods/active");
        response.EnsureSuccessStatusCode();
        var methods = await response.Content.ReadFromJsonAsync<IEnumerable<PaymentMethodDto>>();

        _activeMethodsCache = methods ?? new List<PaymentMethodDto>();
        return _activeMethodsCache;
    }

    public async Task<IEnumerable<PaymentMethodDto>> GetAllMethodsAsync()
    {
        var response = await _httpClient.GetAsync("api/PaymentMethods");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IEnumerable<PaymentMethodDto>>() ?? new List<PaymentMethodDto>();
    }

    public async Task<PaymentMethodDto> CreateAsync(PaymentMethodDto method)
    {
        var response = await _httpClient.PostAsJsonAsync("api/PaymentMethods", method);
        response.EnsureSuccessStatusCode();

        // Invalidate cache
        _activeMethodsCache = null;

        return await response.Content.ReadFromJsonAsync<PaymentMethodDto>() ?? method;
    }

    public async Task<PaymentMethodDto> UpdateAsync(PaymentMethodDto method)
    {
        var response = await _httpClient.PutAsJsonAsync($"api/PaymentMethods/{method.Id}", method);
        response.EnsureSuccessStatusCode();

        // Invalidate cache
        _activeMethodsCache = null;

        return await response.Content.ReadFromJsonAsync<PaymentMethodDto>() ?? method;
    }

    public async Task DeleteAsync(int id)
    {
        var response = await _httpClient.DeleteAsync($"api/PaymentMethods/{id}");
        response.EnsureSuccessStatusCode();

        // Invalidate cache
        _activeMethodsCache = null;
    }

    public void InvalidateCache()
    {
        _activeMethodsCache = null;
    }
}
