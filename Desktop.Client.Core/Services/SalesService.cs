using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Core.DTOs;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Messages;

namespace Desktop.Client.Services;

public class SalesService : ISalesService
{
    private readonly HttpClient _httpClient;
    private readonly object _saleLock = new object();
    private SaleDto? _currentSale;

    public SalesService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public SaleDto? CurrentSale
    {
        get
        {
            lock (_saleLock) return _currentSale;
        }
    }

    private void SetCurrentSale(SaleDto? sale)
    {
        lock (_saleLock)
        {
            _currentSale = sale;
        }
        WeakReferenceMessenger.Default.Send(new CurrentSaleChangedMessage(sale));
    }

    public async Task<SaleDto> GetSaleAsync(int saleId)
    {
        var response = await _httpClient.GetAsync($"api/sales/{saleId}");
        response.EnsureSuccessStatusCode();
        var sale = await response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception($"Sale #{saleId} not found.");
        if (CurrentSale?.Id == saleId)
        {
            SetCurrentSale(sale);
        }
        return sale;
    }

    public async Task<byte[]?> GetReceiptAsync(int saleId)
    {
        var response = await _httpClient.GetAsync($"api/sales/{saleId}/receipt");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    public async Task<SaleDto> StartSaleAsync(int? cashierId = null)
    {
        string url = cashierId.HasValue ? $"api/sales/start?cashierId={cashierId.Value}" : "api/sales/start";
        var _response = await _httpClient.PostAsync(url, null);
        _response.EnsureSuccessStatusCode();
        var sale = await _response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception("Failed to start sale.");
        SetCurrentSale(sale);
        return sale;
    }

    public async Task<SaleDto> AddItemAsync(int saleId, int productId, decimal quantity, decimal exchangeRate, decimal? customUnitPriceUSD = null, decimal? customUnitPriceBsS = null)
    {
        var _request = new { ProductId = productId, Quantity = quantity, ExchangeRate = exchangeRate, CustomUnitPriceUSD = customUnitPriceUSD, CustomUnitPriceBsS = customUnitPriceBsS };
        var _response = await _httpClient.PostAsJsonAsync($"api/sales/{saleId}/items", _request);
        if (!_response.IsSuccessStatusCode)
        {
            var err = await _response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
            var msg = err?["message"]?.ToString() ?? err?["Message"]?.ToString() ?? "Failed to add item.";
            throw new System.InvalidOperationException(msg);
        }
        var sale = await _response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception("Failed to add item.");
        SetCurrentSale(sale);
        return sale;
    }

    public async Task<SaleDto> RemoveItemAsync(int saleId, int itemId, decimal exchangeRate)
    {
        var _response = await _httpClient.DeleteAsync($"api/sales/{saleId}/items/{itemId}?exchangeRate={exchangeRate}");
        _response.EnsureSuccessStatusCode();
        var sale = await _response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception("Failed to remove item.");
        SetCurrentSale(sale);
        return sale;
    }

    public async Task<SaleDto> UpdateItemQuantityAsync(int saleId, int itemId, decimal quantity, decimal exchangeRate)
    {
        var _request = new { Quantity = quantity, ExchangeRate = exchangeRate };
        var _response = await _httpClient.PutAsJsonAsync($"api/sales/{saleId}/items/{itemId}", _request);
        if (!_response.IsSuccessStatusCode)
        {
            var err = await _response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
            var msg = err?["message"]?.ToString() ?? err?["Message"]?.ToString() ?? "Failed to update item quantity.";
            throw new System.InvalidOperationException(msg);
        }
        var sale = await _response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception("Failed to update item quantity.");
        SetCurrentSale(sale);
        return sale;
    }

    public async Task<SaleDto> UpdateExchangeRateAsync(int saleId, decimal exchangeRate)
    {
        var _response = await _httpClient.PutAsync($"api/sales/{saleId}/exchange-rate?exchangeRate={exchangeRate}", null);
        _response.EnsureSuccessStatusCode();
        var sale = await _response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception("Failed to update exchange rate.");
        SetCurrentSale(sale);
        return sale;
    }

    public async Task<SaleDto> UpdatePriceListAsync(int saleId, string priceListType)
    {
        var _request = new { PriceListType = priceListType };
        var _response = await _httpClient.PutAsJsonAsync($"api/sales/{saleId}/price-list", _request);
        if (!_response.IsSuccessStatusCode)
        {
            var err = await _response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
            var msg = err?["message"]?.ToString() ?? err?["Message"]?.ToString() ?? "Error al actualizar lista de precios.";
            throw new System.InvalidOperationException(msg);
        }
        var sale = await _response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception("Failed to update price list.");
        SetCurrentSale(sale);
        return sale;
    }

    public async Task<int> CompleteSaleAsync(int saleId, decimal exchangeRate, IEnumerable<SalePaymentDto> payments, decimal roundingAdjustment = 0, int? cashierId = null, bool isPendingPickup = false, string? idempotencyKey = null, System.Threading.CancellationToken cancellationToken = default)
    {
        var _request = new { ExchangeRate = exchangeRate, Payments = payments, RoundingAdjustment = roundingAdjustment, CashierId = cashierId, IsPendingPickup = isPendingPickup };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"api/sales/{saleId}/complete")
        {
            Content = JsonContent.Create(_request)
        };
        var effectiveKey = !string.IsNullOrWhiteSpace(idempotencyKey) ? idempotencyKey : Guid.NewGuid().ToString("N");
        httpRequest.Headers.Add("Idempotency-Key", effectiveKey);
        var _response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        if (!_response.IsSuccessStatusCode)
        {
            var errorContent = await _response.Content.ReadAsStringAsync();
            throw new System.Exception(ApiErrorParser.FromBody(errorContent, $"Error del servidor ({(int)_response.StatusCode})"));
        }
        var contentStr = await _response.Content.ReadAsStringAsync();
        var invoiceNumber = int.Parse(contentStr);
        WeakReferenceMessenger.Default.Send(new SaleCompletedNotificationMessage(invoiceNumber));
        return invoiceNumber;
    }

    public async Task<(IEnumerable<SaleHistoryDto> Items, int TotalCount)> GetSalesHistoryAsync(int page, int pageSize, System.DateTime? startDate = null, System.DateTime? endDate = null, string? search = null, System.Threading.CancellationToken cancellationToken = default)
    {
        var _url = $"api/sales/history?page={page}&pageSize={pageSize}";
        if (startDate.HasValue) _url += $"&startDate={startDate.Value:yyyy-MM-dd}";
        if (endDate.HasValue) _url += $"&endDate={endDate.Value:yyyy-MM-dd}";
        if (!string.IsNullOrWhiteSpace(search)) _url += $"&search={System.Uri.EscapeDataString(search.Trim())}";

        var _response = await _httpClient.GetAsync(_url, cancellationToken);
        _response.EnsureSuccessStatusCode();

        var _result = await _response.Content.ReadFromJsonAsync<SalesHistoryResponse>(cancellationToken: cancellationToken);
        return (_result?.Items ?? new List<SaleHistoryDto>(), _result?.TotalCount ?? 0);
    }

    public async Task<SaleHistoryDto> GetSaleHistoryDetailAsync(int saleId, System.Threading.CancellationToken cancellationToken = default)
    {
        var _response = await _httpClient.GetAsync($"api/sales/{saleId}/history-detail", cancellationToken);
        _response.EnsureSuccessStatusCode();

        return await _response.Content.ReadFromJsonAsync<SaleHistoryDto>(cancellationToken: cancellationToken)
            ?? throw new System.Exception("Failed to load sale history detail.");
    }

    public async Task<SaleDto> HoldSaleAsync(int saleId, HoldSaleRequestDto request)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"api/sales/{saleId}/hold")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await _httpClient.SendAsync(httpRequest);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(ApiErrorParser.FromBody(err, "Error del servidor"));
        }
        var sale = await response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception("Failed to hold sale.");
        SetCurrentSale(sale);
        return sale;
    }

    public async Task<SaleDto> AddPaymentToHoldSaleAsync(int saleId, AddPaymentRequestDto request)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"api/sales/{saleId}/payments")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await _httpClient.SendAsync(httpRequest);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(ApiErrorParser.FromBody(err, "Error del servidor"));
        }
        var sale = await response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception("Failed to add payment.");
        SetCurrentSale(sale);
        return sale;
    }

    public async Task<IEnumerable<SaleDto>> GetPendingSalesAsync()
    {
        return await _httpClient.GetFromJsonAsync<IEnumerable<SaleDto>>("api/sales/pending") ?? new List<SaleDto>();
    }

    public async Task<(IEnumerable<CustomerDto> Items, int TotalCount)> GetCustomersAsync(
        string? query = null,
        int page = 1,
        int pageSize = 20,
        bool recentOnly = false)
    {
        var url = $"api/sales/customers?page={page}&pageSize={pageSize}&recentOnly={recentOnly}";
        if (!string.IsNullOrWhiteSpace(query))
        {
            url += $"&query={Uri.EscapeDataString(query)}";
        }

        try
        {
            var pagedResult = await _httpClient.GetFromJsonAsync<CustomerPagedResultDto>(url);
            if (pagedResult != null && pagedResult.Items != null)
            {
                return (pagedResult.Items, pagedResult.TotalCount);
            }
        }
        catch
        {
            try
            {
                var list = await _httpClient.GetFromJsonAsync<List<CustomerDto>>(url);
                if (list != null) return (list, list.Count);
            }
            catch { }
        }

        return (new List<CustomerDto>(), 0);
    }



    public async Task<CustomerDto> CreateCustomerAsync(CreateCustomerDto request)
    {
        var response = await _httpClient.PostAsJsonAsync("api/sales/customers", request);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(ApiErrorParser.FromBody(err, "Error del servidor"));
        }
        return await response.Content.ReadFromJsonAsync<CustomerDto>() ?? throw new System.Exception("Failed to create customer.");
    }

    public async Task<CustomerDto> UpdateCustomerAsync(int id, UpdateCustomerDto request)
    {
        var response = await _httpClient.PutAsJsonAsync($"api/sales/customers/{id}", request);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(ApiErrorParser.FromBody(err, "Error del servidor"));
        }
        return await response.Content.ReadFromJsonAsync<CustomerDto>() ?? throw new System.Exception("Failed to update customer.");
    }

    public async Task DeleteCustomerAsync(int id)
    {
        var response = await _httpClient.DeleteAsync($"api/sales/customers/{id}");
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(ApiErrorParser.FromBody(err, "Error del servidor"));
        }
    }

    public async Task<CustomerDto> GetDefaultCustomerAsync()
    {
        return await _httpClient.GetFromJsonAsync<CustomerDto>("api/sales/customers/default") 
               ?? throw new System.Exception("Failed to load default customer.");
    }

    public async Task<SaleDto> UpdateSaleCustomerAsync(int saleId, int customerId)
    {
        var response = await _httpClient.PutAsJsonAsync($"api/sales/{saleId}/customer", new { CustomerId = customerId });
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(ApiErrorParser.FromBody(err, "Error del servidor"));
        }
        var sale = await response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception("Failed to update sale customer.");
        SetCurrentSale(sale);
        return sale;
    }

    public async Task<IEnumerable<PendingPickupClientDto>> GetPendingPickupsAsync()
    {
        return await _httpClient.GetFromJsonAsync<IEnumerable<PendingPickupClientDto>>("api/sales/pending-pickups")
               ?? new List<PendingPickupClientDto>();
    }

    public async Task ConfirmPickupAsync(int saleId)
    {
        var response = await _httpClient.PostAsync($"api/sales/{saleId}/confirm-pickup", null);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(ApiErrorParser.FromBody(err, "Error del servidor"));
        }
    }

    public async Task UpdateSaleItemsAsync(int saleId, IEnumerable<UpdateSaleItemDto> items, decimal exchangeRate)
    {
        var body = new { ExchangeRate = exchangeRate, Items = items };
        var response = await _httpClient.PutAsJsonAsync($"api/sales/{saleId}/items", body);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(ApiErrorParser.FromBody(err, "Error del servidor"));
        }
    }
}



public class SalesHistoryResponse
{
    public IEnumerable<SaleHistoryDto> Items { get; set; } = new List<SaleHistoryDto>();
    public int TotalCount { get; set; }
}
