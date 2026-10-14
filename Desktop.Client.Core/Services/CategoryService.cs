using Core.DTOs;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

/// <summary>
/// 8.179-T2 (S3): cliente HTTP del CRUD de categorías. Los errores del servidor (409 duplicado/
/// ciclo/borrado bloqueado, 404, 400) se traducen con <see cref="ApiErrorParser"/> para que la UI
/// muestre el mensaje ProblemDetails legible sin romper el diálogo.
/// </summary>
public class CategoryService : ICategoryService
{
    private readonly HttpClient _httpClient;

    public CategoryService(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<List<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("api/categories", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<List<CategoryDto>>(cancellationToken: cancellationToken)
               ?? new List<CategoryDto>();
    }

    public async Task<CategoryDto> CreateAsync(CreateCategoryRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await _httpClient.PostAsJsonAsync("api/categories", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, cancellationToken);
        }

        return await ReadRequiredAsync(response, cancellationToken);
    }

    public async Task<CategoryDto> UpdateAsync(int id, UpdateCategoryRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await _httpClient.PutAsJsonAsync($"api/categories/{id}", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, cancellationToken);
        }

        return await ReadRequiredAsync(response, cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"api/categories/{id}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, cancellationToken);
        }
    }

    private static async Task<CategoryDto> ReadRequiredAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<CategoryDto>(cancellationToken: cancellationToken)
        ?? throw new InvalidOperationException("La API de categorías devolvió una respuesta vacía.");

    private static async Task<Exception> CreateErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new Exception(ApiErrorParser.FromBody(body, $"Error HTTP {(int)response.StatusCode}"));
    }
}
