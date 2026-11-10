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

    public async Task<SaleDto> AddItemAsync(int saleId, int productId, decimal quantity, decimal exchangeRate, decimal? customUnitPriceUSD = null, decimal? customUnitPriceBsS = null, string? authorizationToken = null)
    {
        var _request = new { ProductId = productId, Quantity = quantity, ExchangeRate = exchangeRate, CustomUnitPriceUSD = customUnitPriceUSD, CustomUnitPriceBsS = customUnitPriceBsS };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"api/sales/{saleId}/items")
        {
            Content = JsonContent.Create(_request)
        };
        // 8.149 (SRE-02): el backend exige Idempotency-Key; el retry del ResilienceHandler reenvia el
        // MISMO request, por lo que la clave se mantiene estable dentro del intento logico.
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        // 8.150 (T9): token efimero de autorizacion remota/local para el gate ManualPriceOverride.
        if (!string.IsNullOrWhiteSpace(authorizationToken))
        {
            httpRequest.Headers.Add("X-Authorization-Token", authorizationToken);
        }
        var _response = await _httpClient.SendAsync(httpRequest);
        if (!_response.IsSuccessStatusCode)
        {
            var errorBody = await _response.Content.ReadAsStringAsync();
            // 8.150 (T10, design D5/D7): el 403 del gate ManualPriceOverride llega como
            // ProblemDetails con extensiones; se traduce a una excepcion dedicada para que el POS
            // arranque el flujo de espera sin inspeccionar strings. El resto de los errores
            // conserva el contrato de mensaje historico (message/Message/fallback) y tolera
            // bodies no JSON sin romper el camino de error.
            if (_response.StatusCode == System.Net.HttpStatusCode.Forbidden &&
                ApiErrorParser.TryGetAuthorizationRequirement(errorBody, out var authorizationAction))
            {
                throw new AuthorizationRequiredException(
                    ApiErrorParser.FromBody(errorBody, "Failed to add item."),
                    authorizationAction);
            }

            System.Text.Json.Nodes.JsonObject? err = null;
            try
            {
                err = System.Text.Json.Nodes.JsonNode.Parse(errorBody) as System.Text.Json.Nodes.JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                // Body no JSON: se aplica el mismo fallback del contrato de error.
            }

            var msg = err?["message"]?.ToString() ?? err?["Message"]?.ToString() ?? "Failed to add item.";
            throw new System.InvalidOperationException(msg);
        }
        var sale = await _response.Content.ReadFromJsonAsync<SaleDto>() ?? throw new System.Exception("Failed to add item.");
        SetCurrentSale(sale);
        return sale;
    }

    public async Task<SaleDto> RemoveItemAsync(int saleId, int itemId, decimal exchangeRate)
    {
        var _response = await _httpClient.DeleteAsync($"api/sales/{saleId}/items/{itemId}?exchangeRate={exchangeRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
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
        var _response = await _httpClient.PutAsync($"api/sales/{saleId}/exchange-rate?exchangeRate={exchangeRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}", null);
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

    public async Task<CheckoutPreviewClientDto> GetCheckoutPreviewAsync(int saleId, decimal exchangeRate, IEnumerable<SalePaymentDto> payments)
    {
        // 8.156 (CLEAN-05): mismo body del web (exchangeRate + payments con amountLocal);
        // la respuesta es la fuente canónica del redondeo fiscal y del estado de pago total.
        var _request = new { ExchangeRate = exchangeRate, Payments = payments };
        var _response = await _httpClient.PostAsJsonAsync($"api/sales/{saleId}/checkout-preview", _request);
        _response.EnsureSuccessStatusCode();
        return await _response.Content.ReadFromJsonAsync<CheckoutPreviewClientDto>()
            ?? throw new System.Exception("Failed to load checkout preview.");
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

    public async Task<SaleDto> ClaimSaleAsync(int saleId, string action, System.Threading.CancellationToken cancellationToken = default)
    {
        var _request = new { Action = action };
        var _response = await _httpClient.PostAsJsonAsync($"api/sales/{saleId}/claim", _request, cancellationToken);
        if (!_response.IsSuccessStatusCode)
        {
            var errorContent = await _response.Content.ReadAsStringAsync(cancellationToken);
            throw new System.Exception(ApiErrorParser.FromBody(errorContent, $"Error del servidor ({(int)_response.StatusCode})"));
        }
        return await _response.Content.ReadFromJsonAsync<SaleDto>(cancellationToken: cancellationToken) ?? throw new System.Exception("Failed to claim sale.");
    }

    public async Task<SaleDto> ReleaseSaleAsync(int saleId, bool force = false, System.Threading.CancellationToken cancellationToken = default)
    {
        var _response = await _httpClient.PostAsync($"api/sales/{saleId}/release?force={force.ToString().ToLowerInvariant()}", null, cancellationToken);
        if (!_response.IsSuccessStatusCode)
        {
            var errorContent = await _response.Content.ReadAsStringAsync(cancellationToken);
            throw new System.Exception(ApiErrorParser.FromBody(errorContent, $"Error del servidor ({(int)_response.StatusCode})"));
        }
        return await _response.Content.ReadFromJsonAsync<SaleDto>(cancellationToken: cancellationToken) ?? throw new System.Exception("Failed to release sale.");
    }

    public async Task<(IEnumerable<SaleDto> Items, int TotalCount)> GetPendingSalesPagedAsync(int limit = 200, int offset = 0)
    {
        var response = await _httpClient.GetAsync($"api/sales/pending?limit={limit}&offset={offset}");
        response.EnsureSuccessStatusCode();
        var items = await response.Content.ReadFromJsonAsync<IEnumerable<SaleDto>>() ?? new List<SaleDto>();
        int totalCount = 0;
        if (response.Headers.TryGetValues("X-Total-Count", out var totalValues))
        {
            int.TryParse(totalValues.FirstOrDefault(), out totalCount);
        }
        return (items, totalCount);
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
        catch (Exception ex)
        {
            Core.Logging.ClientStateLogger.LogError($"Paged customer query fallback triggered: {ex.Message}", nameof(SalesService));
            try
            {
                var list = await _httpClient.GetFromJsonAsync<List<CustomerDto>>(url);
                if (list != null) return (list, list.Count);
            }
            catch (Exception innerEx)
            {
                Core.Logging.ClientStateLogger.LogError($"Customer query fallback failed: {innerEx.Message}", nameof(SalesService));
            }
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

    public async Task<(IEnumerable<PendingPickupClientDto> Items, int TotalCount)> GetPendingPickupsPagedAsync(int limit = 200, int offset = 0)
    {
        var response = await _httpClient.GetAsync($"api/sales/pending-pickups?limit={limit}&offset={offset}");
        response.EnsureSuccessStatusCode();
        var items = await response.Content.ReadFromJsonAsync<IEnumerable<PendingPickupClientDto>>() ?? new List<PendingPickupClientDto>();
        int totalCount = 0;
        if (response.Headers.TryGetValues("X-Total-Count", out var totalValues))
        {
            int.TryParse(totalValues.FirstOrDefault(), out totalCount);
        }
        return (items, totalCount);
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

    public async Task<DeliveryReceiptClientDto> DeliverPartialAsync(
        int saleId,
        IReadOnlyList<PartialDeliveryItemRequestDto> items,
        string? notes,
        string idempotencyKey,
        System.Threading.CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/sales/{saleId}/deliveries")
        {
            Content = JsonContent.Create(new { Items = items, Notes = notes })
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new System.Exception(ApiErrorParser.FromBody(
                errorContent,
                $"Error del servidor ({(int)response.StatusCode})"));
        }

        return await response.Content.ReadFromJsonAsync<DeliveryReceiptClientDto>(cancellationToken: cancellationToken)
            ?? throw new System.Exception("No se pudo leer el comprobante de despacho.");
    }

    public async Task<byte[]> GetDeliveryNoteAsync(
        int saleId,
        int deliveryId,
        System.Threading.CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/sales/{saleId}/deliveries/{deliveryId}/receipt",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new System.Exception(ApiErrorParser.FromBody(
                errorContent,
                $"Error del servidor ({(int)response.StatusCode})"));
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
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

    // ── Paso 11/12 (F3): crédito CxC ──

    public async Task<CreditProfileClientDto> GetCreditProfileAsync(
        int customerId,
        System.Threading.CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/sales/customers/{customerId}/credit-profile",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowCreditErrorAsync(response, "No se pudo cargar el perfil crediticio del cliente.");
        }

        return await response.Content.ReadFromJsonAsync<CreditProfileClientDto>(cancellationToken: cancellationToken)
            ?? throw new System.Exception("No se pudo leer el perfil crediticio del cliente.");
    }

    public async Task<CreditProfileClientDto> UpdateCreditProfileAsync(
        int customerId,
        CreditProfileUpdateClientDto request,
        System.Threading.CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync(
            $"api/sales/customers/{customerId}/credit-profile",
            request,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowCreditErrorAsync(response, "No se pudo actualizar el perfil crediticio del cliente.");
        }

        return await response.Content.ReadFromJsonAsync<CreditProfileClientDto>(cancellationToken: cancellationToken)
            ?? throw new System.Exception("No se pudo leer el perfil crediticio actualizado.");
    }

    public async Task<CreditStatementClientDto> GetCreditStatementAsync(
        int customerId,
        System.Threading.CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/sales/customers/{customerId}/credit-statement",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowCreditErrorAsync(response, "No se pudo cargar el estado de cuenta del cliente.");
        }

        return await response.Content.ReadFromJsonAsync<CreditStatementClientDto>(cancellationToken: cancellationToken)
            ?? throw new System.Exception("No se pudo leer el estado de cuenta del cliente.");
    }

    public async Task<CreditReceivablesClientDto> GetCreditReceivablesAsync(
        CreditReceivablesFilterClientDto filter,
        System.Threading.CancellationToken cancellationToken = default)
    {
        filter ??= new CreditReceivablesFilterClientDto();
        var query = new System.Text.StringBuilder("api/sales/credit-receivables");
        var separator = '?';

        void Append(string name, string value)
        {
            query.Append(separator).Append(name).Append('=').Append(value);
            separator = '&';
        }

        if (filter.CustomerId.HasValue)
        {
            Append("customerId", filter.CustomerId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            Append("status", Uri.EscapeDataString(filter.Status.Trim()));
        }

        if (filter.OverdueOnly)
        {
            Append("overdueOnly", "true");
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            Append("search", Uri.EscapeDataString(filter.Search.Trim()));
        }

        using var response = await _httpClient.GetAsync(query.ToString(), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowCreditErrorAsync(response, "No se pudo cargar la cartera de cuentas por cobrar.");
        }

        return await response.Content.ReadFromJsonAsync<CreditReceivablesClientDto>(cancellationToken: cancellationToken)
            ?? throw new System.Exception("No se pudo leer la cartera de cuentas por cobrar.");
    }

    public async Task<int> CompleteCreditSaleAsync(
        int saleId,
        decimal exchangeRate,
        IEnumerable<SalePaymentDto> payments,
        int? cashierId = null,
        string? authorizationToken = null,
        string? idempotencyKey = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var effectiveKey = !string.IsNullOrWhiteSpace(idempotencyKey) ? idempotencyKey : Guid.NewGuid().ToString("N");
        var request = new { ExchangeRate = exchangeRate, Payments = payments, CashierId = cashierId, IdempotencyKey = effectiveKey };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"api/sales/{saleId}/complete-credit")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("Idempotency-Key", effectiveKey);
        if (!string.IsNullOrWhiteSpace(authorizationToken))
        {
            httpRequest.Headers.Add("X-Authorization-Token", authorizationToken);
        }

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowCreditErrorAsync(response, $"Error del servidor ({(int)response.StatusCode})");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        int invoiceNumber = int.Parse(content, System.Globalization.CultureInfo.InvariantCulture);
        WeakReferenceMessenger.Default.Send(new SaleCompletedNotificationMessage(invoiceNumber));
        return invoiceNumber;
    }

    public async Task<CreditPaymentResultClientDto> RegisterCreditPaymentAsync(
        CreditPaymentRequestClientDto request,
        System.Threading.CancellationToken cancellationToken = default)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/sales/credit-payments")
        {
            Content = JsonContent.Create(request)
        };
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            httpRequest.Headers.Add("Idempotency-Key", request.IdempotencyKey);
        }

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowCreditErrorAsync(response, "No se pudo registrar el abono.");
        }

        return await response.Content.ReadFromJsonAsync<CreditPaymentResultClientDto>(cancellationToken: cancellationToken)
            ?? throw new System.Exception("No se pudo leer el resultado del abono.");
    }

    public async Task<CreditCancelClientResultDto> CancelCreditSaleAsync(
        int saleId,
        string? reason,
        System.Threading.CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            $"api/sales/{saleId}/credit-cancel",
            new { Reason = reason },
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowCreditErrorAsync(response, "No se pudo anular la venta a crédito.");
        }

        return await response.Content.ReadFromJsonAsync<CreditCancelClientResultDto>(cancellationToken: cancellationToken)
            ?? throw new System.Exception("No se pudo leer el resultado de la anulación.");
    }

    public async Task<CreditReturnClientResultDto> RegisterCreditReturnAsync(
        int saleId,
        CreditReturnRequestClientDto request,
        System.Threading.CancellationToken cancellationToken = default)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"api/sales/{saleId}/credit-return")
        {
            Content = JsonContent.Create(request)
        };
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            httpRequest.Headers.Add("Idempotency-Key", request.IdempotencyKey);
        }

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowCreditErrorAsync(response, "No se pudo registrar la devolución.");
        }

        return await response.Content.ReadFromJsonAsync<CreditReturnClientResultDto>(cancellationToken: cancellationToken)
            ?? throw new System.Exception("No se pudo leer el resultado de la devolución.");
    }

    public async Task<byte[]?> GetCreditSaleReceiptAsync(
        int saleId,
        System.Threading.CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/sales/{saleId}/credit-receipt", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowCreditErrorAsync(response, "No se pudo generar el comprobante de venta a crédito.");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<byte[]?> GetCreditPaymentReceiptAsync(
        int paymentId,
        System.Threading.CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/sales/credit-payments/{paymentId}/receipt", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowCreditErrorAsync(response, "No se pudo generar el comprobante de abono.");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static async Task ThrowCreditErrorAsync(
        System.Net.Http.HttpResponseMessage response,
        string fallback)
    {
        string body = await response.Content.ReadAsStringAsync();
        string message = ApiErrorParser.FromBody(body, fallback);

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden
            && ApiErrorParser.TryGetAuthorizationRequirement(body, out var authorizationAction))
        {
            if (string.Equals(
                    authorizationAction,
                    nameof(Core.Entities.AuthorizationActionType.CreditOverLimit),
                    System.StringComparison.OrdinalIgnoreCase)
                && TryParseCreditOverLimitContext(body, out var context))
            {
                throw new CreditOverLimitAuthorizationException(message, context!);
            }

            throw new AuthorizationRequiredException(message, authorizationAction);
        }

        throw new System.InvalidOperationException(message);
    }

    /// <summary>
    /// Reconstruye el contexto exacto del 403 de sobregiro. Si el cuerpo no trae todos los campos
    /// del contrato, el llamador degrada al 403 genérico (sin contexto no se puede pedir el token).
    /// </summary>
    private static bool TryParseCreditOverLimitContext(
        string body,
        out CreditOverLimitAuthorizationRequestContext? context)
    {
        context = null;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !TryGetJsonProperty(root, "authorizationContext", out var payload)
                || payload.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetJsonProperty(payload, "saleId", out var saleElement)
                || !saleElement.TryGetInt32(out int saleId)
                || !TryGetJsonProperty(payload, "customerId", out var customerElement)
                || !customerElement.TryGetInt32(out int customerId)
                || !TryGetJsonProperty(payload, "financedUSD", out var financedElement)
                || !financedElement.TryGetDecimal(out decimal financedUsd)
                || !TryGetJsonProperty(payload, "availableCreditUSD", out var availableElement)
                || !availableElement.TryGetDecimal(out decimal availableCreditUsd))
            {
                return false;
            }

            string? customerName = TryGetJsonProperty(payload, "customerName", out var nameElement)
                && nameElement.ValueKind == System.Text.Json.JsonValueKind.String
                    ? nameElement.GetString()
                    : null;

            context = new CreditOverLimitAuthorizationRequestContext
            {
                SaleId = saleId,
                CustomerId = customerId,
                CustomerName = customerName,
                FinancedUSD = financedUsd,
                AvailableCreditUSD = availableCreditUsd,
                Terminal = System.Environment.MachineName
            };
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool TryGetJsonProperty(
        System.Text.Json.JsonElement element,
        string name,
        out System.Text.Json.JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, System.StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}



public class SalesHistoryResponse
{
    public IEnumerable<SaleHistoryDto> Items { get; set; } = new List<SaleHistoryDto>();
    public int TotalCount { get; set; }
}
