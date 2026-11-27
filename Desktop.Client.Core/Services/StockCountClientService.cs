using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;

namespace Desktop.Client.Services;

/// <summary>
/// HTTP client for <c>/api/stock-counts</c>. The endpoint is blind for list/detail/scan; the
/// review call is intentionally separated so Cashier flows never request theoretical stock/cost.
/// </summary>
public sealed class StockCountClientService : IStockCountClientService
{
    private const string BasePath = "api/stock-counts";

    private readonly HttpClient _httpClient;

    public StockCountClientService(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<StockCountSessionDto>> ListAsync(
        StockCountStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var url = status.HasValue
            ? $"{BasePath}?status={Uri.EscapeDataString(status.Value.ToString())}"
            : BasePath;

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<StockCountSessionDto>>(
                   cancellationToken: cancellationToken)
               ?? new List<StockCountSessionDto>();
    }

    public async Task<StockCountSessionDto> CreateAsync(
        CreateStockCountRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await _httpClient.PostAsJsonAsync(BasePath, request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredAsync<StockCountSessionDto>(response, cancellationToken);
    }

    public async Task<StockCountDetailDto> GetDetailAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"{BasePath}/{id}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredAsync<StockCountDetailDto>(response, cancellationToken);
    }

    public async Task<StockCountItemDto> ScanAsync(
        int id,
        string code,
        decimal quantityDelta = 1m,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        using var response = await _httpClient.PostAsJsonAsync(
            $"{BasePath}/{id}/scan",
            new ScanStockCountRequestDto(code.Trim(), quantityDelta),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredAsync<StockCountItemDto>(response, cancellationToken);
    }

    public async Task<StockCountItemDto> SetCountedQuantityAsync(
        int id,
        int productId,
        decimal countedQuantity,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync(
            $"{BasePath}/{id}/items/{productId}",
            new SetCountedQuantityRequestDto(countedQuantity),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredAsync<StockCountItemDto>(response, cancellationToken);
    }

    public async Task RemoveItemAsync(
        int id,
        int productId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync(
            $"{BasePath}/{id}/items/{productId}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<StockCountReviewDto> GetReviewAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"{BasePath}/{id}/review", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredAsync<StockCountReviewDto>(response, cancellationToken);
    }

    public Task<StockCountSessionDto> CloseAsync(int id, CancellationToken cancellationToken = default)
        => PostSessionActionAsync(id, "close", cancellationToken);

    public Task<StockCountSessionDto> ApplyAsync(int id, CancellationToken cancellationToken = default)
        => PostSessionActionAsync(id, "apply", cancellationToken);

    public Task<StockCountSessionDto> CancelAsync(int id, CancellationToken cancellationToken = default)
        => PostSessionActionAsync(id, "cancel", cancellationToken);

    private async Task<StockCountSessionDto> PostSessionActionAsync(
        int id,
        string action,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync($"{BasePath}/{id}/{action}", content: null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredAsync<StockCountSessionDto>(response, cancellationToken);
    }

    private static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException("La API de conteo físico devolvió una respuesta vacía.");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            ApiErrorParser.FromBody(body, $"Error HTTP {(int)response.StatusCode}"));
    }
}
