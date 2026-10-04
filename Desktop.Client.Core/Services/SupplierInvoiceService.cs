using Core.DTOs;
using Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public partial class SupplierInvoiceService : ISupplierInvoiceService
{
    private readonly HttpClient _httpClient;

    public SupplierInvoiceService(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<SupplierSummaryDto>> GetSuppliersAsync(
        string? rifOrNit,
        string? commercialName,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(rifOrNit))
        {
            query.Add($"rif={Uri.EscapeDataString(rifOrNit.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(commercialName))
        {
            query.Add($"commercialName={Uri.EscapeDataString(commercialName.Trim())}");
        }

        if (query.Count == 0)
        {
            throw new ArgumentException("A supplier RIF/NIT or commercial name is required.");
        }

        using var response = await _httpClient.GetAsync(
            $"api/suppliers?{string.Join("&", query)}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<SupplierSummaryDto>>(
                   cancellationToken: cancellationToken)
               ?? [];
    }

    public async Task<SupplierSummaryDto> CreateSupplierAsync(
        CreateSupplierRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await _httpClient.PostAsJsonAsync(
            "api/suppliers",
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadRequiredResponseAsync<SupplierSummaryDto>(response, cancellationToken);
    }

    public async Task<SupplierInvoiceDetailDto> StageAsync(
        StageSupplierInvoiceRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await _httpClient.PostAsJsonAsync(
            "api/supplier-invoices",
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadRequiredResponseAsync<SupplierInvoiceDetailDto>(response, cancellationToken);
    }

    public async Task<SupplierInvoiceDetailDto?> GetInvoiceAsync(
        int invoiceId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/supplier-invoices/{invoiceId}",
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await ReadRequiredResponseAsync<SupplierInvoiceDetailDto>(response, cancellationToken);
    }

    public async Task<SupplierInvoiceDetailDto> ConfirmAsync(
        int invoiceId,
        ConfirmSupplierInvoiceRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await _httpClient.PostAsJsonAsync(
            $"api/supplier-invoices/{invoiceId}/confirm",
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadRequiredResponseAsync<SupplierInvoiceDetailDto>(response, cancellationToken);
    }

    public async Task<SupplierInvoiceDetailDto> CreateProductFromLineAsync(
        int invoiceId,
        int lineId,
        CreateInvoiceProductRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await _httpClient.PostAsJsonAsync(
            $"api/supplier-invoices/{invoiceId}/lines/{lineId}/create-product",
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadRequiredResponseAsync<SupplierInvoiceDetailDto>(response, cancellationToken);
    }

    private static async Task<TResponse> ReadRequiredResponseAsync<TResponse>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        where TResponse : class =>
        await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken)
        ?? throw new InvalidOperationException("The supplier invoice API returned an empty response.");
}
