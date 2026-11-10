using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

/// <summary>
/// F4-WU1 (Paso 14): cliente tipado del Estado de Resultados. Construye los filtros del contrato
/// F3 (preset/fechas/categorías/productos/métodos/cajero/tipo/lista/agrupación) y conserva el
/// pipeline HTTP de sesión, pinning y resiliencia inyectado por Desktop.Client.
/// </summary>
public sealed class ProfitabilityClientService : IProfitabilityClientService
{
    private readonly HttpClient _httpClient;

    public ProfitabilityClientService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ProfitabilitySummaryClientDto> GetSummaryAsync(
        ProfitabilityClientFilter filter,
        CancellationToken cancellationToken = default)
    {
        var query = BuildFilterQuery(filter);
        using var response = await _httpClient.GetAsync(
            $"api/profitability/summary?{query}",
            cancellationToken);
        await EnsureSuccessAsync(response, "No se pudo cargar el resumen de rentabilidad.", cancellationToken);
        return await response.Content.ReadFromJsonAsync<ProfitabilitySummaryClientDto>(
                   cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException("El servidor devolvió un resumen de rentabilidad vacío.");
    }

    public async Task<ProfitabilityBreakdownClientDto> GetBreakdownAsync(
        ProfitabilityClientFilter filter,
        CancellationToken cancellationToken = default)
    {
        var query = BuildFilterQuery(filter);
        using var response = await _httpClient.GetAsync(
            $"api/profitability/breakdown?{query}",
            cancellationToken);
        await EnsureSuccessAsync(response, "No se pudo cargar el desglose de rentabilidad.", cancellationToken);
        return await response.Content.ReadFromJsonAsync<ProfitabilityBreakdownClientDto>(
                   cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException("El servidor devolvió un desglose de rentabilidad vacío.");
    }

    public async Task<ProfitabilityClientExport> ExportAsync(
        ProfitabilityClientFilter filter,
        string format,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        var normalizedFormat = format.Trim().ToLowerInvariant();
        var query = BuildFilterQuery(filter, normalizedFormat);
        using var response = await _httpClient.GetAsync(
            $"api/profitability/export?{query}",
            cancellationToken);
        await EnsureSuccessAsync(response, "No se pudo exportar el Estado de Resultados.", cancellationToken);

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var fileName = ResolveFileName(response, normalizedFormat);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return new ProfitabilityClientExport(content, fileName, contentType);
    }

    public async Task<IReadOnlyList<PaymentMethodFeeRuleClientDto>> GetFeeRulesAsync(
        int? paymentMethodId,
        bool? activeOnly,
        CancellationToken cancellationToken = default)
    {
        var parts = new List<string>();
        if (paymentMethodId.HasValue)
        {
            parts.Add($"paymentMethodId={paymentMethodId.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (activeOnly.HasValue)
        {
            parts.Add($"activeOnly={(activeOnly.Value ? "true" : "false")}");
        }

        var query = parts.Count == 0 ? string.Empty : $"?{string.Join("&", parts)}";
        using var response = await _httpClient.GetAsync(
            $"api/payment-method-fees{query}",
            cancellationToken);
        await EnsureSuccessAsync(response, "No se pudieron cargar las reglas de comisión.", cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<PaymentMethodFeeRuleClientDto>>(
                   cancellationToken: cancellationToken)
               ?? new List<PaymentMethodFeeRuleClientDto>();
    }

    public async Task<PaymentMethodFeeRuleClientDto> CreateFeeRuleAsync(
        CreatePaymentMethodFeeRuleClientRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await _httpClient.PostAsJsonAsync(
            "api/payment-method-fees",
            request,
            cancellationToken);
        await EnsureSuccessAsync(response, "No se pudo crear la regla de comisión.", cancellationToken);
        return await response.Content.ReadFromJsonAsync<PaymentMethodFeeRuleClientDto>(
                   cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException("El servidor no devolvió la regla de comisión creada.");
    }

    public async Task<PaymentMethodFeeRuleClientDto> UpdateFeeRuleAsync(
        int id,
        UpdatePaymentMethodFeeRuleClientRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await _httpClient.PutAsJsonAsync(
            $"api/payment-method-fees/{id}",
            request,
            cancellationToken);
        await EnsureSuccessAsync(response, "No se pudo actualizar la regla de comisión.", cancellationToken);
        return await response.Content.ReadFromJsonAsync<PaymentMethodFeeRuleClientDto>(
                   cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException("El servidor no devolvió la regla de comisión actualizada.");
    }

    public async Task DeleteFeeRuleAsync(int id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync(
            $"api/payment-method-fees/{id}",
            cancellationToken);
        await EnsureSuccessAsync(response, "No se pudo desactivar la regla de comisión.", cancellationToken);
    }

    internal static string BuildFilterQuery(
        ProfitabilityClientFilter filter,
        string? format = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var parts = new List<string>
        {
            $"preset={(int)filter.Preset}"
        };

        if (filter.FromDate.HasValue)
        {
            parts.Add($"fromDate={filter.FromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        }

        if (filter.ToDate.HasValue)
        {
            parts.Add($"toDate={filter.ToDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        }

        AppendInts(parts, "categoryIds", filter.CategoryIds);
        parts.Add($"includeCategorySubtree={(filter.IncludeCategorySubtree ? "true" : "false")}");
        AppendInts(parts, "productIds", filter.ProductIds);
        AppendInts(parts, "paymentMethodIds", filter.PaymentMethodIds);

        if (filter.CashierId.HasValue)
        {
            parts.Add($"cashierId={filter.CashierId.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (filter.SaleType.HasValue)
        {
            parts.Add($"saleType={(int)filter.SaleType.Value}");
        }

        if (!string.IsNullOrWhiteSpace(filter.PriceListType))
        {
            parts.Add($"priceListType={Uri.EscapeDataString(filter.PriceListType.Trim())}");
        }

        parts.Add($"groupBy={(int)filter.GroupBy}");

        if (!string.IsNullOrWhiteSpace(format))
        {
            parts.Add($"format={Uri.EscapeDataString(format)}");
        }

        return string.Join("&", parts);
    }

    private static void AppendInts(
        ICollection<string> parts,
        string name,
        IReadOnlyList<int>? values)
    {
        if (values is not { Count: > 0 })
        {
            return;
        }

        foreach (var value in values)
        {
            parts.Add($"{name}={value.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static string ResolveFileName(HttpResponseMessage response, string format)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var fileName = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            return fileName;
        }

        return $"Estado_Resultados_{DateTime.Now:yyyyMMdd}.{format}";
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string fallback,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            ApiErrorParser.FromBody(body, $"{fallback} (HTTP {(int)response.StatusCode})."));
    }
}
