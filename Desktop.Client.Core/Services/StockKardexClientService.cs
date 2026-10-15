using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;

namespace Desktop.Client.Services;

/// <summary>
/// 8.177-T3 (S2/S3): cliente HTTP de <c>/api/stock-movements</c>. Los instantes de fecha viajan
/// en UTC (ISO 8601, el ViewModel resuelve el rango local→UTC); los enums de clasificación viajan
/// por nombre ("Damage,Return") y el sentido como valor numérico, tal como los acepta el
/// <c>StockMovementsController</c> de T2.
/// </summary>
public class StockKardexClientService : IStockKardexClientService
{
    private readonly HttpClient _httpClient;

    public StockKardexClientService(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<PagedResultDto<StockKardexRowDto>> GetMovementsAsync(
        StockKardexFilterDto filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var url = $"api/stock-movements?{BuildQuery(filter)}&page={page}&pageSize={pageSize}";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<PagedResultDto<StockKardexRowDto>>(
                   cancellationToken: cancellationToken)
               ?? new PagedResultDto<StockKardexRowDto>();
    }

    public async Task<StockKardexSummaryDto> GetSummaryAsync(
        StockKardexFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var url = $"api/stock-movements/summary?{BuildQuery(filter)}";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<StockKardexSummaryDto>(
                   cancellationToken: cancellationToken)
               ?? new StockKardexSummaryDto();
    }

    public async Task<byte[]> ExportAsync(
        StockKardexFilterDto filter,
        string format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);

        var url = $"api/stock-movements/export?format={Uri.EscapeDataString(format.Trim())}&{BuildQuery(filter)}";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static string BuildQuery(StockKardexFilterDto filter)
    {
        var parts = new List<string>();

        if (filter.DateFromUtc.HasValue)
        {
            parts.Add($"dateFromUtc={Uri.EscapeDataString(FormatUtcInstant(filter.DateFromUtc.Value))}");
        }

        if (filter.DateToUtc.HasValue)
        {
            parts.Add($"dateToUtc={Uri.EscapeDataString(FormatUtcInstant(filter.DateToUtc.Value))}");
        }

        if (!string.IsNullOrWhiteSpace(filter.UserId))
        {
            parts.Add($"userId={Uri.EscapeDataString(filter.UserId.Trim())}");
        }

        if (filter.ProductId.HasValue)
        {
            parts.Add($"productId={filter.ProductId.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (filter.ReasonCodes is { Count: > 0 })
        {
            parts.Add($"reasonCodes={Uri.EscapeDataString(string.Join(",", filter.ReasonCodes))}");
        }

        if (filter.OriginTypes is { Count: > 0 })
        {
            parts.Add($"originTypes={Uri.EscapeDataString(string.Join(",", filter.OriginTypes))}");
        }

        parts.Add($"direction={((int)filter.Direction).ToString(CultureInfo.InvariantCulture)}");

        return string.Join("&", parts);
    }

    private static string FormatUtcInstant(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value.ToString("o", CultureInfo.InvariantCulture)
        : value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new Exception(ApiErrorParser.FromBody(error, $"Error HTTP {(int)response.StatusCode}"));
    }
}
