using Core.DTOs;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

/// <summary>
/// Paso 15: transporte HTTP del reporte de rotacion. Todos los parametros viajan en query string
/// para que el backend mantenga la pagina y el resumen calculados server-side.
/// </summary>
public sealed class InventoryRotationService : IInventoryRotationService
{
    private readonly HttpClient _httpClient;

    public InventoryRotationService(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<InventoryRotationReportDto> GetReportAsync(
        InventoryRotationFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        using var response = await _httpClient.GetAsync(
            $"api/inventory-rotation/report{BuildQuery(filter)}",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<InventoryRotationReportDto>(
                   cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException("La API de rotacion devolvio una respuesta vacia.");
    }

    public async Task<DeadStockSelectionDto> GetDeadStockProductIdsAsync(
        InventoryRotationFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        using var response = await _httpClient.GetAsync(
            $"api/inventory-rotation/dead-stock-ids{BuildQuery(filter)}",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<DeadStockSelectionDto>(
                   cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException("La API de stock muerto devolvio una respuesta vacia.");
    }

    private static string BuildQuery(InventoryRotationFilterDto filter)
    {
        var query = new List<string>
        {
            $"salesWindowDays={filter.SalesWindowDays.ToString(CultureInfo.InvariantCulture)}",
            $"deadStockDays={filter.DeadStockDays.ToString(CultureInfo.InvariantCulture)}",
            $"includeCategorySubtree={filter.IncludeCategorySubtree.ToString().ToLowerInvariant()}",
            $"sortBy={Uri.EscapeDataString(filter.SortBy ?? string.Empty)}",
            $"descending={filter.Descending.ToString().ToLowerInvariant()}",
            $"page={filter.Page.ToString(CultureInfo.InvariantCulture)}",
            $"pageSize={filter.PageSize.ToString(CultureInfo.InvariantCulture)}"
        };

        if (filter.CategoryId.HasValue)
        {
            query.Add($"categoryId={filter.CategoryId.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            query.Add($"searchText={Uri.EscapeDataString(filter.SearchText.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(filter.RotationClass))
        {
            query.Add($"rotationClass={Uri.EscapeDataString(filter.RotationClass)}");
        }

        return "?" + string.Join("&", query);
    }

    private static async Task<Exception> CreateErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new Exception(ApiErrorParser.FromBody(body, $"Error HTTP {(int)response.StatusCode}"));
    }
}
