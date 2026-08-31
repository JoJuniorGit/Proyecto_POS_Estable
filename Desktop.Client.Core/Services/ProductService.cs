using Core.Entities;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public class ProductService : IProductService
{
    private readonly HttpClient _httpClient;

    public ProductService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<Product>> GetAllAsync()
    {
        return await _httpClient.GetFromJsonAsync<List<Product>>("api/products") ?? new List<Product>();
    }

    public async Task<Product?> GetByIdAsync(int id)
    {
        var response = await _httpClient.GetAsync($"api/products/{id}");
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(string.IsNullOrWhiteSpace(err) ? $"Error HTTP {(int)response.StatusCode}" : err);
        }
        var rawJson = await response.Content.ReadAsStringAsync();
        return System.Text.Json.JsonSerializer.Deserialize<Product>(rawJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    public async Task<Product> CreateAsync(Product product)
    {
        var response = await _httpClient.PostAsJsonAsync("api/products", product);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(string.IsNullOrWhiteSpace(err) ? $"Error HTTP {(int)response.StatusCode}" : err);
        }
        var rawJson = await response.Content.ReadAsStringAsync();
        var created = System.Text.Json.JsonSerializer.Deserialize<Product>(rawJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return created ?? throw new System.Exception("No se pudo deserializar el producto devuelto.");
    }

    public async Task UpdateAsync(Product product)
    {
        var response = await _httpClient.PutAsJsonAsync($"api/products/{product.Id}", product);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(string.IsNullOrWhiteSpace(err) ? $"Error HTTP {(int)response.StatusCode}" : err);
        }
    }

    public async Task SetStatusAsync(int id, bool isActive, bool isDeleted)
    {
        var response = await _httpClient.PutAsJsonAsync($"api/products/{id}/status", new { IsActive = isActive, IsDeleted = isDeleted });
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(string.IsNullOrWhiteSpace(err) ? $"Error HTTP {(int)response.StatusCode}" : err);
        }
    }

    public async Task<string> DeleteAsync(int id, bool hardDelete = false)
    {
        var response = await _httpClient.DeleteAsync($"api/products/{id}?hardDelete={hardDelete.ToString().ToLower()}");
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(string.IsNullOrWhiteSpace(err) ? $"Error HTTP {(int)response.StatusCode}" : err);
        }
        var resObj = await response.Content.ReadFromJsonAsync<DeleteResultResponse>();
        return resObj?.Result ?? "ok";
    }

    public async Task RestoreAsync(int id)
    {
        var response = await _httpClient.PostAsync($"api/products/{id}/restore", null);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(string.IsNullOrWhiteSpace(err) ? $"Error HTTP {(int)response.StatusCode}" : err);
        }
    }

    private class DeleteResultResponse { public string? Result { get; set; } }

    public async Task AdjustStockAsync(int productId, decimal quantityChange, string reason)
    {
        var dto = new { QuantityChange = quantityChange, Reason = reason };
        var response = await _httpClient.PostAsJsonAsync($"api/products/{productId}/adjust-stock", dto);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(string.IsNullOrWhiteSpace(err) ? $"Error HTTP {(int)response.StatusCode}" : err);
        }
    }

    public async Task<Core.DTOs.ProductQuickInfoDto?> GetQuickInfoAsync(string sku)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<Core.DTOs.ProductQuickInfoDto>($"api/products/quick-check/{sku}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<List<Core.DTOs.ProductQuickInfoDto>> GetSuggestionsAsync(string filter, bool activeOnly, System.Threading.CancellationToken token)
    {
        try
        {
            var url = $"api/products/suggestions?filter={System.Uri.EscapeDataString(filter ?? string.Empty)}&activeOnly={activeOnly.ToString().ToLower()}";
            return await _httpClient.GetFromJsonAsync<List<Core.DTOs.ProductQuickInfoDto>>(url, token)
                   ?? new List<Core.DTOs.ProductQuickInfoDto>();
        }
        catch (System.OperationCanceledException)
        {
            // Propagate cancellation
            throw;
        }
        catch
        {
            return new List<Core.DTOs.ProductQuickInfoDto>();
        }
    }

    public async Task<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>> GetPagedAsync(string? filter, int page, int pageSize, string? statusFilter = null, string? sortBy = null, bool isDescending = false, System.Threading.CancellationToken token = default)
    {
        var url = $"api/products?filter={System.Uri.EscapeDataString(filter ?? string.Empty)}&page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            url += $"&status={System.Uri.EscapeDataString(statusFilter)}";
        }
        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            url += $"&sortBy={System.Uri.EscapeDataString(sortBy)}&isDescending={isDescending}";
        }
        return await _httpClient.GetFromJsonAsync<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>>(url, token)
               ?? new Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>();
    }

    public async Task<List<Core.DTOs.ProductDto>> GetVariantsAsync(int parentProductId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<Core.DTOs.ProductDto>>($"api/products/{parentProductId}/variants")
                   ?? new List<Core.DTOs.ProductDto>();
        }
        catch
        {
            return new List<Core.DTOs.ProductDto>();
        }
    }

    public async Task<List<Core.DTOs.ProductDto>> GetParentsAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<Core.DTOs.ProductDto>>("api/products/parents")
                   ?? new List<Core.DTOs.ProductDto>();
        }
        catch
        {
            return new List<Core.DTOs.ProductDto>();
        }
    }
}
