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
    private readonly HttpClient _http_client;
    private readonly object _saleLock = new object();
    private SaleDto? _currentSale;

    public SalesService(HttpClient http_client)
    {
        _http_client = http_client;
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
        var response = await _http_client.GetAsync($"api/sales/{saleId}");
        response.EnsureSuccessStatusCode();
        var sale = await response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception($"Sale #{saleId} not found.");
        if (CurrentSale?.Id == saleId)
        {
            SetCurrentSale(sale);
        }
        return sale;
    }

    public async Task<SaleDto> StartSaleAsync(int? cashierId = null)
    {
        string url = cashierId.HasValue ? $"api/sales/start?cashierId={cashierId.Value}" : "api/sales/start";
        var _response = await _http_client.PostAsync(url, null);
        _response.EnsureSuccessStatusCode();
        var sale = await _response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception("Failed to start sale.");
        SetCurrentSale(sale);
        return sale;
    }

    public async Task<SaleDto> AddItemAsync(int sale_id, int product_id, decimal quantity, decimal exchange_rate, decimal? custom_unit_price_usd = null, decimal? custom_unit_price_bs_s = null)
    {
        var _request = new { ProductId = product_id, Quantity = quantity, ExchangeRate = exchange_rate, CustomUnitPriceUSD = custom_unit_price_usd, CustomUnitPriceBsS = custom_unit_price_bs_s };
        var _response = await _http_client.PostAsJsonAsync($"api/sales/{sale_id}/items", _request);
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

    public async Task<SaleDto> RemoveItemAsync(int sale_id, int item_id, decimal exchange_rate)
    {
        var _response = await _http_client.DeleteAsync($"api/sales/{sale_id}/items/{item_id}?exchangeRate={exchange_rate}");
        _response.EnsureSuccessStatusCode();
        var sale = await _response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception("Failed to remove item.");
        SetCurrentSale(sale);
        return sale;
    }

    public async Task<SaleDto> UpdateItemQuantityAsync(int sale_id, int item_id, decimal quantity, decimal exchange_rate)
    {
        var _request = new { Quantity = quantity, ExchangeRate = exchange_rate };
        var _response = await _http_client.PutAsJsonAsync($"api/sales/{sale_id}/items/{item_id}", _request);
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

    public async Task<SaleDto> UpdateExchangeRateAsync(int sale_id, decimal exchange_rate)
    {
        var _response = await _http_client.PutAsync($"api/sales/{sale_id}/exchange-rate?exchangeRate={exchange_rate}", null);
        _response.EnsureSuccessStatusCode();
        var sale = await _response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception("Failed to update exchange rate.");
        SetCurrentSale(sale);
        return sale;
    }

    public async Task<SaleDto> UpdatePriceListAsync(int saleId, string priceListType)
    {
        var _request = new { PriceListType = priceListType };
        var _response = await _http_client.PutAsJsonAsync($"api/sales/{saleId}/price-list", _request);
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

    public async Task<int> CompleteSaleAsync(int sale_id, decimal exchange_rate, IEnumerable<SalePaymentDto> payments, decimal rounding_adjustment = 0, int? cashierId = null, bool isPendingPickup = false)
    {
        var _request = new { ExchangeRate = exchange_rate, Payments = payments, RoundingAdjustment = rounding_adjustment, CashierId = cashierId, IsPendingPickup = isPendingPickup };
        var _response = await _http_client.PostAsJsonAsync($"api/sales/{sale_id}/complete", _request);
        _response.EnsureSuccessStatusCode();
        var contentStr = await _response.Content.ReadAsStringAsync();
        return int.Parse(contentStr);
    }

    public async Task<(IEnumerable<SaleHistoryDto> Items, int TotalCount)> GetSalesHistoryAsync(int page, int page_size, System.DateTime? start_date = null, System.DateTime? end_date = null, string? search = null, System.Threading.CancellationToken cancellation_token = default)
    {
        var _url = $"api/sales/history?page={page}&pageSize={page_size}";
        if (start_date.HasValue) _url += $"&startDate={start_date.Value:O}";
        if (end_date.HasValue) _url += $"&endDate={end_date.Value:O}";
        if (!string.IsNullOrWhiteSpace(search)) _url += $"&search={System.Uri.EscapeDataString(search.Trim())}";

        var _response = await _http_client.GetAsync(_url, cancellation_token);
        _response.EnsureSuccessStatusCode();

        var _result = await _response.Content.ReadFromJsonAsync<SalesHistoryResponse>(cancellationToken: cancellation_token);
        return (_result?.Items ?? new List<SaleHistoryDto>(), _result?.TotalCount ?? 0);
    }

    public async Task<SaleHistoryDto> GetSaleHistoryDetailAsync(int sale_id, System.Threading.CancellationToken cancellation_token = default)
    {
        var _response = await _http_client.GetAsync($"api/sales/{sale_id}/history-detail", cancellation_token);
        _response.EnsureSuccessStatusCode();

        return await _response.Content.ReadFromJsonAsync<SaleHistoryDto>(cancellationToken: cancellation_token)
            ?? throw new System.Exception("Failed to load sale history detail.");
    }

    public async Task<SaleDto> HoldSaleAsync(int saleId, HoldSaleRequestDto request)
    {
        var response = await _http_client.PostAsJsonAsync($"api/sales/{saleId}/hold", request);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(err);
        }
        var sale = await response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception("Failed to hold sale.");
        SetCurrentSale(sale);
        return sale;
    }

    public async Task<SaleDto> AddPaymentToHoldSaleAsync(int saleId, AddPaymentRequestDto request)
    {
        var response = await _http_client.PostAsJsonAsync($"api/sales/{saleId}/payments", request);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(err);
        }
        var sale = await response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception("Failed to add payment.");
        SetCurrentSale(sale);
        return sale;
    }

    public async Task<IEnumerable<SaleDto>> GetPendingSalesAsync()
    {
        return await _http_client.GetFromJsonAsync<IEnumerable<SaleDto>>("api/sales/pending") ?? new List<SaleDto>();
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
            var pagedResult = await _http_client.GetFromJsonAsync<CustomerPagedResultDto>(url);
            if (pagedResult != null && pagedResult.Items != null)
            {
                return (pagedResult.Items, pagedResult.TotalCount);
            }
        }
        catch
        {
            try
            {
                var list = await _http_client.GetFromJsonAsync<List<CustomerDto>>(url);
                if (list != null) return (list, list.Count);
            }
            catch { }
        }

        return (new List<CustomerDto>(), 0);
    }



    public async Task<CustomerDto> CreateCustomerAsync(CreateCustomerDto request)
    {
        var response = await _http_client.PostAsJsonAsync("api/sales/customers", request);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(err);
        }
        return await response.Content.ReadFromJsonAsync<CustomerDto>() ?? throw new System.Exception("Failed to create customer.");
    }

    public async Task<CustomerDto> UpdateCustomerAsync(int id, UpdateCustomerDto request)
    {
        var response = await _http_client.PutAsJsonAsync($"api/sales/customers/{id}", request);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(err);
        }
        return await response.Content.ReadFromJsonAsync<CustomerDto>() ?? throw new System.Exception("Failed to update customer.");
    }

    public async Task DeleteCustomerAsync(int id)
    {
        var response = await _http_client.DeleteAsync($"api/sales/customers/{id}");
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(err);
        }
    }

    public async Task<CustomerDto> GetDefaultCustomerAsync()
    {
        return await _http_client.GetFromJsonAsync<CustomerDto>("api/sales/customers/default") 
               ?? throw new System.Exception("Failed to load default customer.");
    }

    public async Task<SaleDto> UpdateSaleCustomerAsync(int saleId, int customerId)
    {
        var response = await _http_client.PutAsJsonAsync($"api/sales/{saleId}/customer", new { CustomerId = customerId });
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(err);
        }
        var sale = await response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception("Failed to update sale customer.");
        SetCurrentSale(sale);
        return sale;
    }

    public async Task<IEnumerable<PendingPickupClientDto>> GetPendingPickupsAsync()
    {
        return await _http_client.GetFromJsonAsync<IEnumerable<PendingPickupClientDto>>("api/sales/pending-pickups")
               ?? new List<PendingPickupClientDto>();
    }

    public async Task ConfirmPickupAsync(int saleId)
    {
        var response = await _http_client.PostAsync($"api/sales/{saleId}/confirm-pickup", null);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(err);
        }
    }

    public async Task UpdateSaleItemsAsync(int saleId, IEnumerable<UpdateSaleItemDto> items, decimal exchangeRate)
    {
        var body = new { ExchangeRate = exchangeRate, Items = items };
        var response = await _http_client.PutAsJsonAsync($"api/sales/{saleId}/items", body);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new System.Exception(err);
        }
    }
}



public class SalesHistoryResponse
{
    public IEnumerable<SaleHistoryDto> Items { get; set; } = new List<SaleHistoryDto>();
    public int TotalCount { get; set; }
}
