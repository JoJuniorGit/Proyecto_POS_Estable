using Core.DTOs;
using Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

    // 8.147-T6/D12: sube la imagen/PDF al endpoint de extracción OCR. El mapping viaja como
    // campos planos y solo cuando hay plantilla del proveedor; sin él el parser usa keywords
    // genéricas. El archivo va como stream con nombre para conservar la extensión.
    public async Task<OcrExtractionResultDto> ExtractOcrAsync(
        string filePath,
        int? supplierId,
        SupplierColumnMappingDto? columnMapping,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var fileStream = File.OpenRead(filePath);
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(ResolveOcrContentType(filePath));
        content.Add(fileContent, "file", Path.GetFileName(filePath));

        if (supplierId is not null)
        {
            content.Add(
                new StringContent(supplierId.Value.ToString(CultureInfo.InvariantCulture)),
                "supplierId");
        }

        if (columnMapping is not null)
        {
            content.Add(new StringContent(columnMapping.NameColumnName), "nameColumn");
            content.Add(new StringContent(columnMapping.QuantityColumnName), "quantityColumn");
            content.Add(new StringContent(columnMapping.UnitCostColumnName), "unitCostColumn");
            if (!string.IsNullOrWhiteSpace(columnMapping.SupplierCodeColumnName))
            {
                content.Add(new StringContent(columnMapping.SupplierCodeColumnName), "supplierCodeColumn");
            }

            if (!string.IsNullOrWhiteSpace(columnMapping.BarcodeColumnName))
            {
                content.Add(new StringContent(columnMapping.BarcodeColumnName), "barcodeColumn");
            }
        }

        using var response = await _httpClient.PostAsync(
            "api/supplier-invoices/ocr-extract",
            content,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadRequiredResponseAsync<OcrExtractionResultDto>(response, cancellationToken);
    }

    private static string ResolveOcrContentType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };

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
